using MovieTheater.ArcadeGateway;
using MovieTheater.Core;
using Yarp.ReverseProxy.Forwarder;

// ─────────────────────────────────────────────────────────────────────────────
// ArcadeGateway (arcade-plan.md Appendix C): the public signaling plane for the
// arcade. It runs on the media-server host behind a TLS reverse proxy (Caddy:
// arcade.carpouzis.com) and does exactly one thing — forward an authorized
// browser WebSocket to the CloudRetro coordinator's /ws, which has no auth of its
// own. Everything else 403s. It holds no state: no DB, no sessions.
//
// URL shape: wss://arcade.<host>/w/{token}?room_id=…&zone=…. The token rides the
// path because the stock CloudRetro client force-overwrites the WS path to /ws off
// window.location, so the gateway must (a) validate the signed capability, (b)
// confine a joiner to exactly the room its token names, then (c) REWRITE the path
// to the bare /ws the coordinator serves while preserving the query string.
//
// Media (VP8/Opus + input DataChannels) never touches this process — it flows
// browser ↔ host over WebRTC/UDP. This gateway carries only the signaling WS.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpForwarder();
builder.Services.AddCors();

var app = builder.Build();

var config = app.Configuration;
string secret = config["ArcadeTokenSecret"]
    ?? throw new InvalidOperationException("ArcadeTokenSecret is required.");
string coordinatorBase = (config["CoordinatorBaseUrl"] ?? "http://localhost:8000").TrimEnd('/');
string siteOrigin = config["SiteOrigin"] ?? "https://your-movie-site.example";

// How far past its stamped expiry an in-room CONTROL capability (/w-quick, /w-snap, /w-load) is still
// honoured. The WS connect and the ROM fetch stay strict — see ArcadeCapabilityToken.TryValidate.
//
// A control token names ONE ephemeral CloudRetro room id. That room's own lifetime is the real bound on
// the capability: when it ends, the token authorizes nothing (the id is gone, and /w-quick's parse+match
// rejects any other). Expiring it on a clock protects nothing an attacker couldn't already have had
// while the room was open — and it has now broken SAVING four separate times, because the browser holds
// one token for a multi-hour session while every path that refreshes it is something that can fail
// silently mid-game: presence bookkeeping losing a seat, the reaper closing the room's row under a live
// player, a deploy rolling the site pod, an auth cookie lapsing. WebRTC notices none of those — the game
// plays on perfectly and only the save button dies, which is the worst possible thing to be fragile.
// Saving must depend on the room being ALIVE, not on the rest of the control plane being healthy.
var controlTokenGrace = TimeSpan.FromHours(12);

// CORS for the browser-called REST endpoints (rom-status, w-quick/w-snap/w-load…). The site and the
// gateway are DIFFERENT origins (theater.* → arcade.*), and without these headers the browser split
// the API in half invisibly: a bare POST (w-quick — a CORS "simple request") was still SENT and
// processed but its response was unreadable, so the UI toasted failure over a save that succeeded;
// anything with a JSON body (w-snap, w-load) died in preflight and NEVER arrived (found 2026-07-16,
// "Couldn't save the snapshot"). The WebSocket never noticed — WS is exempt from CORS, so rooms
// played fine while every fetch beside them failed. Scoped to the one trusted site origin.
app.UseCors(p => p.WithOrigins(siteOrigin).AllowAnyHeader().WithMethods("GET", "POST"));

// Optional just-in-time ROM cache (docs/arcade-jit-cache.md). When configured (a manifest + the ROM
// mount path), a game whose ROM isn't pre-staged is extracted from its source archive on demand before
// the connection is forwarded, and LRU-evicted later. Off when unconfigured — the gateway then behaves
// exactly as before (pure signaling proxy).
var romCacheOptions = new MovieTheater.ArcadeGateway.RomCacheOptions();
config.GetSection("RomCache").Bind(romCacheOptions);
MovieTheater.ArcadeGateway.RomCache? romCache = null;
if (romCacheOptions.Enabled)
{
    romCache = new MovieTheater.ArcadeGateway.RomCache(
        romCacheOptions, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RomCache"));
    app.Logger.LogInformation("Arcade JIT ROM cache enabled: {Count} game(s), cap {Cap} bytes.",
        romCache.CatalogCount, romCacheOptions.MaxBytes);
}

// Optional durable, user-scoped save store (docs/arcade-saves-plan.md). When configured (a store dir +
// the /saves mount path), the gateway seeds a chosen save before a game boots and a background sweep
// harvests changed saves back into a per-(user,game) store. Off when unconfigured — CloudRetro's
// room-scoped saves behave exactly as before. Keyed by the deterministic ArcadeSaveId the site mints.
var saveOptions = new MovieTheater.ArcadeGateway.SaveStoreOptions();
config.GetSection("SaveStore").Bind(saveOptions);
MovieTheater.ArcadeGateway.SaveStore? saveStore = null;
// Mirror one save's metadata into the shared app DB (the k8s pod can't read Ziggy's disk but needs the
// rows for the resume/My-Saves UI); gated by the arcade secret. Shared by the harvest sweep AND the
// snapshot endpoint. Success is a 204 SPECIFICALLY — an unmatched /API route mid-deploy returns the
// SPA's 200, which must NOT count as a confirmed write (the sweep would then drop the mirror).
Func<MovieTheater.ArcadeGateway.SaveMeta, Task<bool>> mirrorSave = _ => Task.FromResult(false);
if (saveOptions.Enabled)
{
    saveStore = new MovieTheater.ArcadeGateway.SaveStore(
        saveOptions, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SaveStore"));
    app.Logger.LogInformation("Arcade save store enabled: store={Store} mount={Mount}",
        saveOptions.StoreDir, saveOptions.SavesMountDir);

    var siteClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var saveCallbackUrl = siteOrigin.TrimEnd('/') + "/API/Arcade/Internal/SaveHarvested";
    var appStopping = app.Lifetime.ApplicationStopping;

    mirrorSave = async (m) =>
    {
        try
        {
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, saveCallbackUrl)
            {
                Content = JsonContent.Create(new
                {
                    userId = m.UserId, arcadeGameId = m.GameId, system = m.System, kind = m.Kind,
                    slotId = m.SlotId, label = m.Label, coreName = m.CoreName, coreVersion = m.CoreVersion,
                    storageRelPath = m.StorageRelPath, sizeBytes = m.SizeBytes, sha256 = m.Sha256,
                    source = m.Source, isAutosave = m.IsAutosave,
                }),
            };
            reqMsg.Headers.Add("X-Arcade-Internal-Secret", secret);
            var resp = await siteClient.SendAsync(reqMsg, appStopping);
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return true;
            app.Logger.LogWarning("Save DB mirror got {Status} (not 204) for user {User} game {Game} — will retry",
                (int)resp.StatusCode, m.UserId, m.GameId);
            return false;
        }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Save DB mirror callback failed — will retry"); return false; }
    };

    // Background harvest sweep — copies changed <saveId>.dat/.srm out of the mount continuously, so a
    // save survives even an unclean disconnect (the WS forward ends before CloudRetro's room reap).
    _ = Task.Run(async () =>
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, saveOptions.HarvestDebounceMs * 3));
        while (!appStopping.IsCancellationRequested)
        {
            try { await saveStore.HarvestMountChangesAsync(mirrorSave, appStopping); }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Arcade save harvest sweep error"); }
            try { await Task.Delay(interval, appStopping); } catch { /* shutting down */ }
        }
    });
}

