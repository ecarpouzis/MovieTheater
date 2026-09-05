using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MovieTheater.Arcade;
using MovieTheater.Core;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Arcade;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Arcade control plane (arcade-plan.md §6). Like the stream + channel planes it requires a
    /// password-verified session (StreamingUser); games and rooms are additionally gated by each game's
    /// rating ceiling against the viewer's age restriction. It owns the catalog, room records, seats,
    /// presence, and invites — but NOT the CloudRetro rooms: the backend can't create them (§2 box), so
    /// the creator's browser makes the room and reports its id back via Bind.
    /// </summary>
    [Authorize(Policy = "StreamingUser")]
    public partial class ArcadeController : Controller
    {
        // URL-safe, unambiguous room codes (RFC 4648 base32 alphabet).
        private const string CodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        private const int CodeLength = 6;

        /// <summary>How long after the reaper closed a room a heartbeat from someone still in it may
        /// reopen the row (see the Heartbeat endpoint). Sized to a long play session: the whole point is
        /// that the player never left, and their beat is better evidence of liveness than our own
        /// bookkeeping, which is what got the room closed in the first place.</summary>
        private static readonly TimeSpan RoomRevivalWindow = TimeSpan.FromHours(6);

        /// <summary>The core-key a stored save's namespace implies: <c>""</c> for the system's default core
        /// ("n64"), the suffix for an alternate one ("n64-parallel_n64" → "parallel_n64"), or null when
        /// there's nothing to go on (no row, or a system string we don't recognise — then the caller leaves
        /// the launch alone rather than guessing). Mirrors how saveSystem is minted below.</summary>
        private static string? SaveCoreKey(string system, string? saveSystem)
        {
            if (string.IsNullOrEmpty(saveSystem) || string.IsNullOrEmpty(system)) return null;
            if (string.Equals(saveSystem, system, StringComparison.Ordinal)) return string.Empty;
            var prefix = system + "-";
            return saveSystem.StartsWith(prefix, StringComparison.Ordinal) ? saveSystem[prefix.Length..] : null;
        }

        /// <summary>
        /// A live room's (system, coreKey), recovered from the bound save id — the only record a JOINER
        /// has of how the creator launched. The id carries the SAVE namespace, which is the system for a
        /// default-core room and <c>system-coreKey</c> for an alternate one.
        ///
        /// <para>Splitting the two apart is a fix, not a refactor. Join used to hand the raw save
        /// namespace to the client as <c>descriptor.system</c>, so a joiner in a parallel_n64 room got
        /// <c>"n64-parallel_n64"</c> — a key in none of the client's system tables. The one that hurt was
        /// <c>profileFor()</c>: the miss falls back to the DEFAULT input profile, which turns the N64
        /// stick-to-dpad fold back ON (the Goldeneye "pans the view while walking" double-bind the n64
        /// profile exists to kill), swaps confirm/back on the keyboard, and drops the C-buttons off the
        /// right stick. The creator was fine; only joiners were affected, which is exactly the kind of
        /// bug that survives a solo test.</para>
        ///
        /// <para>A capture room's namespace is the literal "capture" and does not derive from the
        /// catalog system ("switch"), so it falls through to "use the id verbatim, no alternate core" —
        /// the behaviour it already had.</para>
        /// </summary>
        private static (string System, string CoreKey) RoomSystemAndCore(string catalogSystem, string? boundRoomId)
        {
            if (!ArcadeSaveId.TryParse(boundRoomId, out _, out _, out _, out var saveSystem, out _))
                return (catalogSystem, string.Empty);
            var core = SaveCoreKey(catalogSystem, saveSystem);
            return core == null ? (saveSystem, string.Empty) : (catalogSystem, core);
        }

        private readonly MovieDb movieDb;
        private readonly IArcadeHost host;
        private readonly ArcadeRoomService rooms;
        private readonly ILogger<ArcadeController> logger;
        private readonly MovieTheaterConfiguration config;
        private readonly IDataProtectionProvider dataProtection;
        private readonly IMemoryCache cache;
        private readonly IServiceScopeFactory scopeFactory;

        public ArcadeController(MovieDb movieDb, IArcadeHost host, ArcadeRoomService rooms, ILogger<ArcadeController> logger, MovieTheaterConfiguration config, IDataProtectionProvider dataProtection, IMemoryCache cache, IServiceScopeFactory scopeFactory)
        {
            this.movieDb = movieDb;
            this.host = host;
            this.rooms = rooms;
            this.logger = logger;
            this.config = config;
            this.dataProtection = dataProtection;
            this.cache = cache;
            this.scopeFactory = scopeFactory;
        }

        private int? GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
        }

        // Arcade games are intentionally NOT age-gated — site policy is to age-gate MOVIES and CHANNELS,
        // not games (those keep their own GetAgeRestrictionAsync in ChannelController/WatchpartyController).
        // Returning "no ceiling" makes every per-title `RatingCeiling <= / > ageRestriction` check in this
        // controller inert, so all enabled games are visible AND launchable to every user. Kept as one
        // documented chokepoint rather than deleting the scattered checks: the policy is visible in one
        // place and reverses in one line. RatingCeiling still lives on the row for a future gate if wanted.
        private Task<int> GetAgeRestrictionAsync(int userId) => Task.FromResult(int.MaxValue);

        // No explicit choice → default All Games (region still narrows to English; a name search spans
        // everything). Variant used to default to "release" (hiding every hack/mod, including our own
        // curated ones), which is what made the lobby look like it "didn't have" mods/hacks/romhacks —
        // see ArcadeRomTags for why that filter exists at all and RomhacksSourcePrefix below for the
        // dedicated curated-folder option.
        private static string NormalizeVariant(string variant) =>
            string.IsNullOrWhiteSpace(variant) ? "all" : variant.Trim().ToLowerInvariant();

        // The KNOWN regions the user has switched OFF (comma-separated). Empty (the default) hides nothing, so
        // every card shows. The UI only ever offers regions we positively know, so Unknown/NULL never appear here.
        private static List<string> ParseHideRegions(string hideRegions) =>
            string.IsNullOrWhiteSpace(hideRegions)
                ? new List<string>()
                : hideRegions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // The systems the user has switched ON (comma-separated). Empty = no system filter = every system,
        // which is what an untouched lobby sends. The console carousel toggles several at once, so this
        // grew from a single value to a set — a bare "?system=nes" is still exactly one-element list, which
        // is what keeps every link and bookmark minted before the carousel working unchanged.
        private static List<string> ParseSystems(string system) =>
            string.IsNullOrWhiteSpace(system)
                ? new List<string>()
                : system.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => s.ToLowerInvariant()).Distinct().ToList();

        // Our hand-curated romhack/mod pile (L:\4 - Software\Romhacks, sorted per-system — see
        // arcade-jit-ingest usage) as opposed to the tens of thousands of No-Intro/GoodTools-tagged
        // hacks/betas/protos/pirates that live throughout the wider catalog and also carry Variant="Hack"
        // etc. (ArcadeRomTags). The "Romhacks" filter option means "show me OUR pile", not "show me any
        // ROM with a [Hack] tag anywhere" — hence matching on SourceArchivePath rather than Variant.
        // The two Wii SD-loader BrawlEx mods (Project REX, Smash Bros Infinite) ship as a loader .elf/.dol
        // with SourceArchivePath NULL (arcade-wii-sd-loader — not JIT-managed), so they're matched by
        // extension instead; both currently live under Romhacks\Smash Brothers Brawl Mods\.
        private const string RomhacksSourcePrefix = @"L:\4 - Software\Romhacks\";

        // The match set: rows that make a game QUALIFY for a card. Shared by Games and GameLetters so a
        // letter's offset indexes EXACTLY the list Games pages — the moment the two disagree about what
        // matches, every letter jump lands on the wrong card.
        private static IQueryable<ArcadeGame> ApplyCardFilters(
            IQueryable<ArcadeGame> baseQ, List<string> systems, int? maxPlayers, string genre, string search, List<string> hideRegions, string var_, string ra = null)
        {
            var matchQ = baseQ;
            // Several systems = a UNION (show me SNES *and* Genesis), which is how the carousel reads: each
            // tile you light up ADDS a console rather than replacing the one before it.
            if (systems != null && systems.Count > 0) matchQ = matchQ.Where(g => systems.Contains(g.System));
            if (maxPlayers is int mp && mp > 1) matchQ = matchQ.Where(g => g.MaxPlayers >= mp);
            if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); matchQ = matchQ.Where(g => g.Title.Contains(s)); }

            // RetroAchievements support filter (arcade-ra-enrich flags, uniform across a card's versions):
            // find games that track achievements / have high-score or speedrun leaderboards. "any" = any RA.
            switch ((ra ?? "").Trim().ToLowerInvariant())
            {
                case "achievements": matchQ = matchQ.Where(g => (g.RaAchievementCount ?? 0) > 0); break;
                case "highscores": matchQ = matchQ.Where(g => g.RaHasScoreLeaderboard); break;
                case "speedruns": matchQ = matchQ.Where(g => g.RaHasTimeLeaderboard); break;
                case "leaderboards": matchQ = matchQ.Where(g => g.RaHasScoreLeaderboard || g.RaHasTimeLeaderboard); break;
                case "any": matchQ = matchQ.Where(g => (g.RaAchievementCount ?? 0) > 0 || g.RaHasScoreLeaderboard || g.RaHasTimeLeaderboard); break;
            }

            // Region is a DESELECT filter (default = nothing hidden = show everything). hideRegions carries the
            // KNOWN regions the user switched OFF; a version survives unless its region is one of them, and a
            // card shows iff ≥1 version survives — so a card drops only when EVERY version is a known, hidden
            // region. Unknown/NULL are never hidden (you can only hide what we positively know), which also keeps
            // the big "Unknown" bucket always visible.
            if (hideRegions != null && hideRegions.Count > 0)
                matchQ = matchQ.Where(g => g.Region == null || !hideRegions.Contains(g.Region));

            if (var_ == "release")
                matchQ = matchQ.Where(g => g.Variant == "Release" || g.Variant == null);
            else if (var_ == "modded")
                matchQ = matchQ.Where(g => g.Variant != "Release" && g.Variant != null);
            else if (var_ == "romhacks")
                matchQ = matchQ.Where(g => (g.SourceArchivePath != null && g.SourceArchivePath.StartsWith(RomhacksSourcePrefix))
                    || (g.System == "wii" && (g.RomPath.EndsWith(".elf") || g.RomPath.EndsWith(".dol"))));
            else if (var_ != "all")
                matchQ = matchQ.Where(g => g.Variant == var_ || (g.Variant ?? "").ToLower() == var_);

            // Genre filter (IGDB-sourced, stored on the card anchor): a card qualifies if ANY of its rows
            // carries the genre — a correlated EXISTS so it composes with the version-level region/variant gates.
            if (!string.IsNullOrWhiteSpace(genre))
            {
                var gr = genre.Trim();
                matchQ = matchQ.Where(g => baseQ.Any(a => a.System == g.System && a.CollapseKey == g.CollapseKey
                    && a.Genres != null && a.Genres.Contains(gr)));
            }
            return matchQ;
        }

        // ONE CARD PER GAME (docs/arcade-dedupe-multidisc-plan.md): rows are grouped by (System, CollapseKey)
        // — the punctuation/article-folded key — into games, each carrying a version dropdown (region/rev/
        // edition/disc/hack), so the same game's many ROMs (INCLUDING cosmetically-different dumps like
        // "Atlantis - The Lost Tales" ⇄ "Atlantis: The Lost Tales") collapse to a single card. The card's
        // display Title is the alphabetically-first variant in the group. Filters gate CARDS by version
        // existence — a game shows iff it has ≥1 version matching. Defaults are English + All Games (region
        // narrows, variant "romhacks"/"release"/"modded" narrow further). Grouping is query-time so ingests
        // fold in automatically. Age gate always applies.
        //
        // `skip` is an absolute offset into the sorted card list and WINS over `page`: the lobby's pager
        // seeks to a letter bucket, which starts wherever it starts — rounding that down to a page boundary
        // would land the user in the tail of the previous letter.
        [HttpGet("/API/Arcade/Games")]
        public async Task<IActionResult> Games(
            string system = null, string hideRegions = null, int? maxPlayers = null,
            string variant = null, string genre = null, string sort = null, string search = null,
            string ra = null, int page = 1, int pageSize = 60, int? skip = null, int? id = null,
            System.Threading.CancellationToken ct = default)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            // Deep-link fetch (?game=<versionId> in the lobby URL): resolve the one card that
            // contains this version, ignoring the card filters — a shared link should open its game
            // whatever filter set the recipient happens to have. Age visibility still applies via
            // VisibleGamesAsync.
            if (id != null)
            {
                var visibleQ = await VisibleGamesAsync(userId.Value);
                var anchor = await visibleQ.FirstOrDefaultAsync(g => g.Id == id.Value, ct);
                if (anchor == null)
                    return Json(new { games = new List<object>(), totalCount = 0, page = 1, pageSize, skip = 0 });
                var card = await BuildGameCardsAsync(
                    visibleQ,
                    new List<(string, string, string)> { (anchor.System, anchor.CollapseKey, null) },
                    null, ct);
                return Json(new { games = card, totalCount = card.Count, page = 1, pageSize, skip = 0 });
            }

            var var_ = NormalizeVariant(variant);
            var hidden = ParseHideRegions(hideRegions);
            var selectedSystems = ParseSystems(system);

            var baseQ = await VisibleGamesAsync(userId.Value);
            var matchQ = ApplyCardFilters(baseQ, selectedSystems, maxPlayers, genre, search, hidden, var_, ra);

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 120);
            int skipRows = Math.Max(0, skip ?? (page - 1) * pageSize);
            // Card-level aggregates for sorting: rating/year live on the anchor, players is the card max.
            var groupedQ = matchQ.GroupBy(g => new { g.System, g.CollapseKey })
                .Select(grp => new
                {
                    grp.Key.System,
                    grp.Key.CollapseKey,
                    Title = grp.Min(x => x.Title),   // representative display title for the collapsed card
                    Sort = grp.Min(x => x.SortTitle),
                    // Sort on the confidence-weighted score, never the raw one: a 1-vote 100 must not outrank a
                    // 4,000-vote 94 (that's how American Chopper became the top-rated PS2 game). See
                    // ArcadeRatingWeightsCommand, which computes this.
                    Rating = grp.Max(x => x.RatingWeighted),
                    Year = grp.Max(x => x.Year),
                    Players = grp.Max(x => (int)x.MaxPlayers),
                });
            var totalCount = await groupedQ.CountAsync(ct);
            // Sort (all fall back to alphabetical within ties; unrated/undated float to the end).
            groupedQ = (sort ?? "").Trim().ToLowerInvariant() switch
            {
                "rating" => groupedQ.OrderByDescending(x => x.Rating ?? -1).ThenBy(x => x.Sort),
                "year" => groupedQ.OrderByDescending(x => x.Year ?? 0).ThenBy(x => x.Sort),
                "system" => groupedQ.OrderBy(x => x.System).ThenBy(x => x.Sort),
                "players" => groupedQ.OrderByDescending(x => x.Players).ThenBy(x => x.Sort),
                _ => groupedQ.OrderBy(x => x.Sort).ThenBy(x => x.Title),
            };
            var pageKeys = await groupedQ
                .Skip(skipRows).Take(pageSize).ToListAsync(ct);

            // Deselect region model: cards are hidden wholesale, not narrowed to one region, so there is no
            // single "specific region" to pin the displayed version/art to — pass null (card's own default).
            var games = await BuildGameCardsAsync(
                baseQ,
                pageKeys.Select(k => (k.System, k.CollapseKey, k.Title)).ToList(),
                null, ct);

            return Json(new { games, totalCount, page, pageSize, skip = skipRows });
        }

        /// <summary>
        /// The card projection, shared by the lobby grid and the "Recently played" strip. Given card keys
        /// (System, CollapseKey) it loads every age-visible version of those cards plus their cheat counts
        /// and returns the card DTO the UI reads. It is shared deliberately: BOTH surfaces open the same
        /// game modal on click, and the modal needs the full payload (versions, cheats, renderer/scheme
        /// support) — a strip that shipped a thinner card would open a modal that can't launch.
        ///
        /// A key's Title may be null, meaning "derive it from the loaded rows". The lobby passes the
        /// grouped Min(Title) it computed under its FILTERS (a region filter can exclude the
        /// alphabetically-first row, and the card should then show a title it still has); the recent strip
        /// is unfiltered and lets this derive it.
        /// </summary>
        private async Task<List<object>> BuildGameCardsAsync(
            IQueryable<ArcadeGame> baseQ,
            IReadOnlyList<(string System, string CollapseKey, string Title)> keys,
            string specificRegion,
            System.Threading.CancellationToken ct = default)
        {
            // All age-visible versions of the requested games (superset by System/CollapseKey IN, trimmed to exact
            // page keys in memory) — the dropdown lists every version, not just the ones that matched.
            var pageSystems = keys.Select(k => k.System).Distinct().ToList();
            var pageCollapse = keys.Select(k => k.CollapseKey).Distinct().ToList();
            var versionRows = await baseQ.Where(g => pageSystems.Contains(g.System) && pageCollapse.Contains(g.CollapseKey)).ToListAsync(ct);
            var keySet = keys.Select(k => (k.System, k.CollapseKey)).ToHashSet();
            var byGame = versionRows.Where(g => keySet.Contains((g.System, g.CollapseKey)))
                .GroupBy(g => (g.System, g.CollapseKey))
                .ToDictionary(x => x.Key, x => x.ToList());

            // Cheat counts for the page's ROMs, one grouped query (not per card). Codes only — the emulator/
            // quality OPTION cheats moved to the per-game config tool. The card needs only to know whether to
            // render the picker at all; the list itself is lazy-loaded when it's opened.
            var pageVersionIds = versionRows.Select(g => g.Id).ToList();
            var cheatCounts = await movieDb.ArcadeCheats
                .Where(c => pageVersionIds.Contains(c.ArcadeGameId) && c.Kind == "code")
                .GroupBy(c => c.ArcadeGameId)
                .Select(g => new { GameId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.GameId, x => x.Count, ct);

            // The renderer/core each version ACTUALLY launches on (⚙ Configure → ArcadeGameProfile, else the
            // system default). Without it the Start-room menu could only mark the SYSTEM's default, which for
            // a game with a configured core is a lie — SM64: Last Impact is pinned to parallel_n64/Glide64 yet
            // the menu marked "mupen64plus-next · Vulkan — default". One grouped query for the page, keyed the
            // way the config tool keys its rows: (System, lowercased Title).
            var pageTitleKeys = versionRows.Select(TitleKeyOf).Distinct().ToList();
            var savedProfiles = await movieDb.ArcadeGameProfiles.AsNoTracking()
                .Where(p => pageSystems.Contains(p.System) && pageTitleKeys.Contains(p.TitleKey))
                .Select(p => new { p.System, p.TitleKey, p.RenderProfile, p.HwContext })
                .ToListAsync(ct);
            var savedByKey = savedProfiles
                .GroupBy(p => (p.System, p.TitleKey))
                .ToDictionary(g => g.Key, g => g.First());
            var rowById = versionRows.ToDictionary(g => g.Id);

            return keys.Select(k =>
            {
                byGame.TryGetValue((k.System, k.CollapseKey), out var vs);
                vs ??= new List<ArcadeGame>();
                // Null Title = derive it (see the doc comment): the same Min(Title) the grid groups by,
                // taken over every version the card actually has.
                var title = k.Title ?? (vs.Count > 0 ? vs.Min(g => g.Title) : null);
                // Build launchable versions — multi-disc sets collapse to one entry (DiscCount > 1). The
                // first is the card's default selection + box-art source; a region filter floats that region up.
                var versions = ArcadeVersions.Build(vs, specificRegion);
                var rep = versions.FirstOrDefault();
                // Box art is shared across the card's versions. Point at a sibling that already HAS art (so a
                // "(Rev A)" default doesn't hide the base "(USA)" box), else the lowest-id row — the canonical
                // card file the image route writes a fresh fetch to. Filter-independent, so it stays one file.
                var artRow = vs.FirstOrDefault(g => g.BoxArtPath != null) ?? vs.OrderBy(g => g.Id).FirstOrDefault();
                // Enrichment (LaunchBox/IGDB rating + genres/summary/dev/pub) is written to a row's anchor;
                // across a collapsed card spanning several Titles it may sit on any cross-dump sibling, so
                // prefer the enriched one (rating present), else fall back to the lowest-id anchor.
                var meta = vs.Where(g => g.CommunityRating != null || g.LaunchBoxRating != null || g.RatingScore != null)
                             .OrderBy(g => g.Id).FirstOrDefault()
                           ?? vs.OrderBy(g => g.Id).FirstOrDefault();
                return (object)new
                {
                    key = k.System + "|" + k.CollapseKey,
                    title,
                    system = k.System,
                    artId = artRow?.Id ?? rep?.Id ?? 0,
                    // Cache-busting token for that cover — see ArcadeBoxArt.ArtVersion. Taken from artRow,
                    // the same row /ArcadeImage resolves its bytes from, so the token moves exactly when the
                    // art does. Without it a re-pointed cover stays stale in every browser that already
                    // loaded the card, for a day, through hard reloads included.
                    artV = ArcadeBoxArt.ArtVersion(artRow?.BoxArtSourceUrl, artRow?.BoxArtPath,
                                                   vs.Count > 0 ? vs.Max(g => g.BoxArtGeneration) : 0),
                    // A blocked card has no cover and never will until someone unblocks it — say so, so the
                    // UI draws the placeholder instead of requesting an image that is guaranteed to 404.
                    hasBoxArt = vs.Any(g => g.BoxArtPath != null) && !vs.Any(g => g.BoxArtBlocked),
                    year = rep?.Year ?? meta?.Year,
                    maxPlayers = versions.Count > 0 ? versions.Max(v => v.MaxPlayers) : (byte)1,
                    versionCount = versions.Count,
                    // Review score: the hand-curated community score wins (it only exists where the bulk
                    // importers were absent or wrong — see ArcadeGame.CommunityRating), then LaunchBox (83% of
                    // cards), then IGDB for the ~541 LaunchBox doesn't rate. The card shows the RAW score — the
                    // weighted one above exists only to order the grid. ratingSource rides along so the UI can
                    // say where the number came from, which is what keeps a researched estimate honest.
                    rating = (meta?.CommunityRating ?? meta?.LaunchBoxRating ?? meta?.RatingScore) is double rs
                        ? (int?)Math.Round(rs) : null,
                    ratingCount = meta?.CommunityRating != null
                        ? meta.CommunityRatingCount
                        : meta?.LaunchBoxRatingCount ?? meta?.RatingCount,
                    ratingSource = meta?.CommunityRating != null ? meta.CommunityRatingSource
                        : meta?.LaunchBoxRating != null ? "LaunchBox"
                        : meta?.RatingScore != null ? "IGDB" : null,
                    genres = meta?.Genres,
                    themes = meta?.Themes,
                    summary = meta?.Summary,
                    developer = meta?.Developer,
                    publisher = meta?.Publisher,
                    gameModes = meta?.GameModes,
                    esrb = meta?.EsrbRating,
                    // RetroAchievements support badges (arcade-ra-enrich): set on every version of a card,
                    // so any version answers. 🏆 achievements, 🥇 high-score boards, ⏱ speedrun (time) boards.
                    raAchievements = vs.Any(g => (g.RaAchievementCount ?? 0) > 0),
                    raHighScores = vs.Any(g => g.RaHasScoreLeaderboard),
                    raSpeedruns = vs.Any(g => g.RaHasTimeLeaderboard),
                    // 'heavy' = Moonlight-streamed (plan §7.1): the card's action becomes
                    // Prepare/Play-via-Moonlight instead of creating a CloudRetro room.
                    lane = vs.Select(g => g.Lane).FirstOrDefault(l => l != null),
                    // capture (H5): a heavy title on the capture allowlist ALSO offers browser play — the
                    // modal shows "Play in browser" beside the Artemis launch (docs/arcade-capture-worker-plan.md §6.3).
                    capture = vs.Any(g => string.Equals(g.Lane, "heavy", StringComparison.OrdinalIgnoreCase) && CloudRetroHost.IsCaptureEnabled(g.CloudRetroGameKey)),
                    // Per-launch GL/Vulkan force (play-button dropdown): only 3D systems with a real
                    // render-context choice offer it; see CloudRetroHost.HwToggleSystems.
                    supportsHwToggle = CloudRetroHost.SupportsHwToggle(k.System),
                    // Whether the game modal's ⚙ Configure panel (editor-only) has anything to offer for this
                    // system — catalogued core options and/or a renderer choice. Gates showing the button.
                    configurable = ArcadeCoreOptionCatalog.HasAnything(k.System) || CloudRetroHost.SupportsHwToggle(k.System),
                    // Wii controller-scheme picker (GameCube vs Wiimote+Nunchuk): offered on every Wii
                    // title now. defaultControllerScheme pre-selects the dropdown — "gc" for the
                    // GC-native BrawlEx mods, "wiimote" for every other Wii game (empty = no picker).
                    supportsControllerScheme = CloudRetroHost.SupportsControllerScheme(k.System),
                    defaultControllerScheme = CloudRetroHost.DefaultControllerScheme(k.System, title),
                    versions = versions.Select(v =>
                    {
                        // What Start room boots for THIS version (per-game config, else the system default).
                        // Per-version because the config profile is keyed by Title and a card can collapse
                        // versions whose titles differ.
                        ArcadeRendererProfiles.RenderProfile rp = null;
                        var rpFromGame = false;
                        if (rowById.TryGetValue(v.Id, out var vrow))
                        {
                            savedByKey.TryGetValue((vrow.System, TitleKeyOf(vrow)), out var sp);
                            (rp, rpFromGame) = EffectiveRenderProfile(vrow.System, sp?.RenderProfile, sp?.HwContext);
                        }
                        return new
                        {
                            id = v.Id, label = v.Label, region = v.Region,
                            variant = v.Variant, year = v.Year, maxPlayers = v.MaxPlayers, discCount = v.DiscCount,
                            // The renderer/core Start room will use, and whether it's this game's own setting or
                            // just the system default — the Start menu marks the right entry with it.
                            renderProfile = rp?.Id,
                            renderProfileLabel = rp?.Label,
                            renderProfileFromGame = rpFromGame,
                            // Per-version RA support: our dump matches an RA-recognized hash, so achievements/scores
                            // actually fire on THIS version. Drives the 🏆 marker in the modal's version dropdown.
                            raSupported = v.RaSupported,
                            // Code cheats only. Already zero on systems whose core ignores retro_cheat_set
                            // (only imported systems get code rows — see ArcadeCheatCatalog.SupportsCheatCodes).
                            cheatCount = ArcadeCheatCatalog.SupportsCheatCodes(k.System)
                                && cheatCounts.TryGetValue(v.Id, out var cc) ? cc : 0,
                        };
                    }).ToList(),
                };
            }).ToList();
        }

        // Bucket sizes + OFFSETS for the alphabetically-ordered card list, under the lobby's current
        // filters — what the lobby's letter pager jumps with (offset → ?skip=). Offsets are counted by
        // walking the ordered list itself rather than by ordering the buckets ourselves, so they agree
        // with SQL's collation instead of with an assumption about it.
        //
        // Only meaningful for the A–Z sort; under rating/year/system/players the buckets aren't
        // contiguous and the pager shows page numbers instead, so it never calls this.
        [HttpGet("/API/Arcade/GameLetters")]
        public async Task<IActionResult> GameLetters(
            string system = null, string hideRegions = null, int? maxPlayers = null,
            string variant = null, string genre = null, string search = null, string ra = null)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            var var_ = NormalizeVariant(variant);
            var hidden = ParseHideRegions(hideRegions);
            var selectedSystems = ParseSystems(system);

            var baseQ = await VisibleGamesAsync(userId.Value);
            var matchQ = ApplyCardFilters(baseQ, selectedSystems, maxPlayers, genre, search, hidden, var_, ra);

            // The same grouping + the same default ordering Games uses, so index i here is card i there.
            // MUST match Games exactly: group by (System, CollapseKey), tie-break on the Min(Title).
            var sortKeys = await matchQ.GroupBy(g => new { g.System, g.CollapseKey })
                .Select(grp => new { Sort = grp.Min(x => x.SortTitle), Title = grp.Min(x => x.Title) })
                .OrderBy(x => x.Sort).ThenBy(x => x.Title)
                .Select(x => x.Sort)
                .ToListAsync();

            // The walk lives in Web.LetterBuckets (shared with /API/BrowseLetters).
            var letters = Web.LetterBuckets.Walk(sortKeys)
                .Select(b => new { letter = b.Letter, count = b.Count, offset = b.Offset }).ToList();
            return Json(new { total = sortKeys.Count, letters });
        }

        // Facets for the lobby filter controls. Each facet is FACETED: it counts what WOULD be visible if the
        // user chose that value, so it excludes its OWN dimension but honors every OTHER active filter —
        // including the default English region. That's why an all-Japan system (fds, wsc) no longer shows a
        // count under the default English scope: selecting it would render an empty grid (the facet and the
        // grid used to disagree because the facet ignored region entirely), so it isn't offered until the
        // region is widened to All. The client refetches this whenever a facet-affecting param changes.
        [HttpGet("/API/Arcade/Filters")]
        public async Task<IActionResult> Filters(
            string system = null, string hideRegions = null, int? maxPlayers = null,
            string variant = null, string genre = null, string search = null, string ra = null)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            var var_ = NormalizeVariant(variant);
            var hidden = ParseHideRegions(hideRegions);
            var selectedSystems = ParseSystems(system);
            var baseQ = await VisibleGamesAsync(userId.Value);

            // Every facet below (except RA) now honors the current RA filter, so switching systems/regions/etc.
            // stays inside the "games with achievements" scope when that's selected.
            // Systems facet: everything EXCEPT the system filter, so every console keeps its real catalog
            // count and stays offered while you pick — that's what lets the console carousel show a stable
            // shelf instead of the unpicked tiles collapsing to zero. Count CARDS, not version rows: the grid
            // groups by (System, CollapseKey), and `total` below is exactly this key count.
            var systemsQ = ApplyCardFilters(baseQ, (List<string>)null, maxPlayers, genre, search, hidden, var_, ra);
            var systemKeys = await systemsQ.Select(g => new { g.System, g.CollapseKey }).Distinct().ToListAsync();
            var systems = systemKeys.GroupBy(k => k.System)
                .Select(x => new { value = x.Key, count = x.Count() }).OrderByDescending(x => x.count).ToList();

            // Regions facet: hide NOTHING (empty set) so the dropdown always lists every known region with its
            // full count, regardless of what's currently switched off. Unknown/NULL are dropped — they're never
            // a deselect option (you can only hide regions we positively know).
            var regionsQ = ApplyCardFilters(baseQ, selectedSystems, maxPlayers, genre, search, new List<string>(), var_, ra);
            var regions = await regionsQ.Where(g => g.Region != null && g.Region != "Unknown").GroupBy(g => g.Region)
                .Select(x => new { value = x.Key, count = x.Count() }).OrderByDescending(x => x.count).ToListAsync();

            // Variants facet: everything EXCEPT variant (var_="all").
            var variantsQ = ApplyCardFilters(baseQ, selectedSystems, maxPlayers, genre, search, hidden, "all", ra);
            var variants = await variantsQ.GroupBy(g => g.Variant)
                .Select(x => new { value = x.Key ?? "Release", count = x.Count() }).OrderByDescending(x => x.count).ToListAsync();

            // Genres facet: everything EXCEPT genre. Genres are comma-joined on the anchor, so split + count in memory.
            var genresQ = ApplyCardFilters(baseQ, selectedSystems, maxPlayers, null, search, hidden, var_, ra);
            var genreStrings = await genresQ.Where(g => g.Genres != null).Select(g => g.Genres).ToListAsync();
            var genres = genreStrings
                .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .GroupBy(x => x)
                .Select(x => new { value = x.Key, count = x.Count() })
                .OrderByDescending(x => x.count).Take(40).ToList();

            // Multiplayer count: the current scope EXCEPT the players filter (its own dimension).
            var playersQ = ApplyCardFilters(baseQ, selectedSystems, null, genre, search, hidden, var_, ra);

            // RetroAchievements facet: excludes its OWN dimension (ra: null), so each count is "how many cards
            // WOULD show if you picked that RA filter" under the other active filters. Counted as distinct cards.
            var raScopeQ = ApplyCardFilters(baseQ, selectedSystems, maxPlayers, genre, search, hidden, var_, null);
            async Task<int> CardCount(IQueryable<ArcadeGame> q) =>
                await q.Select(g => new { g.System, g.CollapseKey }).Distinct().CountAsync();
            var raFacet = new
            {
                achievements = await CardCount(raScopeQ.Where(g => (g.RaAchievementCount ?? 0) > 0)),
                highScores = await CardCount(raScopeQ.Where(g => g.RaHasScoreLeaderboard)),
                speedruns = await CardCount(raScopeQ.Where(g => g.RaHasTimeLeaderboard)),
            };

            return Json(new
            {
                total = systemKeys.Count,
                multiplayer = await playersQ.CountAsync(g => g.MaxPlayers >= 2),
                systems, regions, variants, genres,
                ra = raFacet,
            });
        }

        /// <summary>The render profiles (core-and-renderer combinations) offered per system, for the
        /// play-button dropdown. Static data — a system → [{id,label,isDefault}] map covering only the
        /// systems that have a choice. The client fetches this once and, per game, looks up its system to
        /// build the launch menu; the chosen id rides <see cref="CreateRoomRequest.RenderProfile"/>.</summary>
        [HttpGet("/API/Arcade/Renderers")]
        public IActionResult Renderers()
        {
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });
            var map = ArcadeRendererProfiles.AllSystems
                .ToDictionary(
                    sys => sys,
                    sys => ArcadeRendererProfiles.For(sys)
                        .Select(p => new { id = p.Id, label = p.Label, isDefault = p.IsDefault })
                        .ToList());
            return Json(map);
        }

        // Enabled arcade games. (Formerly also age-filtered; arcade is no longer age-gated — see
        // GetAgeRestrictionAsync.) Shared by Games/Filters/GameLetters so all three page EXACTLY the same
        // match set — the moment they disagree about what matches, a letter jump lands on the wrong card.
        private Task<IQueryable<ArcadeGame>> VisibleGamesAsync(int userId)
            => Task.FromResult(movieDb.ArcadeGames.Where(g => g.IsEnabled));

        [HttpGet("/API/Arcade/Rooms")]
        public async Task<IActionResult> Rooms()
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);
            var snapshot = rooms.Snapshot();
            if (snapshot.Count == 0)
                return Json(Array.Empty<object>());

            // Resolve the games + player names the snapshot references, then hide any room whose game
            // exceeds the viewer's age ceiling (a room inherits its game's ceiling).
            var gameIds = snapshot.Select(r => r.GameId).Distinct().ToList();
            var games = await movieDb.ArcadeGames
                .Where(g => gameIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Title, g.System, g.MaxPlayers, g.RatingCeiling })
                .ToDictionaryAsync(g => g.Id);

            // Creators and spectators too: the lobby's room card names the host ("Eric hosting") — and a host
            // who has left the room they opened is no longer in PlayerUserIds.
            var peopleIds = snapshot.SelectMany(r => r.PlayerUserIds)
                .Concat(snapshot.SelectMany(r => r.SpectatorUserIds))
                .Concat(snapshot.Select(r => r.CreatorUserId))
                .Distinct().ToList();
            var names = await movieDb.Users
                .Where(u => peopleIds.Contains(u.UserID))
                .Select(u => new { u.UserID, u.Username })
                .ToDictionaryAsync(u => u.UserID, u => u.Username);

            var result = new List<object>();
            foreach (var r in snapshot)
            {
                if (!games.TryGetValue(r.GameId, out var g) || g.RatingCeiling > ageRestriction)
                    continue;
                result.Add(new
                {
                    roomCode = r.RoomCode,
                    game = new { id = g.Id, title = g.Title, system = g.System },
                    players = r.PlayerUserIds.Select(id => names.GetValueOrDefault(id) ?? "Someone").ToList(),
                    host = names.GetValueOrDefault(r.CreatorUserId) ?? "Someone",
                    seatsFree = Math.Max(0, r.MaxPlayers - r.PlayerUserIds.Count),
                    maxPlayers = r.MaxPlayers,
                    // Watchers hold no controller port, so they are never folded into players/seatsFree —
                    // a 1-player game's room reads "1 playing · 0 seats free · 1 watching", not "2 playing".
                    spectators = r.SpectatorUserIds.Select(id => names.GetValueOrDefault(id) ?? "Someone").ToList(),
                    spectatorSeatsFree = Math.Max(0, ArcadeRoomService.SpectatorSeats - r.SpectatorUserIds.Count),
                    starting = !r.Bound,
                });
            }
            return Json(result);
        }

        public class CreateRoomRequest
        {
            public int GameId { get; set; }

            /// <summary>True = "New game": boot fresh instead of resuming the user's saved slot 0 (the
            /// gateway clears the mount). Default false = resume/Continue.</summary>
            public bool NewGame { get; set; }

            /// <summary>True = a COMPETITIVE room: no save-state loading, no cheats, and (once supported) no
            /// rewind, so leaderboard times/scores are legit — and, when the creator has a linked
            /// RetroAchievements account, rcheevos runs in HARDCORE mode. Independent of RA: a creator with no
            /// RA link can still run a competitive room (our own boards stay legit). Ignored for capture/heavy
            /// (native) rooms, where none of these levers apply.</summary>
            public bool Competitive { get; set; }

            /// <summary>Resume from a specific snapshot slot (≥1) instead of the Continue slot 0. 0 = Continue.</summary>
            public int SeedSlot { get; set; }

            /// <summary>Per-room video encoder bitrate in kbps (arcade per-room quality). 0 = use the worker's
            /// config default. Clamped server-side; only the creator's choice takes effect (one encoder/room).</summary>
            public int VideoBitrateKbps { get; set; }

            /// <summary>Per-room opus FEC: 0 = config default, 1 = force on (remote-friendly), 2 = force off
            /// (LAN-only, saves audio-packet bytes). Rides the WS URL to the worker like the other room flags.</summary>
            public int AudioFec { get; set; }

            /// <summary>In-frame packet pacing window in ms (worker patch 0028). The lobby's Network
            /// profile sets it (LAN 0, Remote 5, 5G 8); the worker spreads each encoded frame's RTP
            /// burst over this window so it doesn't slam cellular/shallow-buffer queues. Nullable on
            /// purpose: null = no deliberate choice (lane defaults apply — capture 8, GL 0), while an
            /// explicit 0 = pacing off, honored even on capture. The UI only sends a value when the
            /// Network dropdown was deliberately set.</summary>
            public int? PaceMs { get; set; }

            /// <summary>Per-room video codec (worker patch 0036): "av1"/"h264", null/empty = worker config
            /// default (AV1). The lobby's Codec selector sets it — pick H.264 when a tablet/software-AV1
            /// device will play: Chrome negotiates AV1 it can't decode in real time, and the keyframeless
            /// intra-refresh stream then accumulates UNBOUNDED video delay (2026-07-10 tablet incident).
            /// Room-wide (one encoder per room), so it also rides every joiner's descriptor.</summary>
            public string? VideoCodec { get; set; }

            /// <summary>Cheat ids the creator ticked in the lobby, as returned by <c>GET .../Cheats</c>:
            /// <c>"c{ArcadeCheat.Id}"</c> for a stored cheat, <c>"s:{optionKey}"</c> for a system-wide option
            /// cheat. Unknown ids are ignored, not rejected — a stale card in an open tab shouldn't fail the
            /// launch. Capped at <see cref="ArcadeCheatCatalog.MaxCheatsPerRoom"/>.</summary>
            public List<string>? Cheats { get; set; }

            /// <summary>Per-launch GL/Vulkan render-context force from the play-button dropdown:
            /// "gl"/"vulkan", null/empty = defer to the existing server precedence (the DB-pinned
            /// <see cref="MovieTheater.Db.ArcadeGameProfile.HwContext"/>, then renderer-option inference,
            /// then the core's config default). An explicit value here is the TOP of that precedence —
            /// it wins even over an admin's DB pin. Only meaningful for <see cref="CloudRetroHost.HwToggleSystems"/>;
            /// ignored for every other system and for capture rooms.</summary>
            public string? HwContext { get; set; }

            /// <summary>Per-launch render-PROFILE id from the play-button dropdown (see
            /// <see cref="ArcadeRendererProfiles"/>), e.g. "parallel_gs", "vulkan_gsdx", "parallel_n64". This
            /// is the full core-and-renderer pick — unlike <see cref="HwContext"/> (a bare gl/vulkan surface),
            /// a profile id can select an ALTERNATE CORE (n64 parallel_n64, ps1 pcsx_rearmed) or a different
            /// GS implementation on the SAME surface (ps2 parallel_gs vs vulkan_gsdx are both "vulkan", so a
            /// bare HwContext cannot express the choice). When set and valid for the game's system it is the TOP of the
            /// renderer precedence (above the bare HwContext, the DB-saved profile, and the default);
            /// an unknown id is ignored (a stale open tab shouldn't fail the launch). Only meaningful for
            /// systems that offer more than one profile.</summary>
            public string? RenderProfile { get; set; }

            /// <summary>Wii controller-scheme choice from the room-create picker: "gc" (GameCube
            /// controller — forced via hid4rom pin or the system hidGc fallback) or "wiimote"
            /// (Wiimote+Nunchuk). null/empty = defer to the worker's config default. Meaningful for
            /// any Wii title (<see cref="CloudRetroHost.SupportsControllerScheme"/>); ignored
            /// otherwise.</summary>
            public string? ControllerScheme { get; set; }
        }

        /// <summary>Cheats available for ONE version (ROM) of a game — the card's version dropdown decides
        /// which. Lazy-loaded when the picker opens: the popular titles carry hundreds of codes each
        /// (Mario Kart 64 alone has 941 upstream), so this never belongs in the games list payload.</summary>
        [HttpGet("/API/Arcade/Game/{gameId:int}/Cheats")]
        public async Task<IActionResult> GameCheats(int gameId)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == gameId && g.IsEnabled);
            if (game == null) return NotFound(new { message = "Game not found." });

            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);
            if (game.RatingCeiling > ageRestriction)
                return StatusCode(403, new { message = "This game isn't available on your account." });

            var cheats = await BuildCheatListAsync(game);
            return Json(new
            {
                gameId,
                system = game.System,
                cheats = cheats.Select(c => new { id = c.Id, name = c.Name, kind = c.Kind, defaultOn = c.DefaultOn, note = c.Note }),
            });
        }

        private sealed record CheatOffer(string Id, string Name, string Kind, bool DefaultOn, string? Note,
            string? OptionKey, string? OptionValue, string? Code);

        /// <summary>The cheat offer for one ROM: the community cheat <b>codes</b> in upstream order. Codes are
        /// withheld entirely on systems whose core ignores <c>retro_cheat_set</c>, so the picker can't show a
        /// toggle that provably does nothing. Emulator/quality OPTION cheats no longer appear here — they moved
        /// to the per-game config tool (ArcadeCoreOptionCatalog + the game modal's ⚙ Configure panel).</summary>
        private async Task<List<CheatOffer>> BuildCheatListAsync(ArcadeGame game)
        {
            if (!ArcadeCheatCatalog.SupportsCheatCodes(game.System)) return new List<CheatOffer>();

            var rows = await movieDb.ArcadeCheats
                .Where(c => c.ArcadeGameId == game.Id && c.Kind == "code")
                .OrderBy(c => c.Ordinal)
                .ToListAsync();

            return rows.Select(r =>
                new CheatOffer("c" + r.Id, r.Name, r.Kind, r.DefaultOn, r.Note, r.OptionKey, r.OptionValue, r.Code)).ToList();
        }

        // ── Per-game config tool (docs/arcade-per-game-config.md) ─────────────────────────────────────
        // Editor-gated. The source of truth is ArcadeGameProfile, keyed by normalized identity so one row
        // covers every ROM region/revision. Core options are delivered per-room at Start (ResolveGameConfigAsync
        // → descriptor.CoreOptions), so a change takes effect on the next room with no worker-manifest regen.

        /// <summary>Normalized game identity used to key <see cref="Db.ArcadeGameProfile"/> — the lowercased
        /// Title, matching the arcade-gameconfig-export join (<c>g.Title.ToLower()</c>).</summary>
        private static string TitleKeyOf(ArcadeGame game) => (game.Title ?? "").Trim().ToLowerInvariant();

        /// <summary>The render profile a room will ACTUALLY boot for a game, mirroring the launch-time
        /// precedence in <c>CreateRoom</c> exactly: the saved profile id, else the legacy bare HwContext pin,
        /// else the system default. <c>FromGame</c> says whether that came from this game's own config or is
        /// merely the system default — the distinction the Start-room menu and the config tool must show, and
        /// the reason a stale/unknown saved id reports FromGame=false (it falls through to the default at
        /// launch too, rather than booting what the label claims).</summary>
        private static (ArcadeRendererProfiles.RenderProfile? Profile, bool FromGame) EffectiveRenderProfile(
            string system, string? savedProfileId, string? savedHwContext)
        {
            var profiles = ArcadeRendererProfiles.For(system);
            if (profiles.Count == 0) return (null, false);
            var fallback = ArcadeRendererProfiles.Default(system);
            if (!string.IsNullOrEmpty(savedProfileId))
            {
                var exact = profiles.FirstOrDefault(p => string.Equals(p.Id, savedProfileId, StringComparison.Ordinal));
                return (exact ?? fallback, exact != null);
            }
            if (!string.IsNullOrEmpty(savedHwContext))
            {
                var byHw = ArcadeRendererProfiles.ForRenderer(system, savedHwContext);
                return (byHw ?? fallback, byHw != null);
            }
            return (fallback, false);
        }

        /// <summary>Same CanEditMovies gate used elsewhere in this controller (e.g. heavy-lane pairing).</summary>
        private async Task<bool> IsEditorAsync(int userId)
        {
            var s = await movieDb.UserSettings.AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserID == userId && u.SettingKey == "CanEditMovies");
            return s?.SettingValue == "true";
        }

        private static Dictionary<string, string>? ParseOptionsJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json!); }
            catch { return null; }
        }

        /// <summary>The game's saved per-game config as it should reach the emulator for a room: the per-game
        /// default-on option rows (PS2 widescreen — preserves out-of-the-box widescreen on the ~150 patchable
        /// titles) overlaid by the editor's saved profile (the config tool wins), plus the master switches
        /// those options need to actually fire. Returns the game's configured renderer (HwContext) too.</summary>
        private async Task<(Dictionary<string, string> CoreOptions, string? HwContext, string? RenderProfileId)> ResolveGameConfigAsync(ArcadeGame game)
        {
            var opts = new Dictionary<string, string>(StringComparer.Ordinal);

            // (a) per-game default-on option rows (PS2 widescreen).
            var defaultOn = await movieDb.ArcadeCheats.AsNoTracking()
                .Where(c => c.ArcadeGameId == game.Id && c.Kind == "option" && c.DefaultOn && c.OptionKey != null)
                .Select(c => new { c.OptionKey, c.OptionValue })
                .ToListAsync();
            foreach (var r in defaultOn)
                if (!string.IsNullOrEmpty(r.OptionKey)) opts[r.OptionKey!] = r.OptionValue ?? "enabled";

            // (b) saved profile overrides win.
            var profile = await movieDb.ArcadeGameProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.System == game.System && p.TitleKey == TitleKeyOf(game));
            var saved = ParseOptionsJson(profile?.CoreOptionsJson);
            if (saved != null)
                foreach (var kv in saved)
                    if (!string.IsNullOrEmpty(kv.Key)) opts[kv.Key] = kv.Value;

            // (c) master switches the picked options need behind a gate (pcsx2_enable_hw_hacks, …). An explicit
            // value for a gate key always wins over the implied one.
            foreach (var key in opts.Keys.ToList())
                foreach (var (impliedKey, impliedValue) in ArcadeCheatCatalog.ImpliedOptionsFor(key))
                    if (!opts.ContainsKey(impliedKey)) opts[impliedKey] = impliedValue;

            return (opts, profile?.HwContext, profile?.RenderProfile);
        }

        /// <summary>The game's effective option baseline BEFORE saved overrides, for the ⚙ Configure panel:
        /// the CORE's catalogued factory defaults, overlaid by the live system tuning (UltraLiveSpec — what
        /// the game ACTUALLY runs at on Ultra), overlaid by per-game default-on rows (PS2 widescreen).
        ///
        /// <para>The UltraLiveSpec overlay is the whole point: the core's embedded default disagrees with the
        /// live config.worker-gl.yaml on exactly the quality levers (beetle internal res: factory "1x(native)"
        /// vs live "4x"), so without it the panel shows a value the game isn't running. Every UltraLiveSpec
        /// token is a valid catalog value (ArcadeQualityPresetsTests), so it lands on a real dropdown option.</para>
        ///
        /// <para>GET renders this (plus saved) as each control's selected value; PUT drops a submitted value
        /// equal to it. Both callers MUST use this helper — if the shown baseline and the drop baseline drift,
        /// a left-alone quality lever round-trips into a stored override and stops tracking the yaml.</para>
        ///
        /// <para><b>Filtered by the selected render profile</b> (plan Phase 2.4). <c>UltraLiveSpec</c> is flat
        /// per core — deliberately, because it is the weld against config.worker-gl.yaml and the yaml delivers
        /// the UNION (pcsx2 carries both the paraLLEl-GS <c>pgs_*</c> values and the GSdx
        /// upscale/aniso/blending ones) — so an unfiltered baseline is a BLEND of two renderers and switching
        /// the Graphics dropdown could not change a displayed value even with the selector fixed. Overlaying
        /// only the keys applicable to <paramref name="profileId"/> is the whole display fix; the yaml weld
        /// keeps asserting the union.</para></summary>
        private static Dictionary<string, string> BuildEffectiveBaseline(
            string? core, string? profileId, IEnumerable<(string? OptionKey, string? OptionValue)> defaultOnRows)
        {
            var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
            if (core == null) return baseline;
            foreach (var o in ArcadeCoreOptionCatalog.ForCore(core)) baseline[o.Key] = o.Default;
            if (ArcadeQualityPresets.UltraLiveSpec.TryGetValue(core, out var ultra))
                foreach (var kv in ultra) baseline[kv.Key] = kv.Value;   // live system tuning wins over factory default
            foreach (var (key, value) in defaultOnRows)                  // per-game default-on wins over system tuning
                if (!string.IsNullOrEmpty(key)) baseline[key] = value ?? "enabled";
            // Drop what this profile can't read, LAST, so every layer above is filtered by the same rule and
            // GET's shown baseline and PUT's drop baseline stay identical (see the paragraph above).
            foreach (var key in baseline.Keys.ToList())
                if (!ArcadeCoreOptionApplicability.IsApplicable(core, key, profileId)) baseline.Remove(key);
            return baseline;
        }

        /// <summary>Read the per-game config for the ⚙ Configure panel: the system's catalogued options with
        /// each option's current effective value, the configured renderer, and free-form notes. Editor-only.</summary>
        [HttpGet("/API/Arcade/Game/{gameId:int}/Config")]
        public async Task<IActionResult> GetGameConfig(int gameId, [FromQuery] string profile = null)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            if (!await IsEditorAsync(userId.Value))
                return StatusCode(403, new { message = "Configuring games is editor-only." });

            var game = await movieDb.ArcadeGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId);
            if (game == null) return NotFound(new { message = "Game not found." });

            var savedProfile = await movieDb.ArcadeGameProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.System == game.System && p.TitleKey == TitleKeyOf(game));
            var saved = ParseOptionsJson(savedProfile?.CoreOptionsJson) ?? new Dictionary<string, string>(StringComparer.Ordinal);

            // Which graphics profile is selected → which CORE's options the module shows (PS1 Beetle vs
            // pcsx_rearmed have different options). A `?profile=` query previews another profile's core options
            // before saving; otherwise the saved profile, the legacy HwContext pin, then the default.
            var renderProfiles = ArcadeRendererProfiles.For(game.System);
            // What this game is PINNED to (if anything) vs. what it merely inherits — resolved by the same
            // helper the launch path and the Start menu use, so all three agree about what "default" means.
            var pinned = EffectiveRenderProfile(game.System, savedProfile?.RenderProfile, savedProfile?.HwContext);
            var selected = (!string.IsNullOrEmpty(profile) ? ArcadeRendererProfiles.Resolve(game.System, profile) : null)
                           ?? pinned.Profile;
            var core = selected?.OptionCore ?? ArcadeCoreOptionCatalog.CoreForSystem(game.System);

            // Effective value = the game's baseline (catalogued default → live system tuning → per-game
            // default-on rows; see BuildEffectiveBaseline) overlaid by the saved profile — so a quality
            // lever shows its LIVE Ultra value (e.g. beetle 4x, not the core's factory 1x) before any save,
            // and a patchable PS2 game shows widescreen ON.
            var defaultOn = await movieDb.ArcadeCheats.AsNoTracking()
                .Where(c => c.ArcadeGameId == game.Id && c.Kind == "option" && c.DefaultOn && c.OptionKey != null)
                .Select(c => new { c.OptionKey, c.OptionValue }).ToListAsync();
            var effective = BuildEffectiveBaseline(core, selected?.Id, defaultOn.Select(r => (r.OptionKey, r.OptionValue)));
            foreach (var kv in saved) effective[kv.Key] = kv.Value;

            // ⚠ Only the options that are LIVE under the selected render profile (plan D3/Phase 2). A room is a
            // (core, renderer) pair and this list used to be filtered by core alone, so both PS2 profiles
            // rendered the union — the pgs_* levers that only paraLLEl-GS reads next to the three GSdx levers
            // it provably never reads. Filtering here (not client-side) is enough because the modal re-fetches
            // on every Graphics switch (ArcadeGameConfig.js switchProfile). See
            // ArcadeCoreOptionApplicability — anything without evidence stays visible.
            var options = ArcadeCoreOptionApplicability.OptionsFor(core, selected?.Id).Select(o => new
            {
                key = o.Key,
                label = o.Label,
                category = o.Category,
                note = o.Note,
                isRange = o.IsRange,
                rangeMin = o.RangeMin,
                rangeMax = o.RangeMax,
                @default = o.Default,
                values = o.Values.Select(v => new { token = v.Token, label = v.Label }),
                value = effective.TryGetValue(o.Key, out var ev) ? ev : o.Default,
            });

            // Advanced/raw escape hatch: saved keys not in ANY of this system's cores' catalogs (hand-entered).
            // ⚠ This set is deliberately UNFILTERED by render profile. A key that IS catalogued but is merely
            // inapplicable to the selected profile (a stored GSdx pcsx2_upscale_multiplier while paraLLEl-GS is
            // selected) is a known key the module is choosing not to render — it must NOT reappear as an
            // "advanced" raw row, or hiding it would just move it, and the client would re-submit it under a
            // profile that can't read it. It is preserved instead, in SaveGameConfig's merge.
            var allSystemKeys = renderProfiles.Select(p => p.OptionCore)
                .Append(core).Where(c => c != null).Distinct()
                .SelectMany(c => ArcadeCoreOptionCatalog.ForCore(c).Select(o => o.Key))
                .ToHashSet(StringComparer.Ordinal);
            // Renderer keys are EXCLUDED here even though no catalog holds them: they are owned by the Graphics
            // selector above, and surfacing one as an advanced row let it be re-submitted on every save and
            // beat that selector in the exported overrides (see ArcadeCoreOptionCatalog.IsRendererSelecting).
            var advanced = saved.Where(kv => !allSystemKeys.Contains(kv.Key)
                                             && !ArcadeCoreOptionCatalog.IsRendererSelecting(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            return Json(new
            {
                gameId,
                system = game.System,
                title = game.Title,
                hwToggleSupported = CloudRetroHost.SupportsHwToggle(game.System),
                // The graphics profiles (renderer/core choice) for this system + the selected one.
                profiles = renderProfiles.Select(p => new { id = p.Id, label = p.Label }),
                renderProfile = selected?.Id,
                // Which of those is this SYSTEM's default, and what (if anything) this GAME pins. They are
                // different questions and conflating them is what made the Start menu misleading: a game
                // pinned to parallel_n64/Glide64 still saw "mupen64plus-next · Vulkan — default". Null
                // savedRenderProfile = the game follows the system default and keeps following it if that
                // default ever changes; the editor picks that state explicitly ("System default").
                defaultProfile = ArcadeRendererProfiles.Default(game.System)?.Id,
                savedRenderProfile = pinned.FromGame ? pinned.Profile?.Id : null,
                // The quality-tier dropdown next to "Reset to defaults" (ArcadeQualityPresets). The
                // selection isn't persisted — it's the reset target, defaulting to Ultra client-side.
                qualityTiers = ArcadeQualityPresets.Tiers.Select(t => new { id = t.Id, label = t.Label }),
                optionCore = core,
                notes = savedProfile?.Notes ?? "",
                options,
                advanced,
            });
        }

        /// <summary>Save the per-game config (upserts ArcadeGameProfile by identity). Known option values are
        /// validated against the catalog — libretro silently ignores an unknown value token, so a typo would
        /// be a toggle that does nothing. Only values that differ from the game's effective default are stored,
        /// keeping the profile minimal (and "reset to default" = drop the key). Editor-only.</summary>
        [HttpPut("/API/Arcade/Game/{gameId:int}/Config")]
        public async Task<IActionResult> SaveGameConfig(int gameId, [FromBody] GameConfigRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            if (!await IsEditorAsync(userId.Value))
                return StatusCode(403, new { message = "Configuring games is editor-only." });
            if (request == null) return BadRequest(new { message = "Invalid request." });

            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == gameId);
            if (game == null) return NotFound(new { message = "Game not found." });

            // The graphics profile being saved → the CORE whose options this save validates against. A bad
            // profile id for the system is rejected (never silently ignored → wrong core booted).
            if (!string.IsNullOrEmpty(request.RenderProfile)
                && ArcadeRendererProfiles.For(game.System).All(p => p.Id != request.RenderProfile))
                return BadRequest(new { message = $"'{request.RenderProfile}' is not a graphics profile for {game.System}.", key = "renderProfile" });
            var selected = ArcadeRendererProfiles.Resolve(game.System, request.RenderProfile);
            var core = selected?.OptionCore ?? ArcadeCoreOptionCatalog.CoreForSystem(game.System);

            // Baseline = the game's effective default (catalogued default → live system tuning → per-game
            // default-on rows; the SAME helper GET renders from). A submitted value equal to baseline is
            // dropped, so the stored profile carries only real overrides — and a left-alone quality lever
            // (now shown at its live value, e.g. beetle 4x) is NOT frozen in, keeping the game tracking the
            // yaml. Must match GET's effective baseline exactly, or a shown value round-trips into an override.
            var defaultOn = await movieDb.ArcadeCheats.AsNoTracking()
                .Where(c => c.ArcadeGameId == game.Id && c.Kind == "option" && c.DefaultOn && c.OptionKey != null)
                .Select(c => new { c.OptionKey, c.OptionValue }).ToListAsync();
            var baseline = BuildEffectiveBaseline(core, selected?.Id, defaultOn.Select(r => (r.OptionKey, r.OptionValue)));

            // A quality-tier reset ("Reset to defaults" with a tier picked): ignore any submitted values
            // and store the tier's preset for this (core, renderer) VERBATIM. Deliberately NO
            // baseline-drop here — the baseline above is the CORE's embedded default, but the live
            // default comes from config.worker-gl.yaml, and the two disagree on exactly the quality
            // levers (beetle internal res: catalog "1x(native)" vs yaml "4x"). Dropping a preset value
            // as "equal to default" would leave the yaml value in charge and make the tier silently
            // inert — the recurring silent-no-op class. Ultra's preset is empty, so an Ultra reset
            // clears the core's overrides and the game tracks the live yaml tuning.
            var tier = string.IsNullOrWhiteSpace(request.QualityTier) ? null : request.QualityTier.Trim().ToLowerInvariant();
            if (tier != null && !ArcadeQualityPresets.IsKnown(tier))
                return BadRequest(new { message = $"'{request.QualityTier}' is not a quality tier.", key = "qualityTier" });

            var keyPattern = new System.Text.RegularExpressions.Regex("^[a-z0-9][a-z0-9_-]{1,59}$");
            var toStore = new Dictionary<string, string>(StringComparer.Ordinal);
            if (tier != null)
            {
                // Presets resolve profile-id → hwContext → core-wide, so the SELECTED PROFILE has to be passed
                // in, not just its surface. PS2's parallel_gs and vulkan_gsdx share hwContext "vulkan" and read
                // disjoint levers; on hwContext alone a GSdx reset would fetch the paraLLEl-GS bundle and the
                // filter below would strip all of it, storing nothing (Phase 3, plan D6).
                // Where a bundle IS still surface-keyed it can be one notch coarser than a profile —
                // parallel_n64's "gl" bundle serves BOTH gl profiles (GLideN64 and Glide64) — so the
                // applicability filter stays as the structural backstop: a tier can never store a key that is
                // inert under the profile it was applied for, whatever the preset happens to contain.
                // ArcadeQualityPresetsTests asserts the presets don't NEED the filter (every preset key is live
                // on some profile in its scope) — the filter is what keeps that true.
                foreach (var (key, value) in ArcadeQualityPresets.For(core, selected?.Id, selected?.HwContext, tier))
                    if (ArcadeCoreOptionApplicability.IsApplicable(core, key, selected?.Id)) toStore[key] = value;
            }
            else if (request.CoreOptions != null)
            {
                // The modal posts the FULL rendered option set for the core (ArcadeGameConfig.js buildBody
                // spreads every value, not just the edited ones), so this bound MUST scale with the catalog
                // — a magic number silently turns "save" into a dead button for the biggest cores. The old
                // flat 60 predated the startup extraction folding each core's complete option set in, and
                // by 2026-08-02 it had broken Save outright for every core above it: dolphin 95, flycast 88,
                // mupen64plus_next 77, ppsspp 75, beetle_psx_hw 74, pcsx2 62, genesis_plus_gx 62. Every
                // option rendered; none could be saved (found via ps2 "No interlacing" → "Too many options").
                // Headroom covers the Advanced raw-key rows, which the catalog does not bound.
                var maxOptions = ArcadeCoreOptionCatalog.ForCore(core).Count + 64;
                if (request.CoreOptions.Count > maxOptions)
                    return BadRequest(new { message = "Too many options." });
                foreach (var (key, value) in request.CoreOptions)
                {
                    if (string.IsNullOrWhiteSpace(key) || value == null) continue;
                    // The Graphics selector owns the renderer keys — drop any that a client still submits
                    // (an older payload, or a hand-typed Advanced row) rather than letting them silently
                    // out-rank the selected profile in the export.
                    if (ArcadeCoreOptionCatalog.IsRendererSelecting(key)) continue;
                    var opt = ArcadeCoreOptionCatalog.Find(core, key);
                    if (opt != null)
                    {
                        // Known option: value must be one the core accepts, or the room would ship a dead token.
                        if (!opt.IsValidToken(value))
                            return BadRequest(new { message = $"'{value}' is not a valid value for {key}.", key });
                    }
                    else
                    {
                        // Advanced/raw escape hatch: accept an unknown key if it at least looks like a libretro
                        // option key and the value is bounded. It's the editor's own risk (documented in the UI).
                        if (!keyPattern.IsMatch(key) || value.Length > 80)
                            return BadRequest(new { message = $"'{key}' is not a valid option key.", key });
                    }
                    // Drop values equal to the game's effective default → keep the profile minimal.
                    if (baseline.TryGetValue(key, out var bv) && string.Equals(bv, value, StringComparison.Ordinal))
                        continue;
                    toStore[key] = value;
                }
            }

            var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
            if (notes is { Length: > 500 }) notes = notes.Substring(0, 500);

            var profile = await movieDb.ArcadeGameProfiles
                .FirstOrDefaultAsync(p => p.System == game.System && p.TitleKey == TitleKeyOf(game));
            if (profile == null)
            {
                profile = new ArcadeGameProfile { System = game.System, TitleKey = TitleKeyOf(game) };
                movieDb.ArcadeGameProfiles.Add(profile);
            }

            // Merge into the existing blob, preserving every saved key the module did NOT render for this
            // profile: other cores' options (the flat blob spans a system's cores) and — since Phase 2 — the
            // selected core's options that are inapplicable to the selected profile. The modal posts the FULL
            // rendered set, so an unrendered key is simply absent from the payload and would otherwise be
            // deleted by a save the editor never intended (a stored GSdx pcsx2_upscale_multiplier wiped by
            // pressing Save while paraLLEl-GS is selected). See ArcadeCoreOptionApplicability.MergeSave.
            var existing = ParseOptionsJson(profile.CoreOptionsJson) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var final = ArcadeCoreOptionApplicability.MergeSave(game.System, core, selected?.Id, existing, toStore);

            profile.CoreOptionsJson = final.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(final) : null;
            // An empty RenderProfile means "follow the system default" and must be STORED as null — not as
            // today's default id. Writing the resolved id pinned every game the moment anyone saved any
            // unrelated option, so a later change to the system default silently stopped reaching it, and
            // nothing in the UI showed the game had been pinned at all. HwContext is the legacy pin for the
            // same choice, so it clears with it or it would keep overriding on its own.
            var pinsProfile = !string.IsNullOrEmpty(request.RenderProfile);
            profile.RenderProfile = pinsProfile ? selected?.Id : null;
            profile.HwContext = pinsProfile ? selected?.HwContext : null;  // in sync for the manifest export + legacy fallback
            profile.Notes = notes;
            // ForcedFps is deliberately NOT touched here — it stays SQL/CLI-managed (see docs).
            await movieDb.SaveChangesAsync();

            return await GetGameConfig(gameId);
        }

        public sealed class GameConfigRequest
        {
            /// <summary>Chosen core-option values, key→token, for the selected profile's core. Known keys
            /// validated against that core's catalog; unknown keys allowed as the advanced escape hatch.
            /// Values equal to the game default are not stored.</summary>
            public Dictionary<string, string>? CoreOptions { get; set; }
            /// <summary>The graphics render-profile id (see ArcadeRendererProfiles), e.g. "opengl",
            /// "beetle_opengl", "pcsx_rearmed". Null/empty = follow the system default (stored as null, so
            /// the game keeps tracking that default if it changes) — the config tool's "System default".</summary>
            public string? RenderProfile { get; set; }
            /// <summary>Set = a "Reset to defaults" with this quality tier picked (ArcadeQualityPresets):
            /// CoreOptions is ignored and the tier's preset for the selected profile's (core, renderer)
            /// is stored verbatim. Null = a normal save of the submitted values.</summary>
            public string? QualityTier { get; set; }
            public string? Notes { get; set; }
        }

        // ── RetroAchievements account linking (docs plan Phase 2) ────────────────────────────────────
        // Each user links their OWN retroachievements.org account (RA ToS: one account per human). We store
        // the username plus RA's persistent CONNECT TOKEN (returned from a one-time username+password login —
        // NOT the password) in UserSettings, the token encrypted at rest with Data Protection. When such a
        // user CREATES a room, the token is decrypted and passed on the join descriptor so the worker logs
        // rcheevos in under their account. Joiners never carry creds — a room is one emulator, so RA runs
        // under the creator's account (that's the multiplayer-attribution decision).

        private const string RaUserSettingKey = "RetroAchievementsUser";
        private const string RaTokenSettingKey = "RetroAchievementsTokenProtected";

        // A dedicated protector purpose string so these ciphertexts can never be cross-used with the auth
        // cookie's or any other feature's protected payloads.
        private IDataProtector RaProtector() => dataProtection.CreateProtector("arcade.ra");

        private static readonly HttpClient raClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        private const string RaMediaBase = "https://media.retroachievements.org";

        // RetroAchievements Web API response cache. RA is a community-run service that asks consumers to be
        // gentle, so we fetch a given piece of RA data once for the WHOLE friend group. Two tiers:
        //   • DB (ArcadeRaApiCache) — the durable, restart- and replica-shared cache, and the stale-fallback
        //     when RA is unreachable. Max age per caller (definitions ~static → weeks; board lists → days).
        //   • memory — a short layer over the DB to coalesce bursts + skip a DB round-trip within a minute.
        // A user's live profile is memory-only (volatile + per-user; not worth a DB row every few minutes).
        internal static readonly TimeSpan RaDbDefs = TimeSpan.FromDays(14);      // API_GetGameExtended (achievement set)
        internal static readonly TimeSpan RaDbBoards = TimeSpan.FromDays(2);      // API_GetGameLeaderboards (board list + top entry)
        internal static readonly TimeSpan RaTtlUser = TimeSpan.FromMinutes(10);   // API_GetUserSummary (memory-only)
        private static readonly TimeSpan RaMemTtl = TimeSpan.FromMinutes(1);      // burst layer over the DB cache
        private static readonly TimeSpan RaNegativeTtl = TimeSpan.FromSeconds(60); // short cache on failure so an RA outage can't storm

        // In-flight single-flight coalescing (NOT in the sized memory cache): concurrent misses of the same
        // key await ONE fetch — so both the RA call AND the DB upsert happen once per key, no duplicate-key race.
        private static readonly ConcurrentDictionary<string, Task<string?>> RaInflight = new();

        /// <summary>GET a RetroAchievements Web API endpoint (the <c>/API/API_*.php</c> surface), served from the
        /// DB cache when fresh, else fetched once (coalesced) and persisted, with the last good copy handed back
        /// if RA is down. <paramref name="dbMaxAge"/> null = memory-only (volatile data). Appends the SITE
        /// account's <c>z</c>/<c>y</c> auth + User-Agent at fetch time only (the key never contains the key).
        /// Returns the parsed document (caller disposes it), or null — every caller degrades gracefully.</summary>
        private async Task<System.Text.Json.JsonDocument?> RaWebApiGetAsync(string apiFileAndQuery, TimeSpan memTtl, TimeSpan? dbMaxAge)
        {
            var raw = await RaWebApiRawAsync(apiFileAndQuery, memTtl, dbMaxAge);
            if (raw == null) return null;
            try { return System.Text.Json.JsonDocument.Parse(raw); }
            catch { return null; }
        }

        private async Task<string?> RaWebApiRawAsync(string apiFileAndQuery, TimeSpan memTtl, TimeSpan? dbMaxAge)
        {
            if (!RaWebApiConfigured) return null;
            var cacheKey = "ra:" + apiFileAndQuery;
            if (cache.TryGetValue(cacheKey, out string? cached)) return cached; // hot hit (a null = negative-cached failure)

            var task = RaInflight.GetOrAdd(cacheKey, _ => RaFetchAndCacheAsync(cacheKey, apiFileAndQuery, memTtl, dbMaxAge));
            try { return await task; }
            finally { RaInflight.TryRemove(new KeyValuePair<string, Task<string?>>(cacheKey, task)); }
        }

        private async Task<string?> RaFetchAndCacheAsync(string cacheKey, string apiFileAndQuery, TimeSpan memTtl, TimeSpan? dbMaxAge)
        {
            if (cache.TryGetValue(cacheKey, out string? cached)) return cached; // filled between the miss and here

            string? payload;
            if (dbMaxAge == null)
            {
                // Memory-only (volatile per-user data): no DB row.
                payload = await RaFetchStringAsync(apiFileAndQuery);
            }
            else
            {
                // A FRESH DbContext — this task is shared across concurrent requests, so it must not touch the
                // request-scoped movieDb (disposed when its request ends).
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
                var row = await db.ArcadeRaApiCaches.FirstOrDefaultAsync(c => c.CacheKey == cacheKey);
                var now = DateTime.UtcNow;
                if (row != null && row.FetchedUtc >= now - dbMaxAge.Value)
                {
                    payload = row.Payload; // DB hit (fresh) — no RA call
                }
                else
                {
                    var raw = await RaFetchStringAsync(apiFileAndQuery);
                    if (raw != null)
                    {
                        if (row == null) db.ArcadeRaApiCaches.Add(new ArcadeRaApiCache { CacheKey = cacheKey, Payload = raw, FetchedUtc = now });
                        else { row.Payload = raw; row.FetchedUtc = now; }
                        try { await db.SaveChangesAsync(); }
                        catch (DbUpdateException) { /* lost an insert race on the unique key — the other writer's row stands */ }
                        payload = raw;
                    }
                    else
                    {
                        payload = row?.Payload; // STALE-ON-ERROR: serve the last good copy rather than nothing
                    }
                }
            }

            // The memory cache is size-limited, so every entry must declare a Size (≈ payload bytes). A failure
            // (no payload at all) is negative-cached briefly so opens during an RA hiccup don't hammer it.
            cache.Set(cacheKey, payload, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = payload != null ? memTtl : RaNegativeTtl,
                Size = payload?.Length ?? 64,
            });
            return payload;
        }

        private async Task<string?> RaFetchStringAsync(string apiFileAndQuery)
        {
            var user = config.ArcadeRaWebApiUser;
            var key = config.ArcadeRaWebApiKey;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(key)) return null;
            try
            {
                var sep = apiFileAndQuery.Contains('?') ? "&" : "?";
                var url = $"https://retroachievements.org/API/{apiFileAndQuery}{sep}z={Uri.EscapeDataString(user)}&y={Uri.EscapeDataString(key)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "MovieTheaterArcade/1.0");
                using var resp = await raClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Arcade RA Web API GET failed: {Path}", apiFileAndQuery);
                return null;
            }
        }

        // RA badge/image URL builders. A locked (un-earned) achievement uses the same badge id + "_lock".
        private static string? RaBadgeUrl(string? badgeName, bool locked = false) =>
            string.IsNullOrWhiteSpace(badgeName) ? null : $"{RaMediaBase}/Badge/{badgeName}{(locked ? "_lock" : "")}.png";
        private static string? RaImageUrl(string? path) =>
            string.IsNullOrWhiteSpace(path) ? null : RaMediaBase + path;

        private bool RaWebApiConfigured =>
            !string.IsNullOrWhiteSpace(config.ArcadeRaWebApiUser) && !string.IsNullOrWhiteSpace(config.ArcadeRaWebApiKey);

        /// <summary>The signed-in user's linked RA username + decrypted connect token, or (null, null) when
        /// they haven't linked (or the stored ciphertext no longer decrypts — treated as unlinked, never
        /// thrown). Used by <see cref="CreateRoom"/> to seed the worker's rcheevos login. The token is never
        /// logged and never leaves the server except on the creator's own room descriptor.</summary>
        private async Task<(string? RaUser, string? RaToken)> LoadRaCredentialsAsync(int userId)
        {
            var rows = await movieDb.UserSettings.AsNoTracking()
                .Where(s => s.UserID == userId && (s.SettingKey == RaUserSettingKey || s.SettingKey == RaTokenSettingKey))
                .ToListAsync();
            var raUser = rows.FirstOrDefault(r => r.SettingKey == RaUserSettingKey)?.SettingValue;
            var protectedToken = rows.FirstOrDefault(r => r.SettingKey == RaTokenSettingKey)?.SettingValue;
            if (string.IsNullOrEmpty(raUser) || string.IsNullOrEmpty(protectedToken))
                return (null, null);
            try { return (raUser, RaProtector().Unprotect(protectedToken)); }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Key ring rotated past this ciphertext, or corruption — surface as "not linked" so the user
                // simply re-links, rather than 500ing every room create.
                logger.LogWarning("Arcade RA token for user {UserId} failed to decrypt; treating as unlinked.", userId);
                return (null, null);
            }
        }

        public sealed class RaLinkRequest
        {
            public string? Username { get; set; }
            public string? Password { get; set; }
        }

        /// <summary>Link (or re-link) the signed-in user's RetroAchievements account. Performs RA's one-time
        /// username+password login to obtain the persistent connect token, stores the username + the
        /// DP-encrypted token, and DISCARDS the password. Returns {linked, raUser}. The password is used only
        /// for this single server-to-RA call — it is never stored or logged.</summary>
        [HttpPost("/API/Arcade/RetroAchievements/Link")]
        public async Task<IActionResult> LinkRetroAchievements([FromBody] RaLinkRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { message = "RetroAchievements username and password are required." });

            var raUser = request.Username.Trim();

            // RA's dorequest login2: returns { Success, Token, ... }. The Token is the durable credential we
            // keep; the password never touches our storage. Post as form data (RA expects urlencoded).
            string? token;
            try
            {
                using var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("u", raUser),
                    new KeyValuePair<string, string>("p", request.Password),
                });
                using var resp = await raClient.PostAsync("https://retroachievements.org/dorequest.php?r=login2", content);
                if (!resp.IsSuccessStatusCode)
                    return StatusCode(502, new { message = "Couldn't reach RetroAchievements. Try again." });
                using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var success = root.TryGetProperty("Success", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.True;
                token = success && root.TryGetProperty("Token", out var t) ? t.GetString() : null;
                if (!success || string.IsNullOrEmpty(token))
                    return BadRequest(new { message = "RetroAchievements rejected those credentials." });
                // RA echoes the canonical-cased username; prefer it so display matches their site.
                if (root.TryGetProperty("User", out var u) && u.GetString() is { Length: > 0 } canon)
                    raUser = canon;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Arcade RA login failed for user {UserId}.", userId); // never logs credentials
                return StatusCode(502, new { message = "Couldn't reach RetroAchievements. Try again." });
            }

            await UpsertUserSettingAsync(userId.Value, RaUserSettingKey, raUser);
            await UpsertUserSettingAsync(userId.Value, RaTokenSettingKey, RaProtector().Protect(token!));
            await movieDb.SaveChangesAsync();

            return Json(new { linked = true, raUser });
        }

        /// <summary>Whether the signed-in user has RA linked, and under what username — for the settings UI.</summary>
        [HttpGet("/API/Arcade/RetroAchievements/Status")]
        public async Task<IActionResult> RetroAchievementsStatus()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var (raUser, raToken) = await LoadRaCredentialsAsync(userId.Value);
            return Json(new { linked = raUser != null && raToken != null, raUser });
        }

        /// <summary>PULL a user's real RetroAchievements profile (points, rank, recent unlocks) via the RA
        /// Web API, to show their genuine RA activity on the site alongside our friends-board mirror
        /// (arcade-ra-sync-plan.md, the "pull" half of RA sync). Read-only and keyed by the SITE service
        /// account's Web API key (public data — no per-user token needed), so it's safe for any signed-in
        /// user to view any user's. Resolves the target's linked RA username from their settings; returns
        /// {configured, linked, ...}. Degrades cleanly: not-configured (no site Web API key) and not-linked
        /// (the target never linked) are both 200s with the flag false, never an error — the UI just hides.</summary>
        [HttpGet("/API/Arcade/RetroAchievements/Profile")]
        public async Task<IActionResult> RetroAchievementsProfile(int? userId = null)
        {
            var me = GetCurrentUserId();
            if (me == null) return Unauthorized();
            var targetUserId = userId ?? me.Value;

            var webUser = config.ArcadeRaWebApiUser;
            var webKey = config.ArcadeRaWebApiKey;
            if (string.IsNullOrWhiteSpace(webUser) || string.IsNullOrWhiteSpace(webKey))
                return Json(new { configured = false, linked = false });

            // The target's linked RA username (public identity — the token isn't needed for a Web API pull).
            var raUser = await movieDb.UserSettings.AsNoTracking()
                .Where(s => s.UserID == targetUserId && s.SettingKey == RaUserSettingKey)
                .Select(s => s.SettingValue).FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(raUser))
                return Json(new { configured = true, linked = false });

            try
            {
                // API_GetUserSummary returns score + rank + recent achievements in one call. Memory-only
                // cache (10 min): a profile is volatile + per-user, so it isn't worth a DB row.
                using var doc = await RaWebApiGetAsync($"API_GetUserSummary.php?u={Uri.EscapeDataString(raUser)}&g=5&a=10", RaTtlUser, null);
                if (doc == null)
                    return Json(new { configured = true, linked = true, raUser, available = false });
                var root = doc.RootElement;
                string? Str(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                long Num(string k) => root.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;

                // RecentAchievements is an object keyed by gameId → { achId → {...} }; flatten the newest few.
                var recent = new List<object>();
                if (root.TryGetProperty("RecentAchievements", out var games) && games.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var game in games.EnumerateObject())
                        if (game.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                            foreach (var ach in game.Value.EnumerateObject())
                            {
                                var a = ach.Value;
                                recent.Add(new
                                {
                                    id = a.TryGetProperty("ID", out var id) ? id.ToString() : ach.Name,
                                    title = a.TryGetProperty("Title", out var t) ? t.GetString() : null,
                                    points = a.TryGetProperty("Points", out var p) && p.TryGetInt32(out var pv) ? pv : 0,
                                    hardcore = a.TryGetProperty("HardcoreMode", out var h) && (h.ToString() == "1"),
                                    dateAwarded = a.TryGetProperty("DateAwarded", out var d) ? d.GetString() : null,
                                    raUrl = a.TryGetProperty("ID", out var id2) ? "https://retroachievements.org/achievement/" + id2 : null,
                                });
                            }
                }

                return Json(new
                {
                    configured = true,
                    linked = true,
                    available = true,
                    raUser,
                    totalPoints = Num("TotalPoints"),
                    totalSoftcorePoints = Num("TotalSoftcorePoints"),
                    rank = Num("Rank"),
                    memberSince = Str("MemberSince"),
                    profileUrl = "https://retroachievements.org/user/" + Uri.EscapeDataString(raUser),
                    recent,
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Arcade RA profile pull failed for user {UserId}.", targetUserId);
                return Json(new { configured = true, linked = true, raUser, available = false });
            }
        }

        /// <summary>Unlink the signed-in user's RA account (drops both stored rows).</summary>
        [HttpDelete("/API/Arcade/RetroAchievements/Link")]
        public async Task<IActionResult> UnlinkRetroAchievements()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var rows = await movieDb.UserSettings
                .Where(s => s.UserID == userId.Value && (s.SettingKey == RaUserSettingKey || s.SettingKey == RaTokenSettingKey))
                .ToListAsync();
            if (rows.Count > 0)
            {
                movieDb.UserSettings.RemoveRange(rows);
                await movieDb.SaveChangesAsync();
            }
            return Json(new { linked = false });
        }

        // Find-or-add a single UserSettings row (the inline upsert pattern used across the controllers). The
        // caller batches the SaveChangesAsync so multiple upserts commit together.
        private async Task UpsertUserSettingAsync(int userId, string key, string value)
        {
            var row = await movieDb.UserSettings.FirstOrDefaultAsync(s => s.UserID == userId && s.SettingKey == key);
            if (row == null)
                movieDb.UserSettings.Add(new UserSettings { UserID = userId, SettingKey = key, SettingValue = value });
            else
                row.SettingValue = value;
        }

        [HttpPost("/API/Arcade/Room")]
        public async Task<IActionResult> CreateRoom([FromBody] CreateRoomRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });
            if (request == null)
                return BadRequest(new { message = "Invalid request." });

            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == request.GameId && g.IsEnabled);
            if (game == null)
                return NotFound(new { message = "Game not found." });

            // Heavy-lane titles are Moonlight-streamed by default (docs/arcade-heavy-lane-plan.md). A heavy
            // title with a CloudRetroGameKey ALSO offers the browser "capture" lane (H5): the room routes to
            // the capture worker, which launches the native program and streams the desktop. Both launch
            // paths coexist — the card keeps its Artemis button. A heavy title WITHOUT a capture key stays
            // Moonlight-only.
            var isHeavy = string.Equals(game.Lane, "heavy", StringComparison.OrdinalIgnoreCase);
            var isCapture = isHeavy && CloudRetroHost.IsCaptureEnabled(game.CloudRetroGameKey);
            if (isHeavy && !isCapture)
                return BadRequest(new { message = "This title plays via Moonlight, not in the browser — use its card's Play instructions." });

            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);
            if (game.RatingCeiling > ageRestriction)
                return StatusCode(403, new { message = "This game isn't available on your account." });

            // Best-effort cap: our count is advisory (CloudRetro's t=112 is the real backstop). 0 = no
            // local cap (mirrors StreamingMaxConcurrentTranscodes semantics).
            if (host.MaxConcurrentRooms > 0 && rooms.LiveRoomCount() >= host.MaxConcurrentRooms)
                return StatusCode(503, new { message = "The arcade is full — every machine is in use. Try again in a few minutes." });

            var roomCode = NewRoomCode();

            // Competitive mode is inert for capture/heavy (native) rooms — the save-state/cheat/hardcore
            // levers all live on the retro (CloudRetro) path. Persist the creator's intent on the durable
            // session row so joiners (and a post-restart rehydrate) can read how the room runs from the DB.
            var competitive = request.Competitive && !isCapture;

            var session = new ArcadeSession
            {
                ArcadeGameId = game.Id,
                RoomCode = roomCode,
                CreatedByUserId = userId.Value,
                IsCompetitive = competitive,
                CreatedUtc = DateTime.UtcNow,
            };
            movieDb.ArcadeSessions.Add(session);
            await movieDb.SaveChangesAsync();

            // Per-room codec (worker patch 0036): allowlist hard — this string reaches the worker's
            // encoder selection, never forward a free-form value. "" = worker config default (AV1).
            var codec = request.VideoCodec?.Trim().ToLowerInvariant() switch
            {
                "h264" => "h264",
                "av1" => "av1",
                _ => "",
            };

            // Per-room Wii controller-scheme (room-create picker): computed here, before CreateRoom,
            // because — unlike hwctx/bitrate — it changes what button bits EVERY player's client must
            // send, not just the creator's one-time CoreLoad, so it has to live in room state for
            // Join/ClaimSeat to hand to joiners too (mirrors codec's own reason for the same thing).
            var ctrlScheme = CloudRetroHost.SupportsControllerScheme(game.System)
                ? request.ControllerScheme?.Trim().ToLowerInvariant() switch { "wiimote" => "wiimote", "gc" => "gc", _ => "" }
                : "";

            // Register live state with the creator in seat 0. The CloudRetro room isn't created yet — the
            // creator's browser does that (empty room_id) and then calls Bind (§8 steps 2–3).
            rooms.CreateRoom(roomCode, game.Id, game.MaxPlayers, userId.Value, codec, ctrlScheme);

            string launchKey;
            int discCount;
            string roomSystem;
            string saveSystem;
            var gameCoreOptions = new Dictionary<string, string>(StringComparer.Ordinal);
            ArcadeRendererProfiles.RenderProfile? renderProfile = null;
            if (isCapture)
            {
                // The capture launch key IS the CloudRetroGameKey (== the heavy descriptor id == the
                // worker's .capture stub filename). The room's system is the literal "capture" so the
                // gateway routes it to the capture worker (zone) and the worker's branch fires on it.
                launchKey = game.CloudRetroGameKey!;
                discCount = 0;
                roomSystem = "capture";
                saveSystem = "capture";
            }
            else
            {
                (launchKey, discCount) = await ResolveLaunchAsync(game);
                roomSystem = game.System;

                // Resolve the game's saved config + its render profile NOW — the profile's core determines the
                // SAVE namespace (a different core writes incompatible save-states), so it must be known before
                // the saveId is minted. Precedence: an explicit per-launch profile pick from the play-button
                // dropdown wins (it can name an alternate CORE, e.g. parallel_n64, that a bare gl/vulkan can't);
                // else the bare Force GL/Vulkan override; else the game's saved render profile; else the legacy
                // bare HwContext pin; else the system default. An unknown/invalid id falls through, never fails.
                var (opts, gameHwContext, savedRenderProfileId) = await ResolveGameConfigAsync(game);
                gameCoreOptions = opts;
                if (CloudRetroHost.SupportsHwToggle(game.System))
                {
                    var explicitProfile = !string.IsNullOrEmpty(request.RenderProfile)
                        ? ArcadeRendererProfiles.For(game.System).FirstOrDefault(p => p.Id == request.RenderProfile)
                        : null;
                    renderProfile =
                        explicitProfile                                                             // play-button profile pick (may swap core)
                        ?? ArcadeRendererProfiles.ForRenderer(game.System, request.HwContext)        // bare Force GL/Vulkan
                        ?? (savedRenderProfileId != null
                                ? ArcadeRendererProfiles.Resolve(game.System, savedRenderProfileId) // saved profile
                                : null)
                        ?? ArcadeRendererProfiles.ForRenderer(game.System, gameHwContext)            // legacy HwContext pin
                        ?? ArcadeRendererProfiles.Default(game.System);                              // system default

                    // Resuming a save CHOOSES THE CORE for you. A save-state is a dump of one core's
                    // memory, so a slot written by parallel_n64 restores nothing on stock mupen — and the
                    // player has no way to know which core they were on weeks later, nor should they have
                    // to: they clicked "Resume THIS save". Before this, the launch used whatever profile
                    // the play button carried, minted a room in the OTHER core's save namespace, and the
                    // pick quietly did nothing (2026-07-26, Mario BAZR: three saves, all "the same spot").
                    // This overrides even an explicit dropdown pick, because the two are one click in the
                    // UI and a resume onto the wrong core is never what was meant.
                    if (request.SeedSlot > 0)
                    {
                        var savedOn = await movieDb.ArcadeSaves
                            .Where(s => s.UserId == userId.Value && s.ArcadeGameId == game.Id
                                        && s.Kind == "state" && s.SlotId == request.SeedSlot)
                            .Select(s => s.System)
                            .FirstOrDefaultAsync();
                        var needCore = SaveCoreKey(roomSystem, savedOn);
                        if (needCore != null && !string.Equals(needCore, renderProfile?.CoreKey ?? "", StringComparison.Ordinal))
                        {
                            var match = ArcadeRendererProfiles.For(game.System)
                                .Where(p => string.Equals(p.CoreKey ?? "", needCore, StringComparison.Ordinal))
                                // Keep the surface they'd otherwise have got when that core offers it, so
                                // switching cores doesn't also silently downgrade the renderer.
                                .OrderByDescending(p => p.HwContext == renderProfile?.HwContext)
                                .ThenByDescending(p => p.IsDefault)
                                .FirstOrDefault();
                            if (match != null)
                            {
                                logger.LogInformation(
                                    "Arcade resume slot {Slot} for game {Game}: launching {Profile} — that save was written on {SavedOn}",
                                    request.SeedSlot, game.Id, match.Id, savedOn);
                                renderProfile = match;
                            }
                        }
                    }

                    // Merge the profile's renderer-selecting options as a BASE beneath the saved config
                    // (an explicit per-game option still wins) — flipping the surface alone strands cores
                    // that pick their renderer from a core-option (N64 paraLLEl-RDP on a GL surface = no video).
                    if (renderProfile != null && renderProfile.Options.Count > 0)
                    {
                        var merged = new Dictionary<string, string>(renderProfile.Options, StringComparer.Ordinal);
                        foreach (var kv in gameCoreOptions) merged[kv.Key] = kv.Value;
                        gameCoreOptions = merged;
                    }
                }

                // The blob may legitimately hold BOTH cores' keys on a multi-core system (Last Impact
                // stores the mupen twins of its parallel_n64 fix so a forced mupen launch keeps working) —
                // but the ROOM gets only what its booting core can read, or the other core's keys arrive
                // dead and bury the worker's reconcile signal in known noise (the 2026-08-02 sweep's
                // "cross-namespace bleed"). Storage stays whole; delivery is per-core.
                var (deliverable, droppedForeign) = ArcadeRoomOptionDelivery.FilterForBootingCore(
                    game.System, renderProfile?.OptionCore, gameCoreOptions);
                if (droppedForeign.Count > 0)
                {
                    gameCoreOptions = deliverable;
                    logger.LogInformation(
                        "Arcade room {Room} ({System}/{Core}): withheld {Count} other-core option key(s): {Keys}",
                        roomCode, game.System, renderProfile?.OptionCore ?? "default", droppedForeign.Count,
                        string.Join(", ", droppedForeign));
                }

                // Saves are per-CORE: an alternate core (PS1 pcsx_rearmed) writes save-states the default core
                // (Beetle) can't read — namespace them, or the gateway seeds a foreign state every boot and
                // crash-loops the room (arcade hard rule). Both Beetle renderers share the core, so only a
                // real core-key swap gets a suffix; the default core keeps the bare system (existing saves).
                saveSystem = !string.IsNullOrEmpty(renderProfile?.CoreKey)
                    ? roomSystem + "-" + renderProfile!.CoreKey
                    : roomSystem;
            }

            // Durable, user-scoped saves (docs/arcade-saves-plan.md): instead of an empty room id ("create
            // a random room"), the creator carries a DETERMINISTIC id encoding (user, game, slot 0, system)
            // with the launch key as the CloudRetro-resolvable suffix. This makes the session's save files
            // predictable, so the gateway seeds this user's save before boot and harvests it after — the
            // save belongs to the user+game, not the room. Slot 0 = the "Continue" slot (multi-slot is S3).
            // Capture rooms encode system="capture" here: HeavyVault owns their saves (the gateway skips the
            // CloudRetro save mount for them), and the worker parses this id for the owner + heavy app id.
            var saveId = ArcadeSaveId.Mint(userId.Value, game.Id, 0, saveSystem, launchKey);
            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, launchKey, roomSystem),
                roomCode, cloudRetroRoomId: saveId, playerSlot: 0, isCreator: true);

            // Competitive room: signal the gateway (via ?competitive=1) NOT to seed a save-state at boot,
            // so the run isn't resumed from an earlier state — that's what keeps a leaderboard time/score
            // legit and RA hardcore valid. Deliberately NOT ?fresh=1 (which CLEARS the mount and would nuke
            // the player's battery/SRAM + provoke a harvest clobber): competitive means "don't LOAD a state",
            // not "wipe my saves". The gateway/worker (Phase 1) honor this flag for both the boot seed and
            // the harvest (a competitive run must not overwrite the casual Continue save). A competitive room
            // ignores NewGame/SeedSlot — there is nothing to resume.
            // The RetroAchievements hash we already computed for THIS dump (arcade-ra-hash). The shim puts
            // it in t=104 and the worker loads that game directly instead of identifying the file itself.
            //
            // For five whole systems this is the difference between a tracked room and nothing at all:
            // rc_hash cannot READ our compressed disc containers (.cso on PS2/PSP, .gcz on GameCube, .chd
            // on Dreamcast/Saturn), so identify-at-boot fails with "Unknown game" however healthy the rest
            // of the RA session is. Hashing them offline — where we can decompress — and handing the answer
            // to the worker sidesteps the reader entirely. It is a small win everywhere else too: the ROM
            // is not reopened and re-read at boot just to learn what it already is.
            if (!string.IsNullOrWhiteSpace(game.RaHash))
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&rahash=" + Uri.EscapeDataString(game.RaHash!) };

            if (competitive)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&competitive=1" };
            // "New game": tell the gateway (via ?fresh=1 on the WS URL) to clear the mount so the game
            // boots clean instead of resuming the saved slot. Safe unsigned — it only clears the owner's own save.
            else if (request.NewGame)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&fresh=1" };
            // Resume-from-snapshot: seed a chosen snapshot slot's bytes into the room (arcade-saves-plan S3).
            else if (request.SeedSlot > 0)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&seedslot=" + request.SeedSlot };

            // Per-room encoder quality (arcade per-room bitrate/FEC): the creator picks a stream quality +
            // network-resilience (FEC) in the lobby. Ride the same WS-URL flags the rest of room-create uses;
            // the shim reads ?vbr/?fec and puts them in t=104, and the worker applies them to THIS room's
            // encoder copy. Clamp defensively (the worker clamps again). Only the creator carries these.
            // 0 / absent = "Auto": pick a default from the game's system, because encoded resolution varies
            // ~4.6x across systems (912x672 arcade vs 1280x1056 GameCube) and a flat bitrate starves the
            // big ones. See CloudRetroHost.DefaultVideoBitrateKbps. An explicit lobby choice always wins.
            // "Auto" (0/absent) now means "let the WORKER derive it from the frame it actually encodes"
            // (worker abr.go autoCeilingKbps) rather than "look it up per system here". The table this
            // used to call could not be right: it was measured once, in July 2026, against one
            // configuration, and it is blind to the core's real viewport, its `scale`, a core that
            // changes resolution mid-game, and render profiles that move the frame wholesale — the N64
            // screensize option alone spans 320x240 to 1920x1440 while the table said a flat 11000.
            // Measured consequence: 2D rooms sat at 5000 (0.129 bits/px/frame on a 960x672 Genesis
            // frame) and visibly blocked, while every 3D system had been raised off that same default.
            //
            // CAPTURE derives too, as of ABR plan Phase 2 (2026-08-04). The lane historically pinned the
            // table's flat 12000 because its worker binary predated autoCeilingKbps — both lanes now
            // build from the same cmd/worker, so Auto (0) lets the worker derive from the frame it
            // actually encodes: 1920x1080@60 x 0.18 bpp ≈ 22.4 Mbps, ABR-governed like any ceiling.
            // ⚠ Only flip this after verifying BY HASH that the deployed capture worker matches the GL
            // build — an older capture binary given vbr=0 falls to its yaml default, BELOW the old 12000.
            // ⚠ Upper clamp is 40000 to match the lobby's top preset ("Fiber · 40 Mbps") AND the worker's
            // abrAutoMaxKbps — three places that move together. It was 20000 once, which silently turned a
            // "LAN · 25 Mbps" pick into 20 Mbps — the kind of mismatch that reads as "the setting does
            // nothing". 25000 (2026-07-30 → 2026-09-02) was sized to Ziggy's old ~35 Mbps uplink; the
            // 2026-09-01 fiber cutover measured ~200 Mbps single-stream / ~630 Mbps aggregate up, so the
            // cap is now about a viewer's downlink + decoder, not our egress.
            var vbr = request.VideoBitrateKbps > 0
                ? Math.Clamp(request.VideoBitrateKbps, 500, 40000)
                : 0;
            if (vbr > 0)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&vbr=" + vbr };
            if (request.AudioFec is 1 or 2)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&fec=" + request.AudioFec };
            // In-frame packet pacing (patch 0028). Capture rooms DEFAULT it to 8 ms server-side: the
            // capture stream is the fattest we send (~22 Mbps derived H.264, intra-refresh ⇒ every
            // frame is sizable), and un-paced bursts on tablet WiFi queue behind each other and jitter the
            // audio packets sharing the air, growing the browser's audio jitter buffer (plan §12C). An
            // explicit lobby choice always wins, INCLUDING an explicit 0 (LAN on a capture room): only a
            // null (no deliberate choice) falls to the lane default.
            var paceMs = request.PaceMs ?? (isCapture ? 8 : 0);
            if (paceMs > 0)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&pace=" + Math.Clamp(paceMs, 1, 20) };
            // Codec rides the WS URL like vbr/fec — but unlike them it ALSO rides every joiner's URL
            // (see Join/ClaimSeat): the shim echoes it in INIT_WEBRTC, where each peer's track mime is
            // fixed, and every track must match the room's one encoder.
            if (codec != "")
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&codec=" + codec };

            // Apply the resolved render profile to the descriptor (the game's config + play-button choice were
            // resolved above, before the saveId, because the profile's core sets the save namespace). CoreKey →
            // &core= (worker StartGameRequest.Core → alternate core lib); HwContext → &hwctx= (surface). These
            // ride only the creator's one-time CoreLoad, not joiners. Meaningless for capture rooms. The
            // renderer-selecting options were already merged into gameCoreOptions upstream.
            if (renderProfile != null && !isCapture)
            {
                if (!string.IsNullOrEmpty(renderProfile.CoreKey))
                    descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&core=" + Uri.EscapeDataString(renderProfile.CoreKey) };
                if (!string.IsNullOrEmpty(renderProfile.HwContext))
                    descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&hwctx=" + renderProfile.HwContext };
            }

            // Which core booted, and therefore whether this room can rewind. The core is what decides
            // (ArcadeRewindSupport), and only the server knows it — the profile was resolved up there
            // from a play-button pick, a saved profile, or a resume-slot's save namespace, none of which
            // the browser sees. Joiners recover the same pair from the bound save id; see Join.
            var roomCoreKey = isCapture ? "" : (renderProfile?.CoreKey ?? "");
            descriptor = descriptor with
            {
                CoreKey = roomCoreKey,
                CanRewind = ArcadeRewindSupport.IsArmed(roomSystem, roomCoreKey),
            };

            // Per-room Wii controller-scheme override (computed above, before CreateRoom, since it also
            // needs to land in room state for joiners): ride the creator's own descriptor too.
            if (ctrlScheme != "" && !isCapture)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&ctrlscheme=" + ctrlScheme };

            // Cheats are now codes-only (the emulator/quality OPTIONS moved to the per-game config tool and
            // ride gameCoreOptions above). Resolve the code ids the creator ticked against what this exact ROM
            // actually offers — never trust the client's idea of what a cheat is, because a code is a raw memory
            // poke and one aimed at another game's addresses corrupts state rather than failing.
            if (!isCapture)
            {
                // Competitive rooms take NO cheats — a memory poke would void a legit run (and RA hardcore).
                List<string> codes = new();
                if (!competitive && request.Cheats is { Count: > 0 })
                {
                    var offered = await BuildCheatListAsync(game); // codes only
                    codes = offered.Where(o => o.Kind == "code" && !string.IsNullOrEmpty(o.Code)
                                              && request.Cheats.Contains(o.Id, StringComparer.Ordinal))
                        .Take(ArcadeCheatCatalog.MaxCheatsPerRoom).Select(o => o.Code!).ToList();
                }

                // Some systems need a core option before ANY code is honoured — Dolphin discards every cheat
                // unless dolphin_cheats_enabled is on, and its default is off. Only for rooms that actually
                // took a cheat, and never over a value the room set explicitly.
                if (codes.Count > 0)
                    foreach (var (impliedKey, impliedValue) in ArcadeCheatCatalog.ImpliedOptionsForSystem(game.System))
                        if (!gameCoreOptions.ContainsKey(impliedKey))
                            gameCoreOptions[impliedKey] = impliedValue;

                if (gameCoreOptions.Count > 0 || codes.Count > 0)
                    descriptor = descriptor with
                    {
                        CoreOptions = gameCoreOptions.Count > 0 ? gameCoreOptions : null,
                        CheatCodes = codes.Count > 0 ? codes : null,
                    };

                // RetroAchievements: the worker runs a single SITE service account as the scoring engine
                // (spectator mode — never earns), so NO per-user creds are sent here. All the room needs to
                // tell the worker is whether it's a COMPETITIVE (legit) run — that rides the descriptor's
                // `competitive` flag to t=104, and the worker mirrors achievements/scores/times to the site
                // attributed to the room host. Achievements/leaderboards are recorded for every room.
            }

            return Json(ToJson(descriptor, discCount, competitive));
        }

        // ── Durable saves (docs/arcade-saves-plan.md) ────────────────────────────────────────────────

        /// <summary>Internal callback the gateway POSTs after harvesting a save file, so the shared app DB
        /// mirrors the on-disk store (the k8s pod can't read Ziggy's disk; it needs these rows for the
        /// resume UI). Gated by the shared arcade secret, NOT a user session — it's server-to-server.
        /// Upserts on the (user, game, kind, slot) unique key so a re-harvest updates in place.</summary>
        [AllowAnonymous]
        [HttpPost("/API/Arcade/Internal/SaveHarvested")]
        public async Task<IActionResult> SaveHarvested([FromBody] SaveHarvestedRequest req)
        {
            var secret = config.ArcadeTokenSecret;
            if (string.IsNullOrEmpty(secret) ||
                !string.Equals(Request.Headers["X-Arcade-Internal-Secret"].ToString(), secret, StringComparison.Ordinal))
                return Unauthorized();
            if (req == null || string.IsNullOrEmpty(req.Kind) || string.IsNullOrEmpty(req.StorageRelPath))
                return BadRequest();

            var nowUtc = DateTime.UtcNow;
            var row = await movieDb.ArcadeSaves.FirstOrDefaultAsync(s =>
                s.UserId == req.UserId && s.ArcadeGameId == req.ArcadeGameId && s.Kind == req.Kind && s.SlotId == req.SlotId);
            if (row == null)
            {
                movieDb.ArcadeSaves.Add(new ArcadeSave
                {
                    UserId = req.UserId, ArcadeGameId = req.ArcadeGameId, System = req.System ?? "", Kind = req.Kind,
                    SlotId = req.SlotId, Label = req.Label, CoreName = req.CoreName, CoreVersion = req.CoreVersion,
                    StorageRelPath = req.StorageRelPath, SizeBytes = req.SizeBytes, Sha256 = req.Sha256,
                    Source = string.IsNullOrEmpty(req.Source) ? "online" : req.Source, IsAutosave = req.IsAutosave,
                    CreatedUtc = nowUtc, UpdatedUtc = nowUtc,
                });
            }
            else
            {
                row.System = req.System ?? row.System;
                row.StorageRelPath = req.StorageRelPath;
                row.SizeBytes = req.SizeBytes;
                row.Sha256 = req.Sha256;
                row.CoreName = req.CoreName;
                row.CoreVersion = req.CoreVersion;
                row.IsAutosave = req.IsAutosave;
                if (req.Label != null) row.Label = req.Label;
                row.UpdatedUtc = nowUtc;
            }
            await movieDb.SaveChangesAsync();
            // 204 (not 200) so the gateway can tell a real success from the SPA fallback's 200 that an
            // unmatched /API route returns during a deploy window — see the gateway's mirror callback.
            return NoContent();
        }

        // ── RetroAchievements mirror (docs plan Phase 4) ─────────────────────────────────────────────
        // rcheevos in the worker is the SOURCE OF TRUTH — it submits unlocks/leaderboard runs to
        // retroachievements.org under the player's OWN account. These callbacks are how the worker (via the
        // gateway, secret-gated, exactly like SaveHarvested) MIRRORS those events into our DB for site UI:
        // the in-room toast, the profile "My Achievements" list, and the friends-only leaderboards. The pod
        // can't read Ziggy's disk or talk to RA, so it needs this push.

        /// <summary>Time formats where LOWER is better (speedrun boards). Everything else (SCORE/VALUE/…) is
        /// higher-is-better. Used to decide whether a new leaderboard result beats the stored best.</summary>
        private static bool LowerIsBetter(string? format) => format?.ToUpperInvariant() switch
        {
            "TIME" or "FRAMES" or "MILLISECS" or "CENTISECS" or "TIMESECS" or "MINUTES" or "SECS_AS_MINS" => true,
            _ => false,
        };

        // Resolve the site user for an RA event. The gateway forwards the room creator's UserId from the join
        // token (authoritative — that's who the emulator/RA session belongs to); fall back to matching the RA
        // username against who linked it. Returns null if neither resolves (event is dropped, never 500s).
        private async Task<int?> ResolveRaUserIdAsync(int userIdFromToken, string? raUser)
        {
            if (userIdFromToken > 0) return userIdFromToken;
            if (string.IsNullOrWhiteSpace(raUser)) return null;
            var row = await movieDb.UserSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SettingKey == RaUserSettingKey && s.SettingValue == raUser);
            return row?.UserID;
        }

        public sealed class AchievementUnlockedRequest
        {
            /// <summary>Room creator's site user id from the join token (authoritative owner of the RA session).</summary>
            public int UserId { get; set; }
            public string? RaUser { get; set; }
            public int? ArcadeGameId { get; set; }
            public string? RaGameHash { get; set; }
            public long RaAchievementId { get; set; }
            public string? Title { get; set; }
            public int Points { get; set; }
            public bool Hardcore { get; set; }
            /// <summary>Run-legitimacy taints the worker sampled at unlock: cheat codes active, a save-STATE
            /// was loaded mid-run, or fast-forward/rewind was used. Drive the friends board / profile why-icon.</summary>
            public bool Cheat { get; set; }
            public bool Savescum { get; set; }
            public bool Timeplay { get; set; }
            public DateTime? UnlockedUtc { get; set; }
        }

        /// <summary>Mirror one RetroAchievements unlock into our DB (idempotent on (user, achievement,
        /// hardcore)). Secret-gated server-to-server, like <see cref="SaveHarvested"/>.</summary>
        [AllowAnonymous]
        [HttpPost("/API/Arcade/Internal/AchievementUnlocked")]
        public async Task<IActionResult> AchievementUnlocked([FromBody] AchievementUnlockedRequest req)
        {
            if (!IsInternalCallerAuthorized()) return Unauthorized();
            if (req == null || req.RaAchievementId <= 0) return BadRequest();

            var userId = await ResolveRaUserIdAsync(req.UserId, req.RaUser);
            if (userId == null) return NoContent(); // unknown player — nothing to attribute, don't error the worker

            var nowUtc = DateTime.UtcNow;
            // Dedupe on OBSERVED cleanliness, not the room mode: a re-harvest of the same unlock at the same
            // legitimacy updates in place, but earning it cleanly after a dirty unlock is a genuine first and
            // gets its own row. Clean is a computed column, so we mirror its definition to find the match.
            var incomingClean = !req.Cheat && !req.Savescum && !req.Timeplay;
            var row = await movieDb.ArcadeAchievementUnlocks.FirstOrDefaultAsync(a =>
                a.UserId == userId.Value && a.RaAchievementId == req.RaAchievementId && a.Clean == incomingClean);
            if (row == null)
            {
                movieDb.ArcadeAchievementUnlocks.Add(new ArcadeAchievementUnlock
                {
                    UserId = userId.Value,
                    RaUser = req.RaUser ?? "",
                    ArcadeGameId = req.ArcadeGameId,
                    RaGameHash = req.RaGameHash,
                    RaAchievementId = req.RaAchievementId,
                    Title = req.Title,
                    Points = req.Points,
                    // The wire still calls the room mode `hardcore` (t=104 / worker mirror); we store it as
                    // provenance only. Clean is computed by the DB from the taints below.
                    Competitive = req.Hardcore,
                    Cheat = req.Cheat,
                    Savescum = req.Savescum,
                    Timeplay = req.Timeplay,
                    UnlockedUtc = req.UnlockedUtc ?? nowUtc,
                });
            }
            else
            {
                // Re-harvest (e.g. gateway retry): refresh mutable display fields, keep the earliest unlock.
                row.Title = req.Title ?? row.Title;
                row.Points = req.Points;
                if (req.ArcadeGameId != null) row.ArcadeGameId = req.ArcadeGameId;
                if (!string.IsNullOrEmpty(req.RaGameHash)) row.RaGameHash = req.RaGameHash;
            }
            await movieDb.SaveChangesAsync();
            return NoContent();
        }

        /// <summary>
        /// The RA achievement ids this player has ALREADY unlocked, so the worker can stop re-toasting
        /// them every session.
        ///
        /// Why the worker cannot know this by itself: the rcheevos runtime is a single site service
        /// account in SPECTATOR mode (it evaluates trigger definitions locally and never submits), so it
        /// has no per-user unlock history to load. It starts every session blank and re-raises every
        /// achievement the moment its trigger fires again — which is exactly the repeat-popup complaint.
        /// The DB is the only place that history exists, hence this lookup.
        ///
        /// Returns both sets because a re-earn is not always noise: `clean` are the ones already earned
        /// legitimately (never toast again), `all` includes dirty unlocks (earning it CLEANLY afterwards
        /// is a genuine first and still deserves its toast — the unlock table treats it as a new row for
        /// the same reason, keying on (UserId, RaAchievementId, Clean)).
        /// </summary>
        [AllowAnonymous]
        [HttpGet("/API/Arcade/Internal/UnlockedAchievements")]
        public async Task<IActionResult> UnlockedAchievements(int userId, string? raUser)
        {
            if (!IsInternalCallerAuthorized()) return Unauthorized();

            var resolved = await ResolveRaUserIdAsync(userId, raUser);
            if (resolved == null) return Ok(new { all = Array.Empty<long>(), clean = Array.Empty<long>() });

            // Not scoped to a game: RA achievement ids are globally unique, the per-user row count is
            // small, and scoping by ArcadeGameId would miss unlocks mirrored from a different version
            // row of the same title.
            var rows = await movieDb.ArcadeAchievementUnlocks
                .Where(a => a.UserId == resolved.Value)
                .Select(a => new { a.RaAchievementId, a.Clean })
                .ToListAsync();

            return Ok(new
            {
                all = rows.Select(r => r.RaAchievementId).Distinct().ToArray(),
                clean = rows.Where(r => r.Clean).Select(r => r.RaAchievementId).Distinct().ToArray(),
            });
        }

        public sealed class LeaderboardSubmittedRequest
        {
            public int UserId { get; set; }
            public string? RaUser { get; set; }
            public int? ArcadeGameId { get; set; }
            public string? RaGameHash { get; set; }
            public long RaLeaderboardId { get; set; }
            public string? Title { get; set; }
            public long Value { get; set; }
            public string? Format { get; set; }
            public bool Hardcore { get; set; }
            /// <summary>Run-legitimacy taints for this submission (see <see cref="AchievementUnlockedRequest"/>).
            /// Kept in step with the recorded best — a new best overwrites the stored taints.</summary>
            public bool Cheat { get; set; }
            public bool Savescum { get; set; }
            public bool Timeplay { get; set; }
            public DateTime? AchievedUtc { get; set; }
        }

        /// <summary>Mirror one RetroAchievements leaderboard submission, keeping only the user's BEST per board
        /// (by <see cref="LeaderboardSubmittedRequest.Format"/> — lower for time boards, higher for score).
        /// A worse later attempt is ignored. Secret-gated server-to-server.</summary>
        [AllowAnonymous]
        [HttpPost("/API/Arcade/Internal/LeaderboardSubmitted")]
        public async Task<IActionResult> LeaderboardSubmitted([FromBody] LeaderboardSubmittedRequest req)
        {
            if (!IsInternalCallerAuthorized()) return Unauthorized();
            if (req == null || req.RaLeaderboardId <= 0) return BadRequest();

            var userId = await ResolveRaUserIdAsync(req.UserId, req.RaUser);
            if (userId == null) return NoContent();

            var nowUtc = DateTime.UtcNow;
            var format = string.IsNullOrWhiteSpace(req.Format) ? "SCORE" : req.Format!.Trim().ToUpperInvariant();
            var row = await movieDb.ArcadeLeaderboardEntries.FirstOrDefaultAsync(e =>
                e.UserId == userId.Value && e.RaLeaderboardId == req.RaLeaderboardId);
            if (row == null)
            {
                movieDb.ArcadeLeaderboardEntries.Add(new ArcadeLeaderboardEntry
                {
                    UserId = userId.Value,
                    RaUser = req.RaUser ?? "",
                    ArcadeGameId = req.ArcadeGameId,
                    RaGameHash = req.RaGameHash,
                    RaLeaderboardId = req.RaLeaderboardId,
                    Title = req.Title,
                    Value = req.Value,
                    Format = format,
                    Competitive = req.Hardcore,
                    Cheat = req.Cheat,
                    Savescum = req.Savescum,
                    Timeplay = req.Timeplay,
                    AchievedUtc = req.AchievedUtc ?? nowUtc,
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                var better = LowerIsBetter(format) ? req.Value < row.Value : req.Value > row.Value;
                // Always refresh identity/display fields; only advance the recorded best on an improvement.
                if (req.ArcadeGameId != null) row.ArcadeGameId = req.ArcadeGameId;
                if (!string.IsNullOrEmpty(req.RaGameHash)) row.RaGameHash = req.RaGameHash;
                if (!string.IsNullOrEmpty(req.Title)) row.Title = req.Title;
                row.Format = format;
                row.UpdatedUtc = nowUtc;
                if (better)
                {
                    row.Value = req.Value;
                    row.Competitive = req.Hardcore;
                    // Taints belong to the recorded best — advance them with it (a cleaner but slower run
                    // doesn't scrub the taint off the faster save-scummed one that still holds the top slot).
                    row.Cheat = req.Cheat;
                    row.Savescum = req.Savescum;
                    row.Timeplay = req.Timeplay;
                    row.AchievedUtc = req.AchievedUtc ?? nowUtc;
                }
            }
            await movieDb.SaveChangesAsync();
            return NoContent();
        }

        public sealed class LinkStatRequest
        {
            public string? RoomId { get; set; }
            /// <summary>Site login the peer authenticated as. UNTRUSTED — it arrives from the browser, through
            /// the worker, and is only ever accepted if it resolves to a real user (see below).</summary>
            public string? Username { get; set; }
            /// <summary>Opaque per-device key minted by the frontend into localStorage.</summary>
            public string? DeviceId { get; set; }
            public string? System { get; set; }
            public string? Codec { get; set; }
            public int CeilingKbps { get; set; }
            public int OpenKbps { get; set; }
            public int SustainedKbps { get; set; }
            /// <summary>Null = the room never reached 90% of its ceiling.</summary>
            public int? RampTicks { get; set; }
            public int AtCeilPct { get; set; }
            public int CutsSteady { get; set; }
            public int StarvesSteady { get; set; }
            public int CongEpisodes { get; set; }
            public double RttMeanMs { get; set; }
            public double RttSdMs { get; set; }
            public string? Path { get; set; }
        }

        /// <summary>Record one peer's session link measurement from the CloudRetro worker at room close
        /// (Phase 0 of the ABR quality plan). Secret-gated server-to-server, like
        /// <see cref="AchievementUnlocked"/>. Append-only and purely observational — nothing reads these
        /// rows yet; they are the baseline that later judges whether a warm start actually helped.</summary>
        [AllowAnonymous]
        [HttpPost("/API/Arcade/Internal/LinkStat")]
        public async Task<IActionResult> LinkStat([FromBody] LinkStatRequest req)
        {
            if (!IsInternalCallerAuthorized()) return Unauthorized();
            if (req == null) return BadRequest();

            // The device id is the row's other half of the key: without it the measurement cannot be told
            // apart from the same user's other hardware, which is the one mistake this table exists to avoid.
            var deviceId = SanitizeDeviceId(req.DeviceId);
            if (string.IsNullOrEmpty(deviceId)) return NoContent();

            // The username is untrusted input that has passed through the browser. Resolve it or DROP the
            // row — never insert an unattributable measurement, and never create a user from this path.
            // NoContent rather than an error: a stale client is not a worker fault worth logging as one.
            var username = req.Username?.Trim();
            if (string.IsNullOrEmpty(username)) return NoContent();
            var userId = await movieDb.Users
                .Where(u => u.Username == username)
                .Select(u => (int?)u.UserID)
                .FirstOrDefaultAsync();
            if (userId == null) return NoContent();

            // Same-host sessions must never be stored: they measure our own encoder and CPU rather than a
            // link, so a warm value derived from one would describe nothing. The worker already filters them
            // out; this is the second half of that rule, enforced where the data actually lands.
            var path = Clamp20(req.Path);
            if (string.Equals(path, "samehost", StringComparison.OrdinalIgnoreCase)) return NoContent();

            // Clamp every numeric server-side. These arrive from a process we trust, but a wedged or
            // half-upgraded worker sending a garbage rate must not become a warm-start value later.
            movieDb.ArcadeLinkStats.Add(new ArcadeLinkStat
            {
                UserId = userId.Value,
                DeviceId = deviceId,
                System = Clamp(req.System, 40),
                Codec = Clamp20(req.Codec),
                CeilingKbps = Math.Clamp(req.CeilingKbps, 0, 100000),
                OpenKbps = Math.Clamp(req.OpenKbps, 0, 100000),
                SustainedKbps = Math.Clamp(req.SustainedKbps, 0, 100000),
                RampTicks = req.RampTicks is int rt ? Math.Clamp(rt, 0, 100000) : null,
                AtCeilPct = Math.Clamp(req.AtCeilPct, 0, 100),
                CutsSteady = Math.Clamp(req.CutsSteady, 0, 100000),
                StarvesSteady = Math.Clamp(req.StarvesSteady, 0, 100000),
                CongEpisodes = Math.Clamp(req.CongEpisodes, 0, 100000),
                // NaN/Infinity would round-trip through JSON and poison any later average, so they are
                // normalised to 0 rather than clamped (Math.Clamp propagates NaN).
                RttMeanMs = SaneMs(req.RttMeanMs),
                RttSdMs = SaneMs(req.RttSdMs),
                Path = path,
                CreatedUtc = DateTime.UtcNow,
            });
            await movieDb.SaveChangesAsync();
            return NoContent();

            static string? Clamp(string? s, int max) =>
                string.IsNullOrWhiteSpace(s) ? null : (s!.Length > max ? s[..max] : s);
            static string? Clamp20(string? s) => Clamp(s, 20);
            static double SaneMs(double v) =>
                double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Clamp(v, 0, 600000);
        }

        /// <summary>Reduce a client-supplied device id to the opaque key we actually store: [A-Za-z0-9-],
        /// capped at 64. Mirrors the worker's own sanitiser — both ends enforce it so neither has to trust
        /// the other.</summary>
        private static string SanitizeDeviceId(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new System.Text.StringBuilder(Math.Min(raw.Length, 64));
            foreach (var c in raw)
            {
                if (sb.Length >= 64) break;
                if (c == '-' || (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Ziggy's arcade watchdog (check H) pushes the patched-artifact verifier's report here
        /// every 30 minutes so a REVERTED patched binary raises a popup for admins instead of a line in a
        /// log file. See <see cref="MovieTheater.Arcade.PatchedArtifactAlerts"/> for why this signal exists
        /// and why it is in-memory. Read back by <c>GET /API/Admin/PatchedArtifacts</c>.
        /// Accepts and stores even an OK report — the heartbeat is half the signal, because a watchdog
        /// that has gone silent must not look like a healthy one.</summary>
        // [AllowAnonymous] like every other Internal callback: the caller is Ziggy's watchdog, not a
        // logged-in browser, so the class-wide StreamingUser policy would reject it before the secret
        // check below ever ran (verified live — the correct secret 401'd without this).
        [AllowAnonymous]
        [HttpPost("/API/Arcade/Internal/PatchedArtifactAlert")]
        public IActionResult PatchedArtifactAlert([FromBody] PatchedArtifactAlertRequest req)
        {
            if (!IsInternalCallerAuthorized()) return Unauthorized();
            if (req == null) return BadRequest();

            MovieTheater.Arcade.PatchedArtifactAlerts.Record(req.Ok, req.Findings?.Count ?? 0, req.RawJson);
            return NoContent();
        }

        public sealed class PatchedArtifactAlertRequest
        {
            /// <summary>True when every patched/pinned binary matched the manifest.</summary>
            public bool Ok { get; set; }
            /// <summary>One entry per problem: MISSING / DRIFT / DISAGREE, with the artifact id and path.</summary>
            public List<PatchedArtifactFinding>? Findings { get; set; }
            /// <summary>The verifier's own JSON, passed through verbatim for display.</summary>
            public string? RawJson { get; set; }
        }

        public sealed class PatchedArtifactFinding
        {
            public string? Id { get; set; }
            public string? Status { get; set; }
            public string? Path { get; set; }
            public string? Detail { get; set; }
            /// <summary>True when upstream ships a file of this exact name, so a MISSING file gets
            /// SILENTLY replaced with stock by the next worker start — the worst case.</summary>
            public bool StockName { get; set; }
        }

        /// <summary>Ziggy's arcade watchdog (check I) pushes the host's desktop-session state here — is the
        /// session the emulators render in attached to the physical console, or has someone left a remote
        /// desktop open (or closed one without the console coming back)? Both cost roughly half the frame
        /// rate with no error anywhere, so the lobby warns players instead of letting them blame their own
        /// connection. See <see cref="MovieTheater.Arcade.ArcadeHostSession"/>. Read back by
        /// <c>GET /API/Arcade/HostStatus</c>.
        /// Posted on every state change plus a ~5-minute heartbeat: the heartbeat is what lets the site
        /// distinguish "the console is fine" from "nobody has told us anything in an hour".</summary>
        // [AllowAnonymous] for the same reason as PatchedArtifactAlert: the caller is Ziggy's watchdog,
        // not a logged-in browser, so the class-wide StreamingUser policy would reject it before the
        // secret check ran.
        [AllowAnonymous]
        [HttpPost("/API/Arcade/Internal/HostSessionAlert")]
        public IActionResult HostSessionAlert([FromBody] HostSessionAlertRequest req)
        {
            if (!IsInternalCallerAuthorized()) return Unauthorized();
            if (req == null) return BadRequest();

            MovieTheater.Arcade.ArcadeHostSession.Record(req.Degraded, req.Kind, req.Detail, req.SessionId, req.Recovering);
            return NoContent();
        }

        public sealed class HostSessionAlertRequest
        {
            /// <summary>True when the arcade's session is NOT on the physical console (remote attached, or
            /// detached and not yet reattached) — i.e. capture/render is running at the reduced rate.</summary>
            public bool Degraded { get; set; }
            /// <summary>console | remote | disconnected | unknown — what the watchdog actually observed.</summary>
            public string? Kind { get; set; }
            /// <summary>The watchdog's own one-line reading (session id, state, WinStation name).</summary>
            public string? Detail { get; set; }
            public int? SessionId { get; set; }
            /// <summary>The watchdog has triggered the "MovieTheater - Reattach Console" recovery and is
            /// waiting to see it land. Lets the banner say "restoring…" instead of just "degraded".</summary>
            public bool Recovering { get; set; }
        }

        /// <summary>The arcade host's session health for the lobby/room banner: is a remote desktop session
        /// holding the box off its physical console right now, is the recovery running, or did it just come
        /// back? Cheap (in-memory, no DB) so the lobby can poll it alongside everything else.</summary>
        [HttpGet("/API/Arcade/HostStatus")]
        public IActionResult HostStatus()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var s = MovieTheater.Arcade.ArcadeHostSession.Current;
            return Json(new
            {
                degraded = s.Degraded,
                kind = s.Kind,
                // The raw reading is for admins/debugging; the banner writes its own copy from `kind`.
                detail = s.Detail,
                sessionId = s.SessionId,
                recovering = s.Recovering,
                recentlyRecovered = s.RecentlyRecovered,
                degradedSinceUtc = s.DegradedSinceUtc,
                recoveredUtc = s.RecoveredUtc,
                // `stale` never rides along with degraded=true (the holder suppresses it) — it is here so a
                // future admin view can say "we have lost contact with the watchdog" honestly.
                reported = s.Reported,
                stale = s.Stale,
            });
        }

        // Shared arcade secret gate for the server-to-server callbacks (same header SaveHarvested checks).
        private bool IsInternalCallerAuthorized()
        {
            var secret = config.ArcadeTokenSecret;
            return !string.IsNullOrEmpty(secret) &&
                string.Equals(Request.Headers["X-Arcade-Internal-Secret"].ToString(), secret, StringComparison.Ordinal);
        }

        /// <summary>The friends-only leaderboards for a game card: our mirrored best-per-user rows across every
        /// ROM version of the card, grouped by RA leaderboard, ranked by format, with each entrant's site
        /// username. Each board links out to the global RA board. Empty when nothing's been posted yet.</summary>
        [HttpGet("/API/Arcade/Game/{gameId:int}/Leaderboards")]
        public async Task<IActionResult> GameLeaderboards(int gameId)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var game = await movieDb.ArcadeGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId);
            if (game == null) return NotFound(new { message = "Game not found." });

            // Span the whole collapsed card (all region/rev versions), so a board isn't split across dumps.
            var versions = await movieDb.ArcadeGames.AsNoTracking()
                .Where(g => g.System == game.System && g.CollapseKey == game.CollapseKey)
                .Select(g => new { g.Id, g.RaGameId }).ToListAsync();
            var versionIds = versions.Select(v => v.Id).ToList();
            var raGameId = versions.Select(v => v.RaGameId).FirstOrDefault(r => r != null && r > 0);

            var entries = await (from e in movieDb.ArcadeLeaderboardEntries.AsNoTracking()
                                 join u in movieDb.Users on e.UserId equals u.UserID
                                 where e.ArcadeGameId != null && versionIds.Contains(e.ArcadeGameId.Value)
                                 select new { e.RaLeaderboardId, e.Title, e.Format, e.Value, e.Competitive, e.Clean, e.Cheat, e.Savescum, e.Timeplay, e.AchievedUtc, e.UserId, u.Username })
                                .ToListAsync();

            // Our friends' best entries, grouped + ranked per RA leaderboard (the mirror).
            var friendBoards = entries
                .GroupBy(x => x.RaLeaderboardId)
                .ToDictionary(grp => grp.Key, grp =>
                {
                    var format = grp.Select(x => x.Format).FirstOrDefault() ?? "SCORE";
                    var ranked = (LowerIsBetter(format) ? grp.OrderBy(x => x.Value) : grp.OrderByDescending(x => x.Value))
                        .Select((x, i) => new
                        {
                            rank = i + 1,
                            userId = x.UserId,
                            username = x.Username,
                            value = x.Value,
                            competitive = x.Competitive,
                            // Run-legitimacy taints for the why-icon. `legit` = OBSERVED clean (badge): no
                            // cheat, no save-scum, no time manipulation — independent of the room's mode.
                            cheat = x.Cheat,
                            savescum = x.Savescum,
                            timeplay = x.Timeplay,
                            legit = x.Clean,
                            achievedUtc = x.AchievedUtc,
                            you = x.UserId == userId.Value,
                        }).ToList();
                    return new { format, title = grp.Select(x => x.Title).FirstOrDefault(t => !string.IsNullOrEmpty(t)), ranked };
                });

            // Every board RA defines for this game — so a user sees ALL boards (per-level times, score, any%,
            // etc.), even ones no friend has posted to yet. Kept in RA's order. Best-effort: if RA is
            // unreachable/unconfigured we just show the boards friends have entries on.
            var raOrder = new List<long>();
            var raMeta = new Dictionary<long, (string title, string desc, string format, string? topUser, string? topScore)>();
            if (raGameId != null)
            {
                using var doc = await RaWebApiGetAsync($"API_GetGameLeaderboards.php?i={raGameId}&c=500", RaMemTtl, RaDbBoards);
                if (doc != null && doc.RootElement.TryGetProperty("Results", out var results)
                    && results.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var r in results.EnumerateArray())
                    {
                        if (!(r.TryGetProperty("ID", out var idv) && idv.TryGetInt64(out var id)) || id <= 0) continue;
                        string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : "";
                        // RA's global #1 for this board — a reference line so friends can see the world record.
                        string? topUser = null, topScore = null;
                        if (r.TryGetProperty("TopEntry", out var te) && te.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            topUser = te.TryGetProperty("User", out var tu) ? tu.GetString() : null;
                            topScore = te.TryGetProperty("FormattedScore", out var fs) ? fs.GetString()
                                : (te.TryGetProperty("Score", out var sc) ? sc.ToString() : null);
                        }
                        if (!raMeta.ContainsKey(id)) { raOrder.Add(id); raMeta[id] = (S("Title"), S("Description"), S("Format"), topUser, topScore); }
                    }
                }
            }

            // FRIENDS FIRST — this is a site between friends, so boards our friends have posted to lead;
            // RA's own boards (no friend entry yet) come last, purely as a reference for what RA scores look
            // like. OrderBy is stable, so RA's board order is preserved within each group.
            var boardIds = raOrder.Concat(friendBoards.Keys.Where(k => !raMeta.ContainsKey(k))).ToList();
            var boards = boardIds.Select(id =>
            {
                var hasFriends = friendBoards.TryGetValue(id, out var fb);
                var hasMeta = raMeta.TryGetValue(id, out var meta);
                var title = (hasFriends ? fb!.title : null) ?? (hasMeta ? meta.title : null);
                var format = (hasFriends ? fb!.format : null) ?? (hasMeta && !string.IsNullOrEmpty(meta.format) ? meta.format : "SCORE");
                var hasEntries = hasFriends && fb!.ranked.Count > 0;
                return new
                {
                    leaderboardId = id,
                    title = string.IsNullOrEmpty(title) ? $"Leaderboard {id}" : title,
                    description = hasMeta ? meta.desc : null,
                    format,
                    hasEntries,
                    raTopUser = hasMeta ? meta.topUser : null,
                    raTopScore = hasMeta ? meta.topScore : null,
                    raUrl = $"https://retroachievements.org/leaderboardinfo.php?i={id}",
                    entries = hasFriends ? (object)fb!.ranked : Array.Empty<object>(),
                };
            })
            .OrderByDescending(b => b.hasEntries)
            .ToList();

            return Json(new { gameId, system = game.System, raGameId, boards });
        }

        /// <summary>A user's mirrored RA achievement unlocks, newest first, paged (a heavy player accrues
        /// thousands — always a bounded slice, like MySaves). Any signed-in user can view any user's list;
        /// this is a communal site. Feeds the profile "achievements" view.</summary>
        [HttpGet("/API/Arcade/Users/{targetUserId:int}/Achievements")]
        public async Task<IActionResult> UserAchievements(int targetUserId, int skip = 0, int take = 50)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            skip = Math.Max(0, skip);
            take = Math.Clamp(take, 1, 200);

            var q = from a in movieDb.ArcadeAchievementUnlocks.AsNoTracking()
                    where a.UserId == targetUserId
                    select a;

            var totalCount = await q.CountAsync();
            var totalPoints = totalCount == 0 ? 0 : await q.SumAsync(a => a.Points);
            var rows = await q
                .OrderByDescending(a => a.UnlockedUtc)
                .Skip(skip).Take(take)
                .Select(a => new
                {
                    a.Id,
                    gameId = a.ArcadeGameId,
                    a.RaAchievementId,
                    a.Title,
                    a.Points,
                    a.Competitive,
                    a.Cheat,
                    a.Savescum,
                    a.Timeplay,
                    legit = a.Clean,
                    a.UnlockedUtc,
                    raUrl = "https://retroachievements.org/achievement/" + a.RaAchievementId,
                })
                .ToListAsync();

            return Json(new { userId = targetUserId, totalCount, totalPoints, skip, take, rows });
        }

        /// <summary>Every achievement that EXISTS for a game (from RetroAchievements), with the signed-in
        /// user's earned state overlaid from our own arcade mirror — so a card's modal can show the full set,
        /// earned ones lit with their badge, the rest greyed (locked badge). Spans the collapsed card's
        /// versions to find the RA game id and to match earned unlocks. Degrades to {configured:false} (no RA
        /// Web API key) or {available:false} (RA unreachable / game has no RA set) without erroring.</summary>
        [HttpGet("/API/Arcade/Game/{gameId:int}/Achievements")]
        public async Task<IActionResult> GameAchievements(int gameId)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var game = await movieDb.ArcadeGames.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId);
            if (game == null) return NotFound(new { message = "Game not found." });

            var versions = await movieDb.ArcadeGames.AsNoTracking()
                .Where(g => g.System == game.System && g.CollapseKey == game.CollapseKey)
                .Select(g => new { g.Id, g.RaGameId }).ToListAsync();
            var versionIds = versions.Select(v => v.Id).ToList();
            var raGameId = versions.Select(v => v.RaGameId).FirstOrDefault(r => r != null && r > 0);

            // What THIS user earned in our arcade (mirror), keyed by RA achievement id — the earned overlay.
            // Read BEFORE the RA lookups, because it is also the FALLBACK when they come up empty (below).
            var earned = await movieDb.ArcadeAchievementUnlocks.AsNoTracking()
                .Where(a => a.UserId == userId.Value && a.ArcadeGameId != null && versionIds.Contains(a.ArcadeGameId.Value))
                .ToListAsync();
            var earnedById = earned.GroupBy(a => a.RaAchievementId)
                .ToDictionary(g => g.Key, g => g.OrderBy(a => a.UnlockedUtc).First());

            // No RA set for this card — either arcade-ra-enrich couldn't match its title (ROMHACKS are the
            // standing case: rcheevos identifies the ROM by HASH and happily awards RA's hack set, while the
            // enrich pass matches by title and RA names hacks "~Hack~ Super Mario 64: Last Impact"), or RA is
            // unreachable/unconfigured. Falling straight through to `available: false` threw away unlocks we
            // hold in our own mirror: the trophy room listed the game (it groups off those very rows) and then
            // showed an empty panel — the "I earned trophies and there's nothing here" report. Show what we
            // know. `partial` tells the client this is the earned subset, not the whole set.
            // SELF-HEAL. A card with unlocks but no RaGameId means our title never matched RA's — romhacks are
            // the standing case (RA names them "~Hack~ Super Mario 64: Last Impact"), and a translation patch
            // can resolve to a different region's entry entirely ("Dynamite Headdy English Translation" → RA's
            // "Dynamite Headdy (Japan)"), which no title match could ever find. We can recover it EXACTLY from
            // what we already hold: rcheevos had to hash-identify the ROM before RA would award any of these
            // achievements, so RA's own achievement→game map is authoritative. Resolve once, pin it, and the
            // panel is whole from here on — this is what stops the empty-panel bug coming back per new hack
            // rather than waiting for someone to re-run arcade-ra-enrich.
            var selfHealed = false;
            if (raGameId == null && earnedById.Count > 0)
            {
                raGameId = await ResolveAndPinRaGameIdAsync(versionIds, earnedById.Keys.Min());
                selfHealed = raGameId != null;
            }

            if (raGameId == null || !RaWebApiConfigured)
                return Json(MirrorOnlyAchievements(gameId, raGameId, earnedById));

            using var doc = await RaWebApiGetAsync($"API_GetGameExtended.php?i={raGameId}", RaMemTtl, RaDbDefs);
            if (doc == null)
                return Json(MirrorOnlyAchievements(gameId, raGameId, earnedById));

            var root = doc.RootElement;
            var items = new List<(int order, object obj)>();
            int earnedInSet = 0, pointsEarned = 0, pointsTotal = 0;
            if (root.TryGetProperty("Achievements", out var achs) && achs.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var ap in achs.EnumerateObject())
                {
                    var a = ap.Value;
                    long id = a.TryGetProperty("ID", out var idv) && idv.TryGetInt64(out var idl) ? idl
                        : (long.TryParse(ap.Name, out var pn) ? pn : 0);
                    var points = a.TryGetProperty("Points", out var pv) && pv.TryGetInt32(out var pvv) ? pvv : 0;
                    var order = a.TryGetProperty("DisplayOrder", out var ov) && ov.TryGetInt32(out var ovv) ? ovv : 0;
                    var mine = earnedById.TryGetValue(id, out var m) ? m : null;
                    pointsTotal += points;
                    if (mine != null) { earnedInSet++; pointsEarned += points; }
                    items.Add((order, new
                    {
                        id,
                        title = a.TryGetProperty("Title", out var t) ? t.GetString() : null,
                        description = a.TryGetProperty("Description", out var d) ? d.GetString() : null,
                        points,
                        // Always the COLOURED badge; the grid greys locked ones via CSS, and the in-room
                        // unlock toast reuses this map to show the real art when an achievement fires.
                        badgeUrl = RaBadgeUrl(a.TryGetProperty("BadgeName", out var b) ? b.GetString() : null),
                        earned = mine != null,
                        earnedCompetitive = mine?.Competitive ?? false,
                        earnedUtc = mine?.UnlockedUtc,
                        cheat = mine?.Cheat ?? false,
                        savescum = mine?.Savescum ?? false,
                        timeplay = mine?.Timeplay ?? false,
                        legit = mine != null && mine.Clean,
                        raUrl = "https://retroachievements.org/achievement/" + id,
                    }));
                }
            }

            // A self-healed card has RaGameId but still the 0 achievement count enrich left behind, and that
            // count is what lights the 🏆 badge and answers the lobby's RA filter. We just counted the real
            // set, so finish the job — cheap, and only on the one request that healed the card.
            if (selfHealed && items.Count > 0)
            {
                try
                {
                    var versionRows = await movieDb.ArcadeGames.Where(v => versionIds.Contains(v.Id)).ToListAsync();
                    foreach (var v in versionRows) v.RaAchievementCount = items.Count;
                    await movieDb.SaveChangesAsync();
                }
                catch (Exception ex) { logger.LogWarning(ex, "Arcade RA self-heal: could not persist RaAchievementCount."); }
            }

            return Json(new
            {
                gameId,
                raGameId,
                configured = true,
                available = true,
                title = root.TryGetProperty("Title", out var gt) ? gt.GetString() : game.Title,
                imageIcon = RaImageUrl(root.TryGetProperty("ImageIcon", out var ii) ? ii.GetString() : null),
                raUrl = "https://retroachievements.org/game/" + raGameId,
                numAchievements = items.Count,
                earnedCount = earnedInSet,
                pointsEarned,
                pointsTotal,
                achievements = items.OrderBy(x => x.order).Select(x => x.obj).ToList(),
            });
        }

        /// <summary>Ask RA which game an achievement belongs to, and pin the answer onto every version of the
        /// card that has none. Hash-grade provenance: the achievement is one rcheevos awarded, which it could
        /// only do after identifying the ROM by content hash — so this outranks any title match, and
        /// <c>arcade-ra-enrich</c> is careful not to erase it on a later title miss (--clear-unmatched opts in).
        /// Returns null when RA can't place it; a persistence failure is logged and swallowed, because this is
        /// a cache fill on a read path and must never fail the request that triggered it.</summary>
        private async Task<int?> ResolveAndPinRaGameIdAsync(List<int> versionIds, long achievementId)
        {
            if (!RaWebApiConfigured) return null;
            using var doc = await RaWebApiGetAsync($"API_GetAchievementUnlocks.php?a={achievementId}&c=1", RaMemTtl, RaDbDefs);
            if (doc == null) return null;
            if (!doc.RootElement.TryGetProperty("Game", out var g) || !g.TryGetProperty("ID", out var idv)) return null;
            // RA is inconsistent about numeric-vs-string ids across endpoints; accept either.
            var raId = idv.ValueKind == System.Text.Json.JsonValueKind.Number && idv.TryGetInt32(out var n) ? n
                : int.TryParse(idv.GetString(), out var p) ? p : 0;
            if (raId <= 0) return null;

            try
            {
                var unpinned = await movieDb.ArcadeGames.Where(v => versionIds.Contains(v.Id) && v.RaGameId == null).ToListAsync();
                foreach (var v in unpinned) v.RaGameId = raId;
                if (unpinned.Count > 0)
                {
                    await movieDb.SaveChangesAsync();
                    logger.LogInformation("Arcade RA self-heal: pinned RaGameId {RaGameId} onto {Count} version(s) via achievement {AchievementId}.",
                        raId, unpinned.Count, achievementId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Arcade RA self-heal: resolved RaGameId {RaGameId} but could not persist it.", raId);
            }
            return raId;
        }

        /// <summary>The achievements payload built from OUR mirror alone, for when RA can't supply the set
        /// (card not matched to an RA game, or the Web API unconfigured/unreachable). Carries only what an
        /// <see cref="ArcadeAchievementUnlock"/> row records — no description, no badge art, and no locked
        /// entries, since the set itself is what's missing — so it reports <c>partial: true</c> and leaves
        /// <c>pointsTotal</c> equal to what was earned rather than inventing a denominator. Empty mirror =
        /// the honest <c>available: false</c> the caller used to return unconditionally.</summary>
        private object MirrorOnlyAchievements(int gameId, int? raGameId, Dictionary<long, ArcadeAchievementUnlock> earnedById)
        {
            var mine = earnedById.Values.OrderBy(a => a.UnlockedUtc).ToList();
            if (mine.Count == 0)
                return new { gameId, raGameId, configured = RaWebApiConfigured, available = false, partial = false, achievements = Array.Empty<object>() };

            return new
            {
                gameId,
                raGameId,
                configured = RaWebApiConfigured,
                available = true,
                partial = true,
                raUrl = raGameId == null ? null : "https://retroachievements.org/game/" + raGameId,
                numAchievements = mine.Count,
                earnedCount = mine.Count,
                pointsEarned = mine.Sum(a => a.Points),
                pointsTotal = mine.Sum(a => a.Points),
                achievements = mine.Select(a => new
                {
                    id = a.RaAchievementId,
                    title = a.Title,
                    description = (string?)null,
                    points = a.Points,
                    badgeUrl = (string?)null,
                    earned = true,
                    earnedCompetitive = a.Competitive,
                    earnedUtc = a.UnlockedUtc,
                    cheat = a.Cheat,
                    savescum = a.Savescum,
                    timeplay = a.Timeplay,
                    legit = a.Clean,
                    raUrl = "https://retroachievements.org/achievement/" + a.RaAchievementId,
                }).ToList(),
            };
        }

        /// <summary>The trophy-room summary for a user: the games they've earned achievements in (from our
        /// arcade mirror), collapsed across region/rev versions, with per-game counts + points + how many were
        /// legit hardcore, newest activity first. Drives the trophy-room grid; each tile drills into the game's
        /// full achievement set (<see cref="GameAchievements"/>). Any signed-in user can view any user's —
        /// communal site.</summary>
        [HttpGet("/API/Arcade/Users/{targetUserId:int}/Trophies")]
        public async Task<IActionResult> UserTrophies(int targetUserId)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var rows = await (from a in movieDb.ArcadeAchievementUnlocks.AsNoTracking()
                              where a.UserId == targetUserId && a.ArcadeGameId != null
                              join g in movieDb.ArcadeGames on a.ArcadeGameId equals g.Id
                              select new
                              {
                                  a.ArcadeGameId,
                                  g.System,
                                  g.CollapseKey,
                                  g.Title,
                                  a.Points,
                                  a.Competitive,
                                  a.Clean,
                                  a.Cheat,
                                  a.Savescum,
                                  a.Timeplay,
                                  a.UnlockedUtc,
                              }).ToListAsync();

            var games = rows
                .GroupBy(x => new { x.System, x.CollapseKey })
                .Select(grp => new
                {
                    gameId = grp.OrderByDescending(x => x.UnlockedUtc).Select(x => x.ArcadeGameId).First(),
                    title = grp.Select(x => x.Title).First(),
                    system = grp.Key.System,
                    earnedCount = grp.Count(),
                    points = grp.Sum(x => x.Points),
                    competitiveCount = grp.Count(x => x.Competitive),
                    legitCount = grp.Count(x => x.Clean),
                    lastUnlockedUtc = grp.Max(x => x.UnlockedUtc),
                })
                .OrderByDescending(g => g.lastUnlockedUtc)
                .ToList();

            return Json(new
            {
                userId = targetUserId,
                totalPoints = rows.Sum(x => x.Points),
                totalEarned = rows.Count,
                gameCount = games.Count,
                games,
            });
        }

        /// <summary>The signed-in user's own trophy room (self-scoped via the auth cookie, so the client
        /// needs no user id — the app runs on username). Delegates to <see cref="UserTrophies"/>.</summary>
        [HttpGet("/API/Arcade/Trophies/Mine")]
        public Task<IActionResult> MyTrophies()
        {
            var userId = GetCurrentUserId();
            return userId == null ? Task.FromResult<IActionResult>(Unauthorized()) : UserTrophies(userId.Value);
        }

        /// <summary>The signed-in user's saves for a game — the source for the resume picker / "My Saves".</summary>
        [HttpGet("/API/Arcade/Games/{gameId:int}/Saves")]
        public async Task<IActionResult> ListSaves(int gameId)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var rows = await movieDb.ArcadeSaves
                .Where(s => s.UserId == userId.Value && s.ArcadeGameId == gameId)
                .OrderBy(s => s.SlotId)
                .Select(s => new { s.Id, s.Kind, s.SlotId, s.Label, s.SizeBytes, s.IsAutosave, s.CoreName, s.UpdatedUtc })
                .ToListAsync();
            return Json(rows);
        }

        /// <summary>Games the signed-in user has actually played recently, derived from their own save
        /// activity (most-recent ArcadeSave.UpdatedUtc per game). A save is written whenever a session
        /// ends, so this is real evidence of play whether the user created the room or just joined one —
        /// ArcadeSession only records the creator, so it can't answer this. Feeds the lobby's "Recently
        /// played" strip.
        ///
        /// Each row is { game, lastPlayedUtc, saveCount, playedVersionId }, where `game` is the SAME full
        /// card the grid renders — the strip's tiles open the same game modal, so they must carry the same
        /// payload. `playedVersionId` is the ROM row the save belongs to: saves are keyed per row, so the
        /// modal opens on that version or its Continue prompt would look at the wrong one.</summary>
        [HttpGet("/API/Arcade/RecentlyPlayed")]
        public async Task<IActionResult> RecentlyPlayed(int take = 12)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            take = Math.Clamp(take, 1, 30);

            // Over-fetch save rows: several ROWS of one game (a region swap, a re-dump) collapse onto ONE
            // card, so N rows can yield fewer than N cards. 3× is plenty and still a bounded query.
            var recent = await movieDb.ArcadeSaves
                .Where(s => s.UserId == userId.Value)
                .GroupBy(s => s.ArcadeGameId)
                .Select(g => new { ArcadeGameId = g.Key, LastPlayedUtc = g.Max(s => s.UpdatedUtc), SaveCount = g.Count() })
                .OrderByDescending(x => x.LastPlayedUtc)
                .Take(take * 3)
                .ToListAsync();
            if (recent.Count == 0) return Json(Array.Empty<object>());

            // A game can vanish from the lobby (disabled) without its save rows going away — silently drop
            // those rather than 500 or show a ghost card.
            var baseQ = await VisibleGamesAsync(userId.Value);
            var gameIds = recent.Select(r => r.ArcadeGameId).ToList();
            var played = await baseQ.Where(g => gameIds.Contains(g.Id))
                .Select(g => new { g.Id, g.System, g.CollapseKey })
                .ToDictionaryAsync(g => g.Id);

            // Collapse the played ROWS onto their CARDS, newest first. `recent` is already newest-first, so
            // the first row of a card fixes both its position and the version the modal should open on;
            // later rows of the same card only add to its save count.
            var order = new List<(string System, string CollapseKey)>();
            var byCard = new Dictionary<(string System, string CollapseKey), (DateTime LastPlayedUtc, int SaveCount, int PlayedVersionId)>();
            foreach (var r in recent)
            {
                if (!played.TryGetValue(r.ArcadeGameId, out var g)) continue;
                var key = (g.System, g.CollapseKey);
                if (byCard.TryGetValue(key, out var cur))
                    byCard[key] = (cur.LastPlayedUtc, cur.SaveCount + r.SaveCount, cur.PlayedVersionId);
                else if (order.Count < take)
                {
                    order.Add(key);
                    byCard[key] = (r.LastPlayedUtc, r.SaveCount, r.ArcadeGameId);
                }
            }
            if (order.Count == 0) return Json(Array.Empty<object>());

            var cards = await BuildGameCardsAsync(baseQ, order.Select(k => (k.System, k.CollapseKey, (string)null)).ToList(), null);
            // Pair each card with its metadata by POSITION — safe because BuildGameCardsAsync projects
            // `keys.Select(...)`, so it returns exactly one non-null card per key, in order. That is the
            // contract this relies on, so state it and enforce it rather than trusting it silently: a row
            // shipped without a `game` is not a missing tile on the client, it's a blank arcade page.
            var result = new List<object>(cards.Count);
            for (var i = 0; i < cards.Count && i < order.Count; i++)
            {
                if (cards[i] is null) continue;
                var m = byCard[order[i]];
                result.Add(new { game = cards[i], lastPlayedUtc = m.LastPlayedUtc, saveCount = m.SaveCount, playedVersionId = m.PlayedVersionId });
            }
            return Json(result);
        }

        /// <summary>Every save the signed-in user has, across every game — the "saves vault" pop-out. A
        /// per-game view already exists at Games/{id}/Saves; this is the cross-game management tool,
        /// so it's always a bounded, paged, searchable/filterable slice (never "load everything") —
        /// a player who's touched a few hundred titles can accumulate thousands of save rows.</summary>
        [HttpGet("/API/Arcade/Saves/Mine")]
        public async Task<IActionResult> MySaves(string search = null, string system = null, int skip = 0, int take = 50)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            skip = Math.Max(0, skip);
            take = Math.Clamp(take, 1, 200);

            var q = from s in movieDb.ArcadeSaves
                    join g in movieDb.ArcadeGames on s.ArcadeGameId equals g.Id
                    where s.UserId == userId.Value
                    select new { s, g };

            if (!string.IsNullOrWhiteSpace(system))
                q = q.Where(x => x.g.System == system);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                q = q.Where(x => x.g.Title.Contains(term));
            }

            var totalCount = await q.CountAsync();
            var totalSizeBytes = totalCount == 0 ? 0L : await q.SumAsync(x => (long)x.s.SizeBytes);

            var rows = await q
                .OrderByDescending(x => x.s.UpdatedUtc)
                .Skip(skip).Take(take)
                .Select(x => new
                {
                    id = x.s.Id,
                    gameId = x.g.Id,
                    title = x.g.Title,
                    system = x.g.System,
                    artId = x.g.Id,
                    hasBoxArt = x.g.BoxArtPath != null,
                    kind = x.s.Kind,
                    slotId = x.s.SlotId,
                    label = x.s.Label,
                    sizeBytes = x.s.SizeBytes,
                    isAutosave = x.s.IsAutosave,
                    coreName = x.s.CoreName,
                    updatedUtc = x.s.UpdatedUtc,
                })
                .ToListAsync();

            return Json(new { rows, totalCount, totalSizeBytes, skip, take });
        }

        private static readonly HttpClient gatewayClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        // Call a secret-gated gateway blob op (the blobs live on Ziggy; the k8s pod can't read them).
        private async Task<HttpResponseMessage?> CallGatewayAsync(string path, object body)
        {
            var baseUrl = config.ArcadeGatewayBaseUrl;
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(config.ArcadeTokenSecret)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/" + path)
            { Content = JsonContent.Create(body) };
            req.Headers.Add("X-Arcade-Internal-Secret", config.ArcadeTokenSecret);
            try { return await gatewayClient.SendAsync(req); } catch { return null; }
        }

        // Same channel, GET (the heavy status read has no body).
        private async Task<HttpResponseMessage?> GetGatewayAsync(string path)
        {
            var baseUrl = config.ArcadeGatewayBaseUrl;
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(config.ArcadeTokenSecret)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/" + path);
            req.Headers.Add("X-Arcade-Internal-Secret", config.ArcadeTokenSecret);
            try { return await gatewayClient.SendAsync(req); } catch { return null; }
        }

        // ── Heavy lane (docs/arcade-heavy-lane-plan.md §7): Moonlight-streamed titles ────────────
        // The gateway on Ziggy owns descriptors, the one-session lock, staging, and the Apollo API;
        // these endpoints are the browser's authenticated path to it. The site adds what the gateway
        // can't know: user auth, the age gate, and the Moonlight-client→user mapping (HeavyClient).

        /// <summary>Lane status for the lobby: who holds the heavy session (username, not device
        /// name), plus per-app staging state keyed by ArcadeGame id for the heavy cards.</summary>
        [HttpGet("/API/Arcade/Heavy/Status")]
        public async Task<IActionResult> HeavyStatus()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var resp = await GetGatewayAsync("heavy/status");
            if (resp == null || !resp.IsSuccessStatusCode)
                return StatusCode(501, new { message = "The heavy lane is not configured." });

            var raw = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            // Resolve the Apollo device name to the site user who paired it (plan §7.3).
            string byUser = null;
            if (raw.TryGetProperty("clientName", out var cn) && cn.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var name = cn.GetString();
                var client = await movieDb.HeavyClients.FirstOrDefaultAsync(c => c.ClientName == name);
                if (client != null)
                    byUser = await movieDb.Users.Where(u => u.UserID == client.UserId).Select(u => u.Username).FirstOrDefaultAsync();
            }
            return Json(new
            {
                locked = raw.TryGetProperty("locked", out var l) && l.GetBoolean(),
                title = raw.TryGetProperty("title", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null,
                byUser,
                sinceUtc = raw.TryGetProperty("sinceUtc", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString() : null,
                apps = raw.TryGetProperty("apps", out var a) ? a : default,
            });
        }

        /// <summary>Advance ONE staging chunk for a heavy title (the browser drives the loop — the
        /// bulk-job house rule: bounded per call, progress every chunk, resumable). Anyone age-visible
        /// may prepare; preparing is a disk copy, not a session, so it's allowed while someone plays.</summary>
        // ── ROM prewarm (arcade perf program P7, 2026-09-05) ────────────────────────────────────────
        // Per-user token bucket: a modal open is one call; nobody legitimately opens more than a few a
        // minute, and the gateway serializes extractions anyway (MaxParallelExtractions), so this only
        // stops a scripted client from queueing the whole catalogue.
        private static readonly ConcurrentDictionary<int, (DateTime WindowStart, int Count)> prewarmBuckets = new();
        private const int PrewarmPerMinute = 6;

        /// <summary>Ask the gateway to stage this game's ROM now (JIT-managed titles only; anything else
        /// answers "staged" and costs nothing). Fired by the game modal the moment a version is on screen,
        /// so the extraction that used to run under "Connecting…" is usually done before Start. Same gates
        /// as playing: signed-in + age-visible. Returns the gateway's stage state.</summary>
        [HttpPost("/API/Arcade/Game/{gameId:int}/Prewarm")]
        public async Task<IActionResult> PrewarmGame(int gameId)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == gameId && g.IsEnabled);
            if (game == null) return NotFound();
            if (string.IsNullOrEmpty(game.SourceArchivePath))
                return Json(new { state = "staged", percent = 100 }); // not JIT-managed: the ROM is already on disk
            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);
            if (game.RatingCeiling > ageRestriction)
                return StatusCode(403, new { message = "This game isn't available on your account." });

            var now = DateTime.UtcNow;
            var bucket = prewarmBuckets.AddOrUpdate(userId.Value,
                _ => (now, 1),
                (_, cur) => now - cur.WindowStart >= TimeSpan.FromMinutes(1) ? (now, 1) : (cur.WindowStart, cur.Count + 1));
            if (bucket.Count > PrewarmPerMinute)
                return StatusCode(429, new { message = "Too many prewarm requests; try again in a minute." });

            var resp = await CallGatewayAsync($"internal/rom-prewarm/{gameId}", new { });
            if (resp == null || !resp.IsSuccessStatusCode)
                return Json(new { state = "unavailable" }); // the gateway is not configured or unreachable: harmless, Start still stages inline
            var body = await resp.Content.ReadAsStringAsync();
            return Content(body, "application/json");
        }

        [HttpPost("/API/Arcade/Heavy/Stage/{gameId:int}")]
        public async Task<IActionResult> HeavyStage(int gameId)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == gameId && g.IsEnabled);
            if (game == null || !string.Equals(game.Lane, "heavy", StringComparison.OrdinalIgnoreCase))
                return NotFound(new { message = "Not a heavy-lane title." });
            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);
            if (game.RatingCeiling > ageRestriction)
                return StatusCode(403, new { message = "This game isn't available on your account." });

            var resp = await CallGatewayAsync($"heavy/stage/{gameId}", new { });
            if (resp == null) return StatusCode(501, new { message = "The heavy lane is not configured." });
            var body = await resp.Content.ReadAsStringAsync();
            return Content(body, "application/json");
        }

        /// <summary>An Artemis launch shortcut (.art) for a heavy title: tapping the downloaded file
        /// on a PAIRED Android device streams the game directly — the closest thing to the card
        /// launching the app (moonlight:// deep links still don't exist upstream; Artemis' .art
        /// trampoline does). Same gates as playing: signed-in + age-visible.</summary>
        [HttpGet("/API/Arcade/Heavy/Shortcut/{gameId:int}")]
        public async Task<IActionResult> HeavyShortcut(int gameId)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == gameId && g.IsEnabled);
            if (game == null || !string.Equals(game.Lane, "heavy", StringComparison.OrdinalIgnoreCase))
                return NotFound(new { message = "Not a heavy-lane title." });
            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);
            if (game.RatingCeiling > ageRestriction)
                return StatusCode(403, new { message = "This game isn't available on your account." });

            var resp = await GetGatewayAsync($"heavy/shortcut/{gameId}");
            if (resp == null || !resp.IsSuccessStatusCode)
                return StatusCode(501, new { message = "The heavy lane is not configured." });
            var text = await resp.Content.ReadAsStringAsync();
            var safe = game.Title;
            foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            // Octet-stream + a real .art filename: Android routes the tapped download to Artemis'
            // ShortcutTrampoline by the file extension, not by MIME.
            return File(System.Text.Encoding.UTF8.GetBytes(text), "application/octet-stream", $"{safe}.art");
        }

        /// <summary>Complete a Moonlight pairing PIN and record the device→user mapping. Editor-gated
        /// (plan §10): pairing is physical-seat-equivalent trust, so handing it out is deliberate.</summary>
        [HttpPost("/API/Arcade/Heavy/Pair")]
        public async Task<IActionResult> HeavyPair([FromBody] HeavyPairRequest req)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var editor = await movieDb.UserSettings.FirstOrDefaultAsync(s =>
                s.UserID == userId.Value && s.SettingKey == "CanEditMovies");
            if (editor?.SettingValue != "true")
                return StatusCode(403, new { message = "Pairing new devices is editor-only for now." });
            var pin = req?.Pin?.Trim();
            var name = req?.DeviceName?.Trim();
            if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(name))
                return BadRequest(new { message = "PIN and a device name are both required." });

            var resp = await CallGatewayAsync("heavy/pair", new { pin, name });
            if (resp == null) return StatusCode(501, new { message = "The heavy lane is not configured." });
            var result = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            bool ok = result.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            string detail = result.TryGetProperty("detail", out var d) ? d.GetString() : null;
            if (!ok) return Json(new { ok = false, detail });

            // Record (or re-own) the device → the user who completed the pairing owns its sessions.
            var row = await movieDb.HeavyClients.FirstOrDefaultAsync(c => c.ClientName == name);
            if (row == null)
                movieDb.HeavyClients.Add(new HeavyClient { ClientName = name, UserId = userId.Value, PairedUtc = DateTime.UtcNow });
            else
            {
                row.UserId = userId.Value;
                row.PairedUtc = DateTime.UtcNow;
            }
            await movieDb.SaveChangesAsync();
            logger.LogInformation("Heavy device paired: {Name} → user {UserId}", name, userId.Value);
            return Json(new { ok = true, detail = "paired" });
        }

        public class HeavyPairRequest
        {
            public string Pin { get; set; }
            public string DeviceName { get; set; }
        }

        /// <summary>Internal: the gateway resolves a Moonlight device name to the site user who
        /// paired it (HeavyClient) at heavy-session prepare — that user's save is seeded/harvested
        /// (plan §8). Server-to-server, gated by the shared arcade secret like SaveHarvested.</summary>
        [AllowAnonymous]
        [HttpPost("/API/Arcade/Internal/ResolveHeavyClient")]
        public async Task<IActionResult> ResolveHeavyClient([FromBody] ResolveHeavyClientRequest req)
        {
            var secret = config.ArcadeTokenSecret;
            if (string.IsNullOrEmpty(secret) ||
                !string.Equals(Request.Headers["X-Arcade-Internal-Secret"].ToString(), secret, StringComparison.Ordinal))
                return Unauthorized();
            if (string.IsNullOrWhiteSpace(req?.ClientName)) return BadRequest();
            var row = await movieDb.HeavyClients.FirstOrDefaultAsync(c => c.ClientName == req.ClientName);
            return row == null ? NotFound() : Json(new { userId = row.UserId });
        }

        public class ResolveHeavyClientRequest
        {
            public string ClientName { get; set; }
        }

        /// <summary>Delete one of the user's saves (My Saves): the app-DB row + the on-disk blob on Ziggy.</summary>
        [HttpDelete("/API/Arcade/Saves/{id:int}")]
        public async Task<IActionResult> DeleteSave(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var row = await movieDb.ArcadeSaves.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId.Value);
            if (row == null) return NotFound();
            await CallGatewayAsync("internal/save-delete",
                new { userId = row.UserId, gameId = row.ArcadeGameId, kind = row.Kind, slot = row.SlotId });
            movieDb.ArcadeSaves.Remove(row);
            await movieDb.SaveChangesAsync();
            return NoContent();
        }

        /// <summary>Rename a save's label (My Saves).</summary>
        [HttpPut("/API/Arcade/Saves/{id:int}")]
        public async Task<IActionResult> RenameSave(int id, [FromBody] RenameSaveRequest req)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var row = await movieDb.ArcadeSaves.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId.Value);
            if (row == null) return NotFound();
            row.Label = string.IsNullOrWhiteSpace(req?.Label) ? null : req.Label.Trim();
            row.UpdatedUtc = DateTime.UtcNow;
            await movieDb.SaveChangesAsync();
            await CallGatewayAsync("internal/save-relabel",
                new { userId = row.UserId, gameId = row.ArcadeGameId, kind = row.Kind, slot = row.SlotId, label = row.Label });
            return NoContent();
        }

        /// <summary>Download a save file (export — the manual MVP of cross-device sync).</summary>
        [HttpGet("/API/Arcade/Saves/{id:int}/download")]
        public async Task<IActionResult> DownloadSave(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var row = await movieDb.ArcadeSaves.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId.Value);
            if (row == null) return NotFound();
            var resp = await CallGatewayAsync("internal/save-read",
                new { userId = row.UserId, gameId = row.ArcadeGameId, kind = row.Kind, slot = row.SlotId });
            if (resp == null || !resp.IsSuccessStatusCode) return NotFound();
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            // dirzip = heavy-lane directory save (a plain zip of the emulator save dir — directly
            // usable on a Deck/EmuDeck, plan §8's manual bridge).
            var ext = row.Kind == "sram" ? "srm" : row.Kind == "dirzip" ? "zip" : "dat";
            var safeLabel = (row.Label ?? $"slot{row.SlotId}");
            foreach (var c in Path.GetInvalidFileNameChars()) safeLabel = safeLabel.Replace(c, '_');
            return File(bytes, "application/octet-stream", $"arcade-{row.System}-{row.ArcadeGameId}-{safeLabel}.{ext}");
        }

        /// <summary>Import (upload) a save file (source=imported) — the manual MVP of sync. SRAM goes to the
        /// canonical slot; a state becomes a new snapshot slot. The gateway mirrors the DB row.</summary>
        [HttpPost("/API/Arcade/Games/{gameId:int}/Saves/import")]
        public async Task<IActionResult> ImportSave(int gameId, IFormFile file, [FromForm] string? kind, [FromForm] string? label)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == gameId);
            if (game == null) return NotFound();
            if (file == null || file.Length == 0 || file.Length > 32L * 1024 * 1024)
                return BadRequest(new { message = "Pick a save file (up to 32 MB)." });
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var k = kind == "sram" ? "sram" : "state";
            var resp = await CallGatewayAsync("internal/save-import", new
            {
                userId = userId.Value, gameId, system = game.System, kind = k, slot = 0,
                label = string.IsNullOrWhiteSpace(label) ? Path.GetFileNameWithoutExtension(file.FileName) : label.Trim(),
                dataBase64 = Convert.ToBase64String(ms.ToArray()),
            });
            if (resp == null || !resp.IsSuccessStatusCode)
                return StatusCode(502, new { message = "Couldn't store the uploaded save." });
            return Ok();
        }

        public class RenameSaveRequest { public string? Label { get; set; } }

        public class SaveHarvestedRequest
        {
            public int UserId { get; set; }
            public int ArcadeGameId { get; set; }
            public string? System { get; set; }
            public string Kind { get; set; } = default!;
            public int SlotId { get; set; }
            public string? Label { get; set; }
            public string? CoreName { get; set; }
            public string? CoreVersion { get; set; }
            public string StorageRelPath { get; set; } = default!;
            public long SizeBytes { get; set; }
            public string? Sha256 { get; set; }
            public string? Source { get; set; }
            public bool IsAutosave { get; set; }
        }

        public class BindRequest
        {
            public string CloudRetroRoomId { get; set; } = default!;
        }

        [HttpPost("/API/Arcade/Room/{code}/Bind")]
        public async Task<IActionResult> Bind(string code, [FromBody] BindRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (request == null || string.IsNullOrWhiteSpace(request.CloudRetroRoomId))
                return BadRequest(new { message = "Missing CloudRetro room id." });

            var result = rooms.TryBind(code, userId.Value, request.CloudRetroRoomId);
            switch (result)
            {
                case ArcadeRoomService.BindResult.NotFound:
                    return NotFound(new { message = "Room not found." });
                case ArcadeRoomService.BindResult.NotCreator:
                    return StatusCode(403, new { message = "Only the room creator can bind the room." });
                case ArcadeRoomService.BindResult.AlreadyBound:
                    return Conflict(new { message = "Room is already bound." });
            }

            // Persist the bound id on the durable record too (the live source of truth is the room service).
            var session = await movieDb.ArcadeSessions
                .FirstOrDefaultAsync(s => s.RoomCode == code && s.EndedUtc == null);
            if (session != null)
            {
                session.CloudRetroRoomId = request.CloudRetroRoomId;
                await movieDb.SaveChangesAsync();
            }
            return Json(new { ok = true });
        }

        [HttpPost("/API/Arcade/Room/{code}/Join")]
        public async Task<IActionResult> Join(string code)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            var session = await movieDb.ArcadeSessions
                .FirstOrDefaultAsync(s => s.RoomCode == code && s.EndedUtc == null);
            if (session == null)
                return NotFound(new { message = "Room not found." });

            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == session.ArcadeGameId);
            if (game == null)
                return NotFound(new { message = "Game not found." });

            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);
            if (game.RatingCeiling > ageRestriction)
                return StatusCode(403, new { message = "This game isn't available on your account." });

            var join = rooms.TryJoin(code, userId.Value);
            switch (join.Outcome)
            {
                case ArcadeRoomService.JoinOutcome.NotFound:
                    return NotFound(new { message = "Room not found." });
                case ArcadeRoomService.JoinOutcome.NotBound:
                    return Conflict(new { code = "starting", message = "The room is still starting — try again in a moment." });
                case ArcadeRoomService.JoinOutcome.Full:
                    return Conflict(new { code = "full", message = "The room is full." });
            }

            var boundRoomId = rooms.BoundRoomId(code) ?? string.Empty;
            var (launchKey, discCount) = await ResolveLaunchAsync(game);
            // Use the room's ACTUAL system from its bound id (a capture room is "capture", not the
            // catalog "switch") so a joiner's descriptor.system matches the creator's — client tables
            // keyed on descriptor.system (aspect fallback, the "Live" label) would otherwise diverge (R2).
            var (joinSystem, joinCore) = RoomSystemAndCore(game.System, boundRoomId);
            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, launchKey, joinSystem),
                code, boundRoomId, join.PlayerSlot, isCreator: false);
            descriptor = descriptor with
            {
                CoreKey = joinCore,
                CanRewind = ArcadeRewindSupport.IsArmed(joinSystem, joinCore),
            };

            // The room's codec (patch 0036): a joiner's track mime is fixed at INIT_WEBRTC and must match
            // the room's one encoder, so every joiner echoes the creator's choice.
            var roomCodec = rooms.RoomVideoCodec(code);
            if (roomCodec != "")
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&codec=" + roomCodec };

            // The room's Wii controller scheme: like codec (and unlike hwctx/bitrate) this changes what
            // button bits the joiner's OWN client must send, so every joiner echoes the creator's choice.
            var joinCtrlScheme = rooms.RoomControllerScheme(code);
            if (joinCtrlScheme != "")
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&ctrlscheme=" + joinCtrlScheme };

            return Json(ToJson(descriptor, discCount, session.IsCompetitive));
        }

        /// <summary>
        /// Local multiplayer: claim an ADDITIONAL controller port for a player who is already seated in
        /// the room — one per extra controller plugged into their machine. Returns a normal join
        /// descriptor for the new slot; the browser opens one extra INPUT-ONLY CloudRetro connection with
        /// it (the wire protocol routes input by connection, so an extra local pad needs an extra
        /// connection, not an extra byte). Presence rides the user's existing heartbeat.
        /// </summary>
        [HttpPost("/API/Arcade/Room/{code}/ClaimSeat")]
        public async Task<IActionResult> ClaimSeat(string code)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            var session = await movieDb.ArcadeSessions
                .FirstOrDefaultAsync(s => s.RoomCode == code && s.EndedUtc == null);
            if (session == null)
                return NotFound(new { message = "Room not found." });

            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == session.ArcadeGameId);
            if (game == null)
                return NotFound(new { message = "Game not found." });

            var claim = rooms.TryClaimExtraSeat(code, userId.Value);
            switch (claim.Outcome)
            {
                case ArcadeRoomService.JoinOutcome.NotFound:
                    return NotFound(new { message = "Room not found." });
                case ArcadeRoomService.JoinOutcome.NotBound:
                    return Conflict(new { code = "starting", message = "The room is still starting — try again in a moment." });
                case ArcadeRoomService.JoinOutcome.NotSeated:
                    return Conflict(new { code = "notSeated", message = "Only a seated player can add local players." });
                case ArcadeRoomService.JoinOutcome.Full:
                    return Conflict(new { code = "full", message = "All the controller ports are taken." });
            }

            var boundRoomId = rooms.BoundRoomId(code) ?? string.Empty;
            var (launchKey, discCount) = await ResolveLaunchAsync(game);
            // Match the room's real system (capture rooms are "capture", not "switch") — see R2 in Join.
            var (claimSystem, claimCore) = RoomSystemAndCore(game.System, boundRoomId);
            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, launchKey, claimSystem),
                code, boundRoomId, claim.PlayerSlot, isCreator: false);
            descriptor = descriptor with
            {
                CoreKey = claimCore,
                CanRewind = ArcadeRewindSupport.IsArmed(claimSystem, claimCore),
            };

            // Input-only sessions never attach media, but their peer still negotiates a track — keep its
            // mime consistent with the room's encoder (patch 0036) like every other descriptor.
            var claimCodec = rooms.RoomVideoCodec(code);
            if (claimCodec != "")
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&codec=" + claimCodec };

            // An input-only extra seat still sends button bits — it needs the room's controller scheme too.
            var claimCtrlScheme = rooms.RoomControllerScheme(code);
            if (claimCtrlScheme != "")
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&ctrlscheme=" + claimCtrlScheme };

            return Json(ToJson(descriptor, discCount, session.IsCompetitive));
        }

        public class ReleaseSeatRequest { public int Slot { get; set; } }

        /// <summary>Release one of the caller's extra local-player seats (never their primary — Leave does that).</summary>
        [HttpPost("/API/Arcade/Room/{code}/ReleaseSeat")]
        public IActionResult ReleaseSeat(string code, [FromBody] ReleaseSeatRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            var ok = request != null && rooms.ReleaseSeat(code, userId.Value, request.Slot);
            return Json(new { ok });
        }

        /// <param name="ttffMs">Time-to-first-frame the browser measured for its session (ms from opening the
        /// signaling socket to the first presented video frame). Sent on ONE beat once known; kept on the
        /// session row if the row has none yet. A query param so an older tab's bodiless beat keeps working.</param>
        [HttpPost("/API/Arcade/Room/{code}/Heartbeat")]
        public async Task<IActionResult> Heartbeat(string code, [FromQuery] int? ttffMs = null)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var status = rooms.Heartbeat(code, userId.Value);
            if (status == null)
            {
                // Unknown room but someone's page is heartbeating it → the pod restarted (deploy) and
                // wiped the in-memory registry while the session kept running. Rehydrate from the durable
                // ArcadeSession row (live + bound = the emulator-side room genuinely exists), re-seat the
                // heartbeater, and carry on — invitees' rail/join then work again within one beat (≤12 s).
                // A heartbeat is the proof-of-life gate: the Join/rail paths never resurrect on their own,
                // so stale LIVE rows (crashed sessions that missed their EndedUtc stamp) stay dead.
                var session = await movieDb.ArcadeSessions
                    .Where(s => s.RoomCode == code && s.EndedUtc == null && s.CloudRetroRoomId != null)
                    .OrderByDescending(s => s.CreatedUtc)
                    .FirstOrDefaultAsync();

                // Nothing live under that code, but a browser is still beating it — so ALSO look at rooms
                // the reaper recently closed. A closed row used to be a one-way door: the reaper needs only
                // a five-minute gap in durable heartbeats to stamp EndedUtc (a deploy rolling the pod is
                // enough), and from that moment this endpoint 404'd forever for a player who never left.
                // The room kept streaming — CloudRetro tracks their WebRTC connection, not our roster — but
                // no fresh control token was ever minted again, so every quicksave came back "This room
                // pass expired" for the rest of the night (2026-07-26, Mario BAZR; the third repeat of this
                // family of bug). A live beat is proof the room outlived its obituary: take it back.
                if (session == null)
                {
                    var revivable = DateTime.UtcNow - RoomRevivalWindow;
                    session = await movieDb.ArcadeSessions
                        .Where(s => s.RoomCode == code && s.CloudRetroRoomId != null && s.EndedUtc >= revivable)
                        .OrderByDescending(s => s.CreatedUtc)
                        .FirstOrDefaultAsync();
                    if (session != null)
                    {
                        var closedAt = session.EndedUtc;
                        session.EndedUtc = null;
                        await movieDb.SaveChangesAsync();
                        logger.LogInformation(
                            "Arcade room {Code} reopened: user {User} is still in a room closed at {Ended:u}",
                            code, userId.Value, closedAt);
                    }
                }

                var game = session == null ? null
                    : await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == session.ArcadeGameId);
                if (session == null || game == null)
                    return NotFound(new { message = "Room not found." });

                rooms.Rehydrate(code, game.Id, game.MaxPlayers, session.CreatedByUserId, session.CloudRetroRoomId!);
                rooms.TryJoin(code, userId.Value); // re-seat the heartbeater (their live session already has a slot)
                logger.LogInformation("Arcade room {Code} rehydrated from DB after registry loss (user {User})", code, userId.Value);
                status = rooms.Heartbeat(code, userId.Value);
                if (status == null)
                    return NotFound(new { message = "Room not found." });
            }

            // Durable proof-of-life for the reaper (throttled to one UPDATE per room per 30 s, whatever the
            // player count). Without this the ONLY record that a room is alive is the in-memory registry,
            // which a deploy wipes — and the reaper then cannot tell a live room from a corpse, so it left
            // every restarted-through session open forever. Fire-and-forget-ish: a failed stamp just means
            // the row looks staler than it is, and the next beat (≤12 s) writes it again.
            var beatNow = DateTime.UtcNow;
            if (rooms.ShouldPersistHeartbeat(code, beatNow))
            {
                await movieDb.ArcadeSessions
                    .Where(s => s.RoomCode == code && s.EndedUtc == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenUtc, beatNow));
            }

            // Time-to-first-frame (arcade perf program P1, 2026-09-05): the shim measures connect() -> first
            // presented frame and the page carries it on one beat. Keep the FIRST sane value per session row:
            // normally the creator's, the one that paid for ROM staging + core load. Observability only,
            // nothing reads it back into a decision. Bounded so a broken client cannot store garbage.
            if (ttffMs is > 0 and <= 600_000)
            {
                await movieDb.ArcadeSessions
                    .Where(s => s.RoomCode == code && s.EndedUtc == null && s.TtffMs == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.TtffMs, ttffMs.Value));
            }

            var roster = status.PlayerUserIds.Concat(status.SpectatorUserIds).Distinct().ToList();
            var names = await movieDb.Users
                .Where(u => roster.Contains(u.UserID))
                .Select(u => new { u.UserID, u.Username })
                .ToDictionaryAsync(u => u.UserID, u => u.Username);

            var players = status.PlayerUserIds
                .Select(id => new { name = names.GetValueOrDefault(id) ?? "Someone", you = id == userId.Value })
                .ToList();
            var spectators = status.SpectatorUserIds
                .Select(id => new { name = names.GetValueOrDefault(id) ?? "Someone", you = id == userId.Value })
                .ToList();

            // Re-mint the room's control token (quicksave/snapshot/load) on every beat. Those gateway
            // endpoints re-validate the capability's EXPIRY on each call, but the browser reuses the one token
            // the WS join carried for the whole session — so on a play session longer than the token TTL it
            // lapses and saves fail (the gateway 500s the rejected token, which the browser then reports as a
            // spurious CORS error). A present player beats every ~12 s, so a token refreshed here never goes
            // stale. Bound rooms only (the id must exist to mint against); the client keeps its last good token
            // if this is ever absent. See IArcadeHost.MintControlToken.
            //
            // NOT gated on holding a seat. That gate is what made a lost seat break saving for a whole
            // session: a player pruned or beaconed out of the registry (a frozen tab, a phone switching
            // apps) keeps playing — CloudRetro tracks their connection, not our roster — but stopped
            // being handed a fresh capability, so the page fell back to its join token and every
            // quicksave 403'd "This room pass expired". ArcadeRoomService now re-seats them, and this is
            // the second line of defence: presence bookkeeping must never be what decides whether a
            // player can save. Spectators stay excluded (they have no business writing the room's state);
            // the gateway re-checks room id + game id on every call regardless.
            string? saveToken = null;
            var boundRoomId = rooms.BoundRoomId(code);
            if (status.Bound && boundRoomId != null && !status.YouAreSpectator && rooms.GameId(code) is int gid)
                // The slot is informational in a control token — the save endpoints authorize on the room
                // id and game id, not the port — so a momentarily seatless player signs with "no port".
                saveToken = host.MintControlToken(
                    userId.Value, gid, code, boundRoomId, status.YourSlot ?? ArcadeRoomService.SpectatorSlot);

            return Json(new
            {
                bound = status.Bound,
                maxPlayers = status.MaxPlayers,
                yourSlot = status.YourSlot,
                players,
                spectators,
                youAreSpectator = status.YouAreSpectator,
                saveToken,
            });
        }

        [HttpPost("/API/Arcade/Room/{code}/Leave")]
        public IActionResult Leave(string code)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            rooms.Leave(code, userId.Value);
            return Json(new { ok = true });
        }

        // Multi-disc: a game with sibling disc rows launches its .m3u playlist (patch 0005 disc-swap) rather
        // than one disc. Returns the CloudRetro launch key (.m3u basename for multi-disc, else the row's key)
        // and the disc count (0/1 = single). The gateway JIT-materializes the .m3u from the disc archives.
        private async Task<(string launchKey, int discCount)> ResolveLaunchAsync(ArcadeGame game)
        {
            var rows = await movieDb.ArcadeGames
                .Where(g => g.IsEnabled && g.System == game.System && g.Title == game.Title).ToListAsync();
            var (discCount, m3uKey) = ArcadeVersions.MultiDisc(game, rows);
            return (m3uKey ?? game.CloudRetroGameKey, discCount);
        }

        private static object ToJson(ArcadeJoinDescriptor d, int discCount = 0, bool competitive = false) => new
        {
            roomCode = d.RoomCode,
            wsUrl = d.WsUrl,
            playerSlot = d.PlayerSlot,
            // How the room runs — sourced from the durable ArcadeSession for joiners (restart-safe), so
            // every member's room page can hide Save/Load and show the competitive badge, not just the
            // creator's. Omitted (false) for an ordinary room.
            competitive,
            // Watch-only seat (playerSlot -1): the shim skips t=108 and never opens its input pump, so this
            // browser holds no controller port. Derived, so the token stays the single source of truth.
            spectator = d.PlayerSlot == ArcadeRoomService.SpectatorSlot,
            gameKey = d.GameKey,
            iceConfig = d.IceConfig.Select(i => new { urls = i.Urls, username = i.Username, credential = i.Credential }).ToList(),
            isCreator = d.IsCreator,
            system = d.System,
            // The alternate core this room booted, when it isn't the system's default. Kept out of
            // `system` on purpose — every client table (input profiles, save-state support, the system
            // label) is keyed by system, and folding the core in made all of them miss for joiners.
            coreKey = string.IsNullOrEmpty(d.CoreKey) ? null : d.CoreKey,
            // Whether the worker has this room's rewind ring armed. Per-CORE, so only the server can
            // answer it; the room page offers the Rewind control on this and nothing else.
            canRewind = d.CanRewind,
            discCount,
            // The shim copies these straight into its t=104 GAME_START. Omitted when empty so a room with no
            // cheats sends the same packet it always did.
            coreOptions = d.CoreOptions is { Count: > 0 } ? d.CoreOptions : null,
            cheats = d.CheatCodes is { Count: > 0 } ? d.CheatCodes : null,
            // No per-user RA creds ride the wire any more — the worker runs a single SITE service account
            // as the scoring engine (spectator mode). The room only tells the worker whether it's a
            // COMPETITIVE (legit) run, and that travels on `competitive` above; the shim maps it to t=104.
        };

        // A short, URL-safe invite code, regenerated on the rare collision with a live room.
        private string NewRoomCode()
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var bytes = RandomNumberGenerator.GetBytes(CodeLength);
                var chars = new char[CodeLength];
                for (int i = 0; i < CodeLength; i++)
                    chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
                var code = new string(chars);
                if (rooms.BoundRoomId(code) == null && rooms.Snapshot().All(r => r.RoomCode != code))
                    return code;
            }
            // Astronomically unlikely; fall back to a longer random string.
            return new string(Enumerable.Range(0, CodeLength + 4)
                .Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray());
        }
    }
}
