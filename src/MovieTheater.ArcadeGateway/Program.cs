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

var app = builder.Build();

var config = app.Configuration;
string secret = config["ArcadeTokenSecret"]
    ?? throw new InvalidOperationException("ArcadeTokenSecret is required.");
string coordinatorBase = (config["CoordinatorBaseUrl"] ?? "http://localhost:8000").TrimEnd('/');
string siteOrigin = config["SiteOrigin"] ?? "https://your-movie-site.example";

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
        && ArcadeSaveId.TryParse(requestedRoomId, out var svUser, out var svGame, out var svSlot, out _, out _)
        && svUser == payload.UserId && svGame == payload.GameId
        && !saveStore.MountHasSave(requestedRoomId))
    {
        var fresh = context.Request.Query["fresh"].ToString();
        bool newGame = fresh == "1" || string.Equals(fresh, "true", StringComparison.OrdinalIgnoreCase);
        // Resume-from-snapshot: ?seedslot=N seeds a chosen snapshot slot's bytes into the (slot-0) room,
        // so the player continues FROM snapshot N. Defaults to the id's slot (0 = the auto Continue slot).
        int seedSlot = int.TryParse(context.Request.Query["seedslot"].ToString(), out var ss) ? ss : svSlot;
        try
        {
            if (newGame)
            {
                saveStore.ClearSession(requestedRoomId);
                saveStore.ClearCoreSaveDir(requestedRoomId); // PSP/DC/… save-dir tree, if any
                app.Logger.LogInformation("Arcade save cleared (New game) for user {User} game {Game}", svUser, svGame);
            }
            else
            {
                bool seeded = saveStore.SeedSession(svUser, svGame, requestedRoomId, seedSlot);
                // Save-dir cores (PSP memstick / DC-Naomi VMU / DOS) don't ride SAVE_RAM — restore their tree too.
                bool seededCore = saveStore.SeedCoreSaveDir(svUser, svGame, requestedRoomId);
                app.Logger.LogInformation("Arcade save {Action}{Core} for user {User} game {Game} slot {Slot}",
                    seeded ? "seeded" : "none (fresh)", seededCore ? "+coredir" : "", svUser, svGame, seedSlot);
            }
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
            await romCache.EnsureMaterializedAsync(payload.GameId, context.RequestAborted);
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
// label. The client flushes a SAVE (t=106) first, then POSTs here with its capability token. Only the
// OWNER may snapshot — a guest's token userId won't match the creator's id (the save belongs to the
// creator's world). Best-effort DB mirror so the slot shows in the resume/My-Saves lists.
app.MapPost("/w-snap/{token}", async (HttpContext ctx, string token) =>
{
    if (saveStore == null) return Results.NotFound();
    if (!ArcadeCapabilityToken.TryValidate(secret, token, out var p) || p is null) return Results.Forbid();
    var id = p.CloudRetroRoomId ?? "";
    if (!ArcadeSaveId.TryParse(id, out var u, out var g, out _, out var sys, out _) || u != p.UserId || g != p.GameId)
        return Results.BadRequest();

    string? label = null;
    try
    {
        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string?>>(ctx.RequestAborted);
        body?.TryGetValue("label", out label);
    }
    catch { /* no/invalid body → unnamed snapshot */ }

    var meta = await saveStore.SnapshotCurrentAsync(u, g, sys, id,
        string.IsNullOrWhiteSpace(label) ? null : label!.Trim(), ctx.RequestAborted);
    if (meta == null) return Results.Json(new { ok = false, reason = "no live save yet — play a moment, then snapshot" });
    await mirrorSave(meta);
    app.Logger.LogInformation("Arcade snapshot slot {Slot} for user {User} game {Game}", meta.SlotId, u, g);
    return Results.Json(new { ok = true, slot = meta.SlotId, label = meta.Label });
});

// In-room LOAD a snapshot WITHOUT restarting (arcade-saves-plan S3): swap the chosen slot's bytes into
// the live mount, then the client sends t=107 to make the core restore it. Owner-only.
app.MapPost("/w-load/{token}", async (HttpContext ctx, string token) =>
{
    if (saveStore == null) return Results.NotFound();
    if (!ArcadeCapabilityToken.TryValidate(secret, token, out var p) || p is null) return Results.Forbid();
    var id = p.CloudRetroRoomId ?? "";
    if (!ArcadeSaveId.TryParse(id, out var u, out var g, out _, out _, out _) || u != p.UserId || g != p.GameId)
        return Results.BadRequest();
    int slot = 0;
    try { var b = await ctx.Request.ReadFromJsonAsync<Dictionary<string, int>>(ctx.RequestAborted); b?.TryGetValue("slot", out slot); } catch { }
    return Results.Json(new { ok = saveStore.LoadSlotToMount(u, g, id, slot) });
});

// Internal, secret-gated blob ops the SITE calls for My-Saves management + import/export (the k8s pod
// can't touch Ziggy's disk). Blobs stay on Ziggy; the site owns auth + the DB row.
bool InternalAuth(HttpContext c) => !string.IsNullOrEmpty(secret) &&
    string.Equals(c.Request.Headers["X-Arcade-Internal-Secret"].ToString(), secret, StringComparison.Ordinal);

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
    int slot = (r.Kind ?? "state") == "sram" ? 0 : (r.Slot > 0 ? r.Slot : saveStore.NextSnapshotSlot(r.UserId, r.GameId));
    var meta = await saveStore.ImportSaveAsync(r.UserId, r.GameId, r.System ?? "", r.Kind ?? "state", slot, r.Label, bytes, ctx.RequestAborted);
    await mirrorSave(meta);
    return Results.Json(new { ok = true, slot = meta.SlotId, kind = meta.Kind, sizeBytes = meta.SizeBytes });
});

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
        // One-pool arcade: the site still tags GL-core rooms (dc/naomi/atomiswave/psp) with zone=gl for the
        // old two-pool routing, but there is a single Windows worker pool now (registered zone "main"). Strip
        // the zone so those rooms match it instead of the coordinator returning "no free workers" (PSP/DC were
        // disconnecting after the Windows-only migration flipped the workers from zone "gl" to "main").
        query = System.Text.RegularExpressions.Regex.Replace(query, @"[?&]zone=[^&]*", "");
        if (query.StartsWith("&")) query = "?" + query.Substring(1);
        proxyRequest.RequestUri = new Uri(destinationPrefix + "/ws" + query);
        proxyRequest.Headers.Host = null;
    }
}