// One pooled client for all forwarding. No cookies, no proxy, no redirects — the
// same posture as StreamGateway; the WS upgrade is carried end-to-end by YARP.
var httpClient = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    ConnectTimeout = TimeSpan.FromSeconds(15),
});

// Delta from StreamGateway (Appendix C1): the signaling socket goes quiet after
// SDP/ICE setup — inputs move to WebRTC DataChannels — so a 100 s activity timeout
// would sever a live room. Disable it and let the room TTL drive lifecycle.
var requestOptions = new ForwarderRequestConfig { ActivityTimeout = Timeout.InfiniteTimeSpan };
var transformer = new WsTransformer();
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();

// Liveness probe for Caddy / monitoring — reveals nothing.
app.MapGet("/healthz", () => Results.Text("ok"));

// Is this game's ROM ready to play, and if not, how far along is preparing it?
//
// A JIT game's first play may have to inflate a compressed disc image (a PSP .cso, a GameCube .gcz —
// hundreds of MB) before any worker can open it. That used to happen INSIDE the WebSocket upgrade, so
// the player watched "Connecting…" and the browser's patience was the de-facto timeout — and when they
// gave up, the abort cancelled the extraction, leaving the next attempt to start from zero. Preparing a
// ROM is a STATE the client is entitled to see, not a race it has to win. The client polls this, shows
// "Preparing…" with progress, and connects when it reads Ready.
//
// Safe to expose: it takes a capability token like every other endpoint here, and returns nothing beyond
// the state of a game the caller was already authorized to launch.
app.MapGet("/rom-status/{token}", (HttpContext ctx, string token) =>
{
    if (!ArcadeCapabilityToken.TryValidate(secret, token, out var payload) || payload is null)
        return Results.Unauthorized();
    if (romCache is null || !romCache.IsManaged(payload.GameId))
        return Results.Json(new { state = "ready", percent = 100 });

    var s = romCache.Status(payload.GameId);
    if (s.State == RomCache.StageState.Absent)
    {
        romCache.BeginMaterialize(payload.GameId); // first ask starts the work
        return Results.Json(new { state = "preparing", percent = 0 });
    }
    return Results.Json(new
    {
        state = s.State.ToString().ToLowerInvariant(),
        percent = s.Percent,
        error = s.Error,
    });
});

app.Map("/w/{token}", async (HttpContext context, string token) =>
{
    if (!ArcadeCapabilityToken.TryValidate(secret, token, out var payload) || payload is null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // Defense-in-depth on top of the coordinator's own Origin check: if a browser
    // presents an Origin, it must be the site. Non-browser clients (test scripts)
    // send none and are allowed through — the token is the real gate.
    var origin = context.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, siteOrigin, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // Confine the capability to its room. A joiner token carries the bound CloudRetro
    // room id; the connect query's room_id must match it exactly (this is the arcade
    // analog of StreamGateway confining /Videos/{itemId} to the token's item). A
    // creator token carries an empty room id and must connect with an empty room_id
    // (empty ⇒ "create a room on a free worker"). Both sides are the decoded value.
    var requestedRoomId = context.Request.Query["room_id"].ToString();
    if (!string.Equals(requestedRoomId, payload.CloudRetroRoomId ?? string.Empty, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // Save seed (docs/arcade-saves-plan.md): if this is the OWNER's deterministic save id and the room
    // hasn't booted yet, restore the chosen slot into the mount so CloudRetro auto-loads it at GAME_START.
    // Only the owner seeds — the id encodes the creator's (user, game); a joiner's token userId won't match,
    // so guests never re-seed the live room. A "New game" carries ?fresh=1 → we CLEAR the mount instead so
    // the game boots clean (unsigned is safe: it only ever clears the owner's own save). Harvest is handled
    // continuously by the background sweep.
    if (saveStore != null
        && ArcadeSaveId.TryParse(requestedRoomId, out var svUser, out var svGame, out var svSlot, out var svSystem, out _)
        && svUser == payload.UserId && svGame == payload.GameId
        // Capture rooms (heavy browser lane) ride HeavyVault at prepare/finish, NOT the CloudRetro
        // save mount. Skipping keeps a heavy dir-zip from ever being seeded into the .dat/.srm mount
        // (the Snowboard-Kids wedge class — plan §5.2 / trap #8).
        && !string.Equals(svSystem, "capture", StringComparison.Ordinal))
    {
        // Harvest-on-reconnect (the close-save race fix): the background sweep can lose a foot-race
        // with a quick End→relaunch — the worker's close-time save lands on the mount AFTER the
        // sweep's last pass, so the vault (and the site's My-Saves rows) stay stale. The owner
        // reconnecting is the one moment freshness matters; harvest this session's mount files NOW,
        // before deciding whether the mount needs seeding.
        try
        {
            foreach (var meta in await saveStore.HarvestSessionAsync(svUser, svGame, svSystem, requestedRoomId, false, context.RequestAborted))
                await mirrorSave(meta);
        }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Arcade harvest-on-reconnect failed for {Id}", requestedRoomId); }

        var fresh = context.Request.Query["fresh"].ToString();
        bool newGame = fresh == "1" || string.Equals(fresh, "true", StringComparison.OrdinalIgnoreCase);
        // Competitive room (?competitive=1): boot WITHOUT auto-restoring the save-state (so a run starts
        // clean and RA hardcore stays valid), exactly like a New game — clear the mount state, keep the
        // battery/card. Also MARK the session so harvest never vaults its state over the casual Continue.
        // Set/clear unconditionally: a later CASUAL boot of the same deterministic id clears the mark.
        var competitiveQ = context.Request.Query["competitive"].ToString();
        bool competitive = competitiveQ == "1" || string.Equals(competitiveQ, "true", StringComparison.OrdinalIgnoreCase);
        saveStore.SetCompetitive(requestedRoomId, competitive);
        // Resume-from-snapshot: ?seedslot=N seeds a chosen snapshot slot's bytes into the (slot-0) room,
        // so the player continues FROM snapshot N. Defaults to the id's slot (0 = the auto Continue slot).
        bool seedSlotAsked = int.TryParse(context.Request.Query["seedslot"].ToString(), out var ss);
        int seedSlot = seedSlotAsked ? ss : svSlot;
        // "Resume THIS save" is an explicit choice by the player, and it has to beat the don't-stomp guard
        // below — which it did not, so picking ANY snapshot silently booted the leftover mount state
        // instead, i.e. every save in the vault loaded "the exact same spot" (2026-07-26, Mario BAZR).
        // The mount keeps a room's .dat/.srm after the room ends (the deterministic id reuses the same
        // filenames), so MountHasSave is TRUE on every replay of a game once it has been played once —
        // which made the whole vault unloadable for that game, not just occasionally wrong. Safe now
        // because the harvest-on-reconnect above has already vaulted whatever the mount was holding.
        bool chosenSeed = seedSlotAsked && ss > 0;
        try
        {
            if (newGame || competitive)
            {
                // The clear must be UNCONDITIONAL. It used to sit behind the !MountHasSave guard below,
                // which inverted it: the one time "New game" has stale mount files to remove is exactly
                // when MountHasSave is true — so the clear never ran, and the deterministic id booted
                // the leftover .dat anyway (a wedged state then resurrected on every "New game";
                // Snowboard Kids, 2026-07-10). Harmless if a room is somehow live: CloudRetro only
                // reads these files at boot, and its close-save rewrites them.
                saveStore.ClearSession(requestedRoomId);
                saveStore.ClearCoreSaveDir(requestedRoomId); // PSP/DC/… save-dir tree, if any
                // A New game skips the auto-restored save-STATE, but must NOT eject the memory card /
                // battery: re-seed SAVE_RAM so the player's PS1 card (and NES/SNES/GBA/N64 battery) stays
                // inserted — exactly as starting a new game on real hardware leaves the card untouched.
                // ClearSession above removed the mount .srm; this puts the vault copy back. Without it a
                // New game booted a BLANK card, so card-only titles (SotN) showed "no saved game" and a
                // subsequent in-game save could overwrite the vault with a near-empty card. The worker-side
                // cardVault (ps2/gc/wii/dc/psp) is a separate path the worker seeds regardless of ?fresh,
                // so those systems were already unaffected; only SAVE_RAM needed this.
                bool keptCard = saveStore.SeedSramOnly(svUser, svGame, requestedRoomId);
                app.Logger.LogInformation("Arcade save cleared ({Reason}){Card} for user {User} game {Game}",
                    competitive ? "competitive" : "New game", keptCard ? " +card kept" : "", svUser, svGame);
            }
            else if (chosenSeed || !saveStore.MountHasSave(requestedRoomId))
            {
                // Without a chosen slot: mount files present = the room is live (or was just harvested
                // above) — don't stomp them, since a harvest that failed would otherwise cost the player
                // newer state than the vault holds. With one: see chosenSeed.
                // svSystem carries the CORE ("n64-parallel_n64" vs "n64"), and a save-state only restores on
                // the core that wrote it — so the seed is withheld on a mismatch instead of overwriting the
                // mount with a blob this core can't read.
                bool seeded = saveStore.SeedSession(svUser, svGame, requestedRoomId, seedSlot, svSystem, out var wrongCore);
                // Save-dir cores (PSP memstick / DC-Naomi VMU / DOS) don't ride SAVE_RAM — restore their tree too.
                bool seededCore = saveStore.SeedCoreSaveDir(svUser, svGame, requestedRoomId);
                app.Logger.LogInformation("Arcade save {Action}{Core} for user {User} game {Game} slot {Slot}{Chosen}",
                    wrongCore ? "state withheld (saved on another core)" : seeded ? "seeded" : "none (fresh)",
                    seededCore ? "+coredir" : "", svUser, svGame, seedSlot,
                    chosenSeed ? " (chosen resume)" : "");
            }

            // THE CARD IS NOT PART OF THAT DECISION. Everything above is about the save-STATE, which is
            // one core's memory dump and rightly stays where it was written. A battery/memory card is the
            // GAME's own data, reads identically on any core, and is the whole reason you can switch cores
            // and keep playing the same file — so it follows the player unconditionally, and it is the ONE
            // save that must converge rather than fork. Runs on every branch, including the ones above that
            // seeded nothing: the don't-stomp guard is right about states and wrong about cards.
            if (saveStore.SeedSramIfNewer(svUser, svGame, requestedRoomId))
                app.Logger.LogInformation("Arcade card refreshed from vault for user {User} game {Game}", svUser, svGame);
        }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Arcade save seed/clear failed for {Id}", requestedRoomId); }
    }

    // JIT: materialize the ROM before forwarding so the worker can launch it (the scan-on-miss patch
    // then makes CloudRetro see the just-extracted file). A managed game is pinned for the life of the
    // connection so eviction can't pull it mid-session. Non-managed games skip all of this.
    if (romCache != null && romCache.IsManaged(payload.GameId))
    {
        try
        {
            // WaitMaterialized, not EnsureMaterialized(RequestAborted): the WORK must not belong to this
            // request. A player who gives up on a slow first-play used to CANCEL the extraction — so the
            // next attempt restarted from zero and could never finish either. Now the job outlives them,
            // and /Arcade/RomStatus lets the client show "Preparing…" instead of guessing at a timeout.
            await romCache.WaitMaterializedAsync(payload.GameId, context.RequestAborted);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "RomCache failed to materialize game {GameId}", payload.GameId);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        romCache.Pin(payload.GameId);
        try { await forwarder.SendAsync(context, coordinatorBase, httpClient, requestOptions, transformer); }
        finally { romCache.Unpin(payload.GameId); }
        return;
    }

    await forwarder.SendAsync(context, coordinatorBase, httpClient, requestOptions, transformer);
});

// In-room "Save snapshot" (arcade-saves-plan S3): copy the live save into a NEW numbered slot with a
// label. The client flushes a SAVE (t=106) first, then POSTs here with its capability token. The slot is
// written into the CALLER's vault (p.UserId), not the room owner's: the emulator world is shared, but
// every player's saves are their own — a guest snapshotting a co-op session keeps their own copy and can
// resume it solo later, and the owner's slots are never written by someone else's click. (The room id
// still names the OWNER's mount — that's the shared world being copied, and that part is correct.)
// Best-effort DB mirror so the slot shows in the resume/My-Saves lists.
app.MapPost("/w-snap/{token}", async (HttpContext ctx, string token) =>
{
    if (saveStore == null) return Results.NotFound();
    if (!ArcadeCapabilityToken.TryValidate(secret, token, controlTokenGrace, out var p) || p is null)
        // NOT Results.Forbid(): this gateway registers no authentication scheme, so ForbidAsync throws and
        // the endpoint 500s — and a 500 from an unhandled exception carries no Access-Control-Allow-Origin,
        // so the browser reports a rejected token as a bogus "blocked by CORS policy" error (the WS is
        // CORS-exempt, so the room plays on while every save beside it appears to fail on CORS). Return a
        // plain 403 that flows through the CORS middleware so the client can READ it and show a real message.
        // With controlTokenGrace this is now reachable only by a forged/ancient token, not by a long session.
        return Results.Json(new { ok = false, reason = "This room pass isn't valid — reload the room page to keep saving." },
            statusCode: StatusCodes.Status403Forbidden);
    var id = p.CloudRetroRoomId ?? "";
    if (!ArcadeSaveId.TryParse(id, out _, out var g, out _, out var sys, out _) || g != p.GameId)
        return Results.BadRequest();

    string? label = null;
    try
    {
        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string?>>(ctx.RequestAborted);
        body?.TryGetValue("label", out label);
    }
    catch { /* no/invalid body → unnamed snapshot */ }

    SaveMeta? meta;
    try
    {
        meta = await saveStore.SnapshotCurrentAsync(p.UserId, g, sys, id,
            string.IsNullOrWhiteSpace(label) ? null : label!.Trim(), ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Arcade snapshot failed for user {User} game {Game}", p.UserId, g);
        return Results.Json(new { ok = false, reason = "save file busy — wait a moment, then try again" });
    }
    if (meta == null) return Results.Json(new { ok = false, reason = "no live save yet — play a moment, then snapshot" });
    await mirrorSave(meta);
    app.Logger.LogInformation("Arcade snapshot slot {Slot} for user {User} game {Game}", meta.SlotId, p.UserId, g);
    return Results.Json(new { ok = true, slot = meta.SlotId, label = meta.Label });
});

// In-room "Save" = QUICKSAVE: copy the live save into the reserved quicksave slot, replacing the last
// one. Same flush-then-copy path as /w-snap, but a fixed slot and an auto label, so pressing Save is
// one click. It deliberately does NOT write slot 0: that slot belongs to autosave/save-on-quit, which
// would overwrite a player's save on the way out of the room (SaveStore.QuickSlot).
app.MapPost("/w-quick/{token}", async (HttpContext ctx, string token) =>
{
    if (saveStore == null) return Results.NotFound();
    if (!ArcadeCapabilityToken.TryValidate(secret, token, controlTokenGrace, out var p) || p is null)
        // See /w-snap for why this is a hand-rolled 403 rather than Results.Forbid().
        return Results.Json(new { ok = false, reason = "This room pass isn't valid — reload the room page to keep saving." },
            statusCode: StatusCodes.Status403Forbidden);
    var id = p.CloudRetroRoomId ?? "";
    // Every seated player may quicksave (the shared world is what's being copied), but the slot lands in
    // the CALLER's vault (p.UserId), not the room owner's — matching the button's own promise ("keeps
    // YOUR place") and the site's per-user save lists. It used to land in the owner's slot 99, which let
    // any (even ex-) player of a room overwrite the owner's deliberate quicksave; keying the write to the
    // token's holder makes that impossible by construction — a token can only ever write its own vault.
    if (!ArcadeSaveId.TryParse(id, out _, out var g, out _, out var sys, out _) || g != p.GameId)
        return Results.BadRequest();

    var label = $"Quicksave {DateTime.Now:h:mm tt}";
    SaveMeta? meta;
    try
    {
        meta = await saveStore.SnapshotToSlotAsync(
            p.UserId, g, sys, id, MovieTheater.ArcadeGateway.SaveStore.QuickSlot, label, ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Arcade quicksave failed for user {User} game {Game}", p.UserId, g);
        return Results.Json(new { ok = false, reason = "save file busy — wait a moment, then try again" });
    }
    if (meta == null) return Results.Json(new { ok = false, reason = "no live save yet — play a moment, then save" });
    await mirrorSave(meta);
    app.Logger.LogInformation("Arcade quicksave for user {User} game {Game}", p.UserId, g);
    return Results.Json(new { ok = true, slot = meta.SlotId, label = meta.Label });
});

// In-room LOAD a snapshot WITHOUT restarting (arcade-saves-plan S3): swap the chosen slot's bytes into
// the live mount, then the client sends t=107 to make the core restore it. Owner-only.
app.MapPost("/w-load/{token}", async (HttpContext ctx, string token) =>
{
    if (saveStore == null) return Results.NotFound();
    if (!ArcadeCapabilityToken.TryValidate(secret, token, controlTokenGrace, out var p) || p is null)
        // See /w-snap for why this is a hand-rolled 403 rather than Results.Forbid().
        return Results.Json(new { ok = false, reason = "This room pass isn't valid — reload the room page to keep saving." },
            statusCode: StatusCodes.Status403Forbidden);
    var id = p.CloudRetroRoomId ?? "";
    // Any player of this room may load — the emulator, and so the world, is shared. But the slot is read
    // from the CALLER's vault (p.UserId), mirroring /w-quick: Load restores YOUR quicksave/snapshot, and
    // the in-room picker lists the caller's saves (ListSaves is caller-scoped), so vault and list finally
    // agree for guests — they used to see their own list and load the owner's slots.
    if (!ArcadeSaveId.TryParse(id, out _, out var g, out _, out var lsys, out _) || g != p.GameId)
        return Results.BadRequest();
    int slot = 0;
    try { var b = await ctx.Request.ReadFromJsonAsync<Dictionary<string, int>>(ctx.RequestAborted); b?.TryGetValue("slot", out slot); } catch { }

    // A state restores only on the core that wrote it, and this load goes straight into the LIVE room —
    // handing the running core another core's memory dump is how you hard-wedge a session someone is
    // playing. The room-create path re-launches on the save's own core; here there is no relaunch, so
    // say why instead. (Nothing to do about it in-room: the fix is to resume it from the lobby.)
    var savedOn = saveStore.StateSystem(p.UserId, g, slot);
    if (!string.IsNullOrEmpty(savedOn) && !string.Equals(savedOn, lsys, StringComparison.Ordinal))
        return Results.Json(new { ok = false, reason = "That save was made on a different core — resume it from the game's page and it'll launch the right one." });

    return Results.Json(new { ok = saveStore.LoadSlotToMount(p.UserId, g, id, slot) });
});

// Internal, secret-gated blob ops the SITE calls for My-Saves management + import/export (the k8s pod
// can't touch Ziggy's disk). Blobs stay on Ziggy; the site owns auth + the DB row.
bool InternalAuth(HttpContext c) => !string.IsNullOrEmpty(secret) &&
    string.Equals(c.Request.Headers["X-Arcade-Internal-Secret"].ToString(), secret, StringComparison.Ordinal);

// ── Heavy lane (docs/arcade-heavy-lane-plan.md §7): Moonlight/Apollo-streamed emulators ─────────
// The gateway is the heavy lane's Ziggy-side control plane: descriptor registry, the one-session
// lock, the pre-staged big-title cache, and the only channel to Apollo's admin API. Everything is
// behind the same internal secret — the SITE proxies what browsers need (with its own auth/age
// gates), and heavy-launch.ps1 calls prepare/attach/finish from this same machine.
var heavyOptions = new MovieTheater.ArcadeGateway.HeavyOptions();
config.GetSection("Heavy").Bind(heavyOptions);
if (heavyOptions.Enabled)
{
    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
    var heavyRegistry = new MovieTheater.ArcadeGateway.HeavyAppRegistry(
        heavyOptions.AppsDir!, loggerFactory.CreateLogger("HeavyApps"));
    var heavyLock = new MovieTheater.ArcadeGateway.HeavyLock(heavyOptions.StaleLockMinutes);
    MovieTheater.ArcadeGateway.HeavyStager? heavyStager = null;
    if (!string.IsNullOrWhiteSpace(heavyOptions.CacheDir))
        heavyStager = new MovieTheater.ArcadeGateway.HeavyStager(
            heavyOptions.CacheDir!, heavyOptions.CacheMaxBytes, heavyOptions.ChunkBytes, loggerFactory.CreateLogger("HeavyStager"));
    var apollo = new MovieTheater.ArcadeGateway.ApolloAdmin(heavyOptions, loggerFactory.CreateLogger("Apollo"));
    // Per-user dir-zip saves (plan §8) — rides the SAME store as the CloudRetro vault, so it needs
    // the SaveStore section configured. Absent = heavy sessions play machine-local saves (v0).
    MovieTheater.ArcadeGateway.HeavyVault? heavyVault = saveOptions.Enabled
        ? new MovieTheater.ArcadeGateway.HeavyVault(saveOptions.StoreDir!, loggerFactory.CreateLogger("HeavyVault"))
        : null;
    app.Logger.LogInformation("Heavy lane enabled: {Count} descriptor(s), cache={Cache}, vault={Vault}",
        heavyRegistry.All().Count, heavyOptions.CacheDir ?? "(none)", heavyVault != null ? "on" : "off");

    // The paired Moonlight device name → site user mapping lives in the app DB (HeavyClient, owned
    // by the site's pairing flow); the gateway asks the site. Best-effort with a short timeout —
    // an unmapped/unreachable answer means "no vault ops", never a blocked launch.
    var resolveClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    async Task<int?> ResolveHeavyUserAsync(string? clientName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientName)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                siteOrigin.TrimEnd('/') + "/API/Arcade/Internal/ResolveHeavyClient")
            { Content = JsonContent.Create(new { clientName }) };
            req.Headers.Add("X-Arcade-Internal-Secret", secret);
            var resp = await resolveClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, int?>>(ct);
            return body?.GetValueOrDefault("userId");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Heavy client resolve failed for {Name}", clientName);
            return null;
        }
    }

    // Resolve an app by descriptor id OR its ArcadeGame row id (the site speaks gameId).
    MovieTheater.ArcadeGateway.HeavyApp? ResolveApp(string idOrGameId) =>
        heavyRegistry.Get(idOrGameId)
        ?? (int.TryParse(idOrGameId, out var gid) ? heavyRegistry.GetByArcadeGameId(gid) : null);

    // Lane + staging status in one call — what the lobby needs for every heavy card.
    app.MapGet("/heavy/status", (HttpContext ctx) =>
    {
        if (!InternalAuth(ctx)) return Results.Unauthorized();
        var held = heavyLock.Current();
        var heldTitle = held != null ? heavyRegistry.Get(held.AppId)?.Title ?? held.AppId : null;
        var apps = heavyRegistry.All().Select(a => new
        {
            id = a.Id,
            title = a.Title,
            system = a.System,
            arcadeGameId = a.ArcadeGameId,
            enabled = a.Enabled,
            staging = heavyStager != null ? heavyStager.Progress(a) : new { state = "local" } as object,
        });
        return Results.Json(new
        {
            locked = held != null,
            appId = held?.AppId,
            title = heldTitle,
            clientName = held?.ClientName,
            sinceUtc = held?.SinceUtc,
            apps,
        });
    });

    // Advance one staging chunk (the caller — card UI or an admin loop — drives to completion).
    app.MapPost("/heavy/stage/{appId}", (HttpContext ctx, string appId) =>
    {
        if (!InternalAuth(ctx)) return Results.Unauthorized();
        if (heavyStager == null) return Results.Json(new { state = "error", error = "No heavy cache configured." });
        var a = ResolveApp(appId);
        return a == null ? Results.NotFound() : Results.Json(heavyStager.Advance(a));
    });

    // Complete a Moonlight pairing PIN (site-proxied; the site records the device→user mapping).
    app.MapPost("/heavy/pair", async (HttpContext ctx) =>
    {
        if (!InternalAuth(ctx)) return Results.Unauthorized();
        var r = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string?>>(ctx.RequestAborted);
        var pin = r?.GetValueOrDefault("pin")?.Trim();
        var name = r?.GetValueOrDefault("name")?.Trim();
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(name)) return Results.BadRequest();
        var (ok, detail) = await apollo.PairAsync(pin, name, ctx.RequestAborted);
        return Results.Json(new { ok, detail });
    });

    // Compile descriptors → Apollo's app list. Dry-run unless ?apply=1 (upsert-only, never deletes).
    app.MapPost("/heavy/sync-apps", async (HttpContext ctx) =>
    {
        if (!InternalAuth(ctx)) return Results.Unauthorized();
        bool apply = ctx.Request.Query["apply"].ToString() == "1";
        return Results.Json(await apollo.SyncAppsAsync(heavyRegistry, heavyStager, heavyOptions, apply, ctx.RequestAborted));
    });

    // An Artemis .art launch shortcut for one app (plan §7.5 upgraded): tapping the file on a
    // PAIRED Android device jumps straight into the stream — the site card serves it so "play"
    // is one tap instead of navigating the client's grid. Text is built fresh per request so it
    // always carries Apollo's current uuids.
    app.MapGet("/heavy/shortcut/{appId}", async (HttpContext ctx, string appId) =>
    {
        if (!InternalAuth(ctx)) return Results.Unauthorized();
        var a = ResolveApp(appId);
        if (a == null) return Results.NotFound();
        var snap = await apollo.GetAppsSnapshotAsync(ctx.RequestAborted);
        if (snap == null || string.IsNullOrEmpty(snap.HostUuid))
            return Results.Json(new { error = "Apollo is unreachable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        var appObj = snap.Apps.FirstOrDefault(o =>
            string.Equals((string?)o["name"], a.Title, StringComparison.OrdinalIgnoreCase));
        var appUuid = (string?)appObj?["uuid"];
        if (appObj == null || string.IsNullOrEmpty(appUuid))
            return Results.Json(new { error = "App is not synced to Apollo yet — run sync-apps." }, statusCode: StatusCodes.Status404NotFound);
        return Results.Text(
            MovieTheater.ArcadeGateway.ApolloAdmin.BuildArtShortcut(snap.HostUuid!, snap.HostName ?? "Ziggy", appUuid!, a.Title),
            "text/plain");
    });

    // ── The heavy-launch.ps1 contract (plan §4): prepare → attach → finish ──────────────────────
    app.MapPost("/heavy/prepare/{appId}", async (HttpContext ctx, string appId) =>
    {
        if (!InternalAuth(ctx)) return Results.Unauthorized();
        var a = ResolveApp(appId);
        if (a == null) return Results.NotFound();
        // Prepare NEVER stages (plan trap #8: the play click must not become a 40 GB copy) — the
        // card's Prepare flow exists exactly so the ROM is already local here.
        if (a.NeedsStaging && (heavyStager == null || !heavyStager.IsStaged(a)))
            return Results.Json(new { ok = false, error = "Title is not staged — Prepare it from its arcade card first." },
                statusCode: StatusCodes.Status409Conflict);
        var clientName = ctx.Request.Query["client"].ToString();
        if (!heavyLock.TryAcquire(a.Id, string.IsNullOrEmpty(clientName) ? null : clientName, out var holder))
        {
            var t = heavyRegistry.Get(holder.AppId)?.Title ?? holder.AppId;
            return Results.Json(new { ok = false, error = $"The heavy lane is in use: {t}.", appId = holder.AppId, sinceUtc = holder.SinceUtc },
                statusCode: StatusCodes.Status409Conflict);
        }

        // Save seed (plan §8): device → site user → restore their Continue save into the emulator's
        // save dir, displacing (never deleting) whatever is live. Unmapped device / no vault entry
        // = leave the machine-local save as-is. A seed failure is LOGGED but doesn't block play —
        // the displaced content is always recoverable from the store's _displaced graveyard.
        if (heavyVault != null)
        {
            // Capture lane (H5): the room is site-authenticated and the owner is baked into the
            // deterministic room id, so the worker passes ?userId= directly and we skip the
            // HeavyClient device lookup. This endpoint is InternalAuth-gated, so trusting the param
            // is safe. Apollo/Artemis (no param) still resolves the user from its client name.
            int? userId;
            if (int.TryParse(ctx.Request.Query["userId"].ToString(), out var directUid) && directUid > 0)
                userId = directUid;
            else
                userId = await ResolveHeavyUserAsync(clientName, ctx.RequestAborted);
            if (userId is int uid)
            {
                heavyLock.SetUser(a.Id, uid); // finish() harvests for this user
                try { heavyVault.Seed(a, uid); }
                catch (Exception ex) { app.Logger.LogError(ex, "Heavy save seed failed for {App} user {User}", a.Id, uid); }
            }
        }

        string rom = a.NeedsStaging && heavyStager != null ? heavyStager.TargetPathFor(a) : "";
        string args = (a.ArgsTemplate ?? "").Replace("{rom}", rom);
        app.Logger.LogInformation("Heavy prepare: {App} (client {Client})", a.Id, clientName);
        return Results.Json(new { ok = true, appId = a.Id, exe = a.Exe, args, workingDir = a.WorkingDir ?? "", rom });
    });

    app.MapPost("/heavy/attach/{appId}", async (HttpContext ctx, string appId) =>
    {
        if (!InternalAuth(ctx)) return Results.Unauthorized();
        var r = await ctx.Request.ReadFromJsonAsync<Dictionary<string, int>>(ctx.RequestAborted);
        int pid = r?.GetValueOrDefault("pid") ?? 0;
        var a = ResolveApp(appId);
        if (a == null || pid <= 0) return Results.BadRequest();
        return Results.Json(new { ok = heavyLock.Attach(a.Id, pid) });
    });

    app.MapPost("/heavy/finish/{appId}", async (HttpContext ctx, string appId) =>
    {
        if (!InternalAuth(ctx)) return Results.Unauthorized();
        var a = ResolveApp(appId);
        if (a == null) return Results.Json(new { ok = false });

        // Capture the session owner BEFORE releasing: finish is called twice by design (the launch
        // script's exit path AND Apollo's undo prep-cmd) — only the call that actually releases the
        // lock harvests, so the save is zipped exactly once per session.
        var held = heavyLock.Current();
        int? ownerId = held != null && string.Equals(held.AppId, a.Id, StringComparison.OrdinalIgnoreCase) ? held.UserId : null;
        bool released = heavyLock.Release(a.Id);
        if (released)
        {
            app.Logger.LogInformation("Heavy finish: {App}", a.Id);
            // Harvest (plan §8): the emulator flushed its save on exit; vault it if it changed and
            // mirror the row so My Saves / the Deck bridge can see it.
            if (heavyVault != null && ownerId is int uid)
            {
                try
                {
                    var meta = heavyVault.Harvest(a, uid);
                    if (meta != null) await mirrorSave(meta);
                }
                catch (Exception ex) { app.Logger.LogError(ex, "Heavy save harvest failed for {App} user {User}", a.Id, uid); }
            }
        }
        return Results.Json(new { ok = released });
    });
}

app.MapPost("/internal/save-delete", async (HttpContext ctx) =>
{
    if (saveStore == null) return Results.NotFound();
    if (!InternalAuth(ctx)) return Results.Unauthorized();
    var r = await ctx.Request.ReadFromJsonAsync<MovieTheater.ArcadeGateway.SaveOpReq>(ctx.RequestAborted);
    if (r == null) return Results.BadRequest();
    saveStore.DeleteSave(r.UserId, r.GameId, r.Kind ?? "state", r.Slot);
    return Results.NoContent();
});

app.MapPost("/internal/save-relabel", async (HttpContext ctx) =>
{
    if (saveStore == null) return Results.NotFound();
    if (!InternalAuth(ctx)) return Results.Unauthorized();
    var r = await ctx.Request.ReadFromJsonAsync<MovieTheater.ArcadeGateway.SaveOpReq>(ctx.RequestAborted);
    if (r == null) return Results.BadRequest();
    saveStore.RelabelSave(r.UserId, r.GameId, r.Kind ?? "state", r.Slot, r.Label);
    return Results.NoContent();
});

app.MapPost("/internal/save-read", async (HttpContext ctx) =>
{
    if (saveStore == null) return Results.NotFound();
    if (!InternalAuth(ctx)) return Results.Unauthorized();
    var r = await ctx.Request.ReadFromJsonAsync<MovieTheater.ArcadeGateway.SaveOpReq>(ctx.RequestAborted);
    if (r == null) return Results.BadRequest();
    var bytes = await saveStore.ReadSaveAsync(r.UserId, r.GameId, r.Kind ?? "state", r.Slot, ctx.RequestAborted);
    return bytes == null ? Results.NotFound() : Results.Bytes(bytes, "application/octet-stream");
});

app.MapPost("/internal/save-import", async (HttpContext ctx) =>
{
    if (saveStore == null) return Results.NotFound();
    if (!InternalAuth(ctx)) return Results.Unauthorized();
    var r = await ctx.Request.ReadFromJsonAsync<MovieTheater.ArcadeGateway.SaveImportReq>(ctx.RequestAborted);
    if (r == null || string.IsNullOrEmpty(r.DataBase64)) return Results.BadRequest();
    byte[] bytes;
    try { bytes = Convert.FromBase64String(r.DataBase64); } catch { return Results.BadRequest(); }
    // sram + dirzip (heavy lane) are single canonical slots; only state snapshots get numbered.
    int slot = (r.Kind is "sram" or "dirzip") ? 0 : (r.Slot > 0 ? r.Slot : saveStore.NextSnapshotSlot(r.UserId, r.GameId));
    var meta = await saveStore.ImportSaveAsync(r.UserId, r.GameId, r.System ?? "", r.Kind ?? "state", slot, r.Label, bytes, ctx.RequestAborted);
    await mirrorSave(meta);
    return Results.Json(new { ok = true, slot = meta.SlotId, kind = meta.Kind, sizeBytes = meta.SizeBytes });
});

// ── Console reattach: the EVENT-DRIVEN half of the remote-desktop warning ────────────────────────
// The arcade renders into a real interactive Windows session. With a remote desktop attached (or
// left DISCONNECTED after someone closes theirs) that session runs at ~32 Hz instead of 60, so every
// room is choppy for reasons no log reports. The recovery is `tscon <id> /dest:console`, which needs
// SeTcbPrivilege — hence the elevated on-demand task rather than a call from here.
//
// WHO CALLS THIS: the SITE, the moment its last live room ends while the host is reported degraded.
// The alternative was waiting for Ziggy's watchdog to notice on its next 30 s cycle, which is a long
// time to sit choppy after the thing blocking the fix (a live room) has gone away. The watchdog is
// still the backstop — this only makes the common case immediate.
//
// WHY THE COORDINATOR IS RE-CHECKED HERE: the site's room list is its own in-memory bookkeeping, and
// the emulator is the coordinator's. They disagree briefly at exactly the moment this fires (the
// site's room ends before the worker finishes tearing down), and moving a session out from under a
// LIVE room is unproven. The coordinator is localhost to us and is the authority, so the decision is
// made on this side of the wire, not by the caller. "I could not ask" is never "nothing is playing".
{
    var reattachGate = new SemaphoreSlim(1, 1);
    var lastReattachUtc = DateTime.MinValue;
    // Own client: short timeout, localhost only. The coordinator answering slowly must not hold the
    // site's call open — a missed reattach costs 30 s (the watchdog picks it up), a hung one costs more.
    var coordClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

    app.MapPost("/internal/reattach-console", async (HttpContext ctx) =>
    {
        if (!InternalAuth(ctx)) return Results.Unauthorized();

        await reattachGate.WaitAsync(ctx.RequestAborted);
        try
        {
            // A floor, not a policy: several rooms can end within a second of each other, and a tscon
            // that cannot succeed (reattach-console.ps1 refuses when several sessions are disconnected)
            // must not be re-run per event. The watchdog retries on its own schedule regardless.
            var since = DateTime.UtcNow - lastReattachUtc;
            if (since < TimeSpan.FromSeconds(60))
                return Results.Json(new { ran = false, reason = "throttled", secondsAgo = (int)since.TotalSeconds });

            // Authority check: any worker holding a room means something is still playing.
            try
            {
                using var sresp = await coordClient.GetAsync(coordinatorBase + "/status", ctx.RequestAborted);
                if (!sresp.IsSuccessStatusCode)
                    return Results.Json(new { ran = false, reason = "coordinator-status-unavailable" });
                var doc = await sresp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ctx.RequestAborted);
                int busy = 0;
                if (doc.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var w in doc.EnumerateArray())
                        if (w.TryGetProperty("room", out var room) &&
                            room.ValueKind == System.Text.Json.JsonValueKind.String &&
                            !string.IsNullOrEmpty(room.GetString())) busy++;
                if (busy > 0)
                    return Results.Json(new { ran = false, reason = "rooms-live", rooms = busy });
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "reattach-console: could not read the coordinator's status — NOT reattaching");
                return Results.Json(new { ran = false, reason = "coordinator-status-unavailable" });
            }

            // The task itself has the last word: reattach-console.ps1 never moves an ACTIVE session,
            // so a run while somebody is genuinely attached is a logged no-op, not a kick.
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe")
                {
                    Arguments = "/Run /TN \"MovieTheater - Reattach Console\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return Results.Json(new { ran = false, reason = "spawn-failed" });
                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(ctx.RequestAborted);
                lastReattachUtc = DateTime.UtcNow;
                app.Logger.LogInformation("reattach-console: triggered (exit {Exit}) {Out}{Err}",
                    proc.ExitCode, stdout.Trim(), stderr.Trim());
                // exit != 0 is reported, not hidden: the task may be unregistered on this box, and a
                // recovery that never ran must not answer "ran".
                return Results.Json(new { ran = proc.ExitCode == 0, exitCode = proc.ExitCode });
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "reattach-console: could not trigger the scheduled task");
                return Results.Json(new { ran = false, reason = "trigger-failed" });
            }
        }
        finally { reattachGate.Release(); }
    });
}

app.Run();

/// <summary>
/// Rewrites the proxied request: swaps the /w/{token} path for the bare /ws the
/// CloudRetro coordinator serves (its client and server both hardcode /ws, so a
/// prefix-preserving proxy 404s), re-appends the original query string (room_id,
/// zone), and clears Host so the upstream sees its own. No API key to inject —
/// unlike StreamGateway, CloudRetro has no auth surface behind the gate.
/// </summary>
sealed class WsTransformer : HttpTransformer
{
    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        var query = httpContext.Request.QueryString.Value ?? string.Empty;
        // Zone routing (capture lane, H5 — docs/arcade-capture-worker-plan.md). The worker pool is split
        // by zone: retro/GL cores run on the "main" workers, the capture mod runs on the "capture" worker.
        // We DERIVE the zone from the room id's system (every room id the site mints is
        // sv-…-<system>___<gameKey>): a capture room (system "capture") → zone "capture"; everything else,
        // including GL rooms that still carry zone=gl for the retired two-pool routing, and legacy/random
        // ids that don't parse → zone "main". This is independent of whatever zone the site tagged.
        // ⚠ This REPLACES the old blanket strip. A stripped/empty zone matched ANY worker via Worker.In("")
        // — once a capture worker joins the coordinator, that would let a retro room land on it and fail
        // its FindAppByName (plan trap #7). Deriving a concrete zone here closes that off at the gateway,
        // with no dependency on a matching site deploy.
        var roomIdForZone = httpContext.Request.Query["room_id"].ToString();
        string zone = "main";
        if (ArcadeSaveId.TryParse(roomIdForZone, out _, out _, out _, out var zoneSystem, out _)
            && string.Equals(zoneSystem, "capture", StringComparison.Ordinal))
            zone = "capture";
        query = System.Text.RegularExpressions.Regex.Replace(query, @"[?&]zone=[^&]*", "");
        if (query.StartsWith("&")) query = "?" + query.Substring(1);
        query = string.IsNullOrEmpty(query) || query == "?" ? "?zone=" + zone : query + "&zone=" + zone;
        proxyRequest.RequestUri = new Uri(destinationPrefix + "/ws" + query);
        proxyRequest.Headers.Host = null;
    }
}
