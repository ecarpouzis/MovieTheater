using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    public class ArcadeController : Controller
    {
        // URL-safe, unambiguous room codes (RFC 4648 base32 alphabet).
        private const string CodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        private const int CodeLength = 6;

        private readonly MovieDb movieDb;
        private readonly IArcadeHost host;
        private readonly ArcadeRoomService rooms;
        private readonly ILogger<ArcadeController> logger;
        private readonly MovieTheaterConfiguration config;

        public ArcadeController(MovieDb movieDb, IArcadeHost host, ArcadeRoomService rooms, ILogger<ArcadeController> logger, MovieTheaterConfiguration config)
        {
            this.movieDb = movieDb;
            this.host = host;
            this.rooms = rooms;
            this.logger = logger;
            this.config = config;
        }

        private int? GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
        }

        private async Task<int> GetAgeRestrictionAsync(int userId)
        {
            var setting = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == userId);
            return setting != null && int.TryParse(setting.SettingValue, out var parsed) ? parsed : 100;
        }

        // No explicit choice → default English + non-modded (but a name search spans everything).
        private static (string Region, string Variant) NormalizeScope(string region, string variant, bool searching)
        {
            var reg = string.IsNullOrWhiteSpace(region) ? (searching ? "all" : "english") : region.Trim().ToLowerInvariant();
            var var_ = string.IsNullOrWhiteSpace(variant) ? (searching ? "all" : "release") : variant.Trim().ToLowerInvariant();
            return (reg, var_);
        }

        // The match set: rows that make a game QUALIFY for a card. Shared by Games and GameLetters so a
        // letter's offset indexes EXACTLY the list Games pages — the moment the two disagree about what
        // matches, every letter jump lands on the wrong card.
        private static IQueryable<ArcadeGame> ApplyCardFilters(
            IQueryable<ArcadeGame> baseQ, string system, int? maxPlayers, string genre, string search, string reg, string var_)
        {
            var matchQ = baseQ;
            if (!string.IsNullOrWhiteSpace(system)) matchQ = matchQ.Where(g => g.System == system);
            if (maxPlayers is int mp && mp > 1) matchQ = matchQ.Where(g => g.MaxPlayers >= mp);
            if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); matchQ = matchQ.Where(g => g.Title.Contains(s)); }

            if (reg == "english")
                matchQ = matchQ.Where(g => g.Region == null || (g.Region != "Japan" && g.Region != "Asia" && g.Region != "Other"));
            else if (reg != "all")
                matchQ = matchQ.Where(g => g.Region == reg || (g.Region ?? "").ToLower() == reg);

            if (var_ == "release")
                matchQ = matchQ.Where(g => g.Variant == "Release" || g.Variant == null);
            else if (var_ == "modded")
                matchQ = matchQ.Where(g => g.Variant != "Release" && g.Variant != null);
            else if (var_ != "all")
                matchQ = matchQ.Where(g => g.Variant == var_ || (g.Variant ?? "").ToLower() == var_);

            // Genre filter (IGDB-sourced, stored on the card anchor): a card qualifies if ANY of its rows
            // carries the genre — a correlated EXISTS so it composes with the version-level region/variant gates.
            if (!string.IsNullOrWhiteSpace(genre))
            {
                var gr = genre.Trim();
                matchQ = matchQ.Where(g => baseQ.Any(a => a.System == g.System && a.Title == g.Title
                    && a.Genres != null && a.Genres.Contains(gr)));
            }
            return matchQ;
        }

        // ONE CARD PER GAME (docs/arcade-dedupe-multidisc-plan.md): rows are grouped by (System, Title) into
        // games, each carrying a version dropdown (region/rev/edition/disc/hack), so the same game's many
        // ROMs collapse to a single card. Filters gate CARDS by version existence — a game shows iff it has
        // ≥1 version matching. Defaults are English + non-modded; region/variant "all" broadens, a specific
        // value narrows. Grouping is query-time so ingests fold in automatically. Age gate always applies.
        //
        // `skip` is an absolute offset into the sorted card list and WINS over `page`: the lobby's pager
        // seeks to a letter bucket, which starts wherever it starts — rounding that down to a page boundary
        // would land the user in the tail of the previous letter.
        [HttpGet("/API/Arcade/Games")]
        public async Task<IActionResult> Games(
            string system = null, string region = null, int? maxPlayers = null,
            string variant = null, string genre = null, string sort = null, string search = null,
            int page = 1, int pageSize = 60, int? skip = null)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            bool searching = !string.IsNullOrWhiteSpace(search);
            var (reg, var_) = NormalizeScope(region, variant, searching);

            var baseQ = await AgeVisibleGamesAsync(userId.Value);
            var matchQ = ApplyCardFilters(baseQ, system, maxPlayers, genre, search, reg, var_);

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 120);
            int skipRows = Math.Max(0, skip ?? (page - 1) * pageSize);
            // Card-level aggregates for sorting: rating/year live on the anchor, players is the card max.
            var groupedQ = matchQ.GroupBy(g => new { g.System, g.Title })
                .Select(grp => new
                {
                    grp.Key.System,
                    grp.Key.Title,
                    Sort = grp.Min(x => x.SortTitle),
                    // Sort on the confidence-weighted score, never the raw one: a 1-vote 100 must not outrank a
                    // 4,000-vote 94 (that's how American Chopper became the top-rated PS2 game). See
                    // ArcadeRatingWeightsCommand, which computes this.
                    Rating = grp.Max(x => x.RatingWeighted),
                    Year = grp.Max(x => x.Year),
                    Players = grp.Max(x => (int)x.MaxPlayers),
                });
            var totalCount = await groupedQ.CountAsync();
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
                .Skip(skipRows).Take(pageSize).ToListAsync();

            // All age-visible versions of the paged games (superset by System/Title IN, trimmed to exact
            // page keys in memory) — the dropdown lists every version, not just the ones that matched.
            var pageSystems = pageKeys.Select(k => k.System).Distinct().ToList();
            var pageTitles = pageKeys.Select(k => k.Title).Distinct().ToList();
            var versionRows = await baseQ.Where(g => pageSystems.Contains(g.System) && pageTitles.Contains(g.Title)).ToListAsync();
            var keySet = pageKeys.Select(k => (k.System, k.Title)).ToHashSet();
            var byGame = versionRows.Where(g => keySet.Contains((g.System, g.Title)))
                .GroupBy(g => (g.System, g.Title))
                .ToDictionary(x => x.Key, x => x.ToList());

            // Cheat counts for the page's ROMs, one grouped query (not per card). The card needs only to know
            // whether to render the picker at all; the list itself is lazy-loaded when it's opened.
            var pageVersionIds = versionRows.Select(g => g.Id).ToList();
            var cheatCounts = await movieDb.ArcadeCheats
                .Where(c => pageVersionIds.Contains(c.ArcadeGameId))
                .GroupBy(c => c.ArcadeGameId)
                .Select(g => new { GameId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.GameId, x => x.Count);

            // Default-on cheats must ship WITH the card, not with the lazy-loaded cheat list: PS2's widescreen
            // patch is pre-selected, and a player who never opens the picker still expects it applied. Tiny set
            // (only the ~150 PS2 widescreen rows are DefaultOn today).
            var defaultCheats = await movieDb.ArcadeCheats
                .Where(c => pageVersionIds.Contains(c.ArcadeGameId) && c.DefaultOn)
                .Select(c => new { c.ArcadeGameId, c.Id })
                .ToListAsync();
            var defaultsByVersion = defaultCheats.GroupBy(c => c.ArcadeGameId)
                .ToDictionary(g => g.Key, g => g.Select(c => "c" + c.Id).ToList());

            string specificRegion = reg is "english" or "all" ? null : reg;
            var games = pageKeys.Select(k =>
            {
                byGame.TryGetValue((k.System, k.Title), out var vs);
                vs ??= new List<ArcadeGame>();
                // Build launchable versions — multi-disc sets collapse to one entry (DiscCount > 1). The
                // first is the card's default selection + box-art source; a region filter floats that region up.
                var versions = ArcadeVersions.Build(vs, specificRegion);
                var rep = versions.FirstOrDefault();
                // Box art is shared across the card's versions. Point at a sibling that already HAS art (so a
                // "(Rev A)" default doesn't hide the base "(USA)" box), else the lowest-id row — the canonical
                // card file the image route writes a fresh fetch to. Filter-independent, so it stays one file.
                var artRow = vs.FirstOrDefault(g => g.BoxArtPath != null) ?? vs.OrderBy(g => g.Id).FirstOrDefault();
                // IGDB enrichment is stored on the card's anchor (lowest-id) row, same convention as box art.
                var meta = vs.OrderBy(g => g.Id).FirstOrDefault();
                return new
                {
                    key = k.System + "|" + k.Title,
                    title = k.Title,
                    system = k.System,
                    artId = artRow?.Id ?? rep?.Id ?? 0,
                    hasBoxArt = vs.Any(g => g.BoxArtPath != null),
                    year = rep?.Year ?? meta?.Year,
                    maxPlayers = versions.Count > 0 ? versions.Max(v => v.MaxPlayers) : (byte)1,
                    versionCount = versions.Count,
                    // Review score: LaunchBox is the primary source (83% of cards); IGDB is a fallback for the
                    // ~541 cards LaunchBox doesn't rate. The card shows the RAW score — the weighted one above
                    // exists only to order the grid.
                    rating = (meta?.LaunchBoxRating ?? meta?.RatingScore) is double rs ? (int?)Math.Round(rs) : null,
                    ratingCount = meta?.LaunchBoxRatingCount ?? meta?.RatingCount,
                    genres = meta?.Genres,
                    themes = meta?.Themes,
                    summary = meta?.Summary,
                    developer = meta?.Developer,
                    publisher = meta?.Publisher,
                    gameModes = meta?.GameModes,
                    esrb = meta?.EsrbRating,
                    // 'heavy' = Moonlight-streamed (plan §7.1): the card's action becomes
                    // Prepare/Play-via-Moonlight instead of creating a CloudRetro room.
                    lane = vs.Select(g => g.Lane).FirstOrDefault(l => l != null),
                    // capture (H5): a heavy title on the capture allowlist ALSO offers browser play — the
                    // modal shows "Play in browser" beside the Artemis launch (docs/arcade-capture-worker-plan.md §6.3).
                    capture = vs.Any(g => string.Equals(g.Lane, "heavy", StringComparison.OrdinalIgnoreCase) && CloudRetroHost.IsCaptureEnabled(g.CloudRetroGameKey)),
                    // Per-launch GL/Vulkan force (play-button dropdown): only 3D systems with a real
                    // render-context choice offer it; see CloudRetroHost.HwToggleSystems.
                    supportsHwToggle = CloudRetroHost.SupportsHwToggle(k.System),
                    versions = versions.Select(v => new
                    {
                        id = v.Id, label = v.Label, region = v.Region,
                        variant = v.Variant, year = v.Year, maxPlayers = v.MaxPlayers, discCount = v.DiscCount,
                        // Stored rows + the system-wide option cheats. Only imported systems get code rows, so
                        // this is already zero where the core would ignore them (see ArcadeCheatCatalog).
                        cheatCount = (cheatCounts.TryGetValue(v.Id, out var cc) ? cc : 0)
                                     + ArcadeCheatCatalog.SystemOptionCheats(k.System).Count,
                        defaultCheats = defaultsByVersion.TryGetValue(v.Id, out var dc) ? dc : new List<string>(),
                    }).ToList(),
                };
            }).ToList();

            return Json(new { games, totalCount, page, pageSize, skip = skipRows });
        }

        // The A–Z bucket a card sorts into. Anything not starting A–Z (numbers, punctuation) is "#".
        private static string LetterOf(string sortKey)
        {
            if (string.IsNullOrEmpty(sortKey)) return "#";
            var c = char.ToUpperInvariant(sortKey[0]);
            return c >= 'A' && c <= 'Z' ? c.ToString() : "#";
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
            string system = null, string region = null, int? maxPlayers = null,
            string variant = null, string genre = null, string search = null)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            bool searching = !string.IsNullOrWhiteSpace(search);
            var (reg, var_) = NormalizeScope(region, variant, searching);

            var baseQ = await AgeVisibleGamesAsync(userId.Value);
            var matchQ = ApplyCardFilters(baseQ, system, maxPlayers, genre, search, reg, var_);

            // The same grouping + the same default ordering Games uses, so index i here is card i there.
            var sortKeys = await matchQ.GroupBy(g => new { g.System, g.Title })
                .Select(grp => new { Sort = grp.Min(x => x.SortTitle), grp.Key.Title })
                .OrderBy(x => x.Sort).ThenBy(x => x.Title)
                .Select(x => x.Sort)
                .ToListAsync();

            // First offset wins if a letter turns out not to be contiguous (a collation could sort some
            // punctuation between letters): jumping to the bucket's first card is still right.
            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            var offsets = new Dictionary<string, int>();
            for (int i = 0; i < sortKeys.Count; i++)
            {
                var letter = LetterOf(sortKeys[i]);
                if (!counts.ContainsKey(letter))
                {
                    order.Add(letter);
                    counts[letter] = 0;
                    offsets[letter] = i;
                }
                counts[letter]++;
            }

            var letters = order.Select(l => new { letter = l, count = counts[l], offset = offsets[l] }).ToList();
            return Json(new { total = sortKeys.Count, letters });
        }

        // Facets for the lobby filter controls: the systems / regions / variants actually present in the
        // viewer's age-visible catalog, each with a count, plus how many are multiplayer.
        [HttpGet("/API/Arcade/Filters")]
        public async Task<IActionResult> Filters()
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            var q = await AgeVisibleGamesAsync(userId.Value);
            // Count CARDS, not version rows. The grid groups by (System, Title), so counting rows made the
            // picker advertise "All systems (24710)" for a catalog that renders 17,291 cards. One DISTINCT
            // pull (~17k pairs) then grouped in memory.
            var cardKeys = await q.Select(g => new { g.System, g.Title }).Distinct().ToListAsync();
            var systems = cardKeys.GroupBy(k => k.System)
                .Select(x => new { value = x.Key, count = x.Count() }).OrderByDescending(x => x.count).ToList();
            var regions = await q.GroupBy(g => g.Region)
                .Select(x => new { value = x.Key ?? "Unknown", count = x.Count() }).OrderByDescending(x => x.count).ToListAsync();
            var variants = await q.GroupBy(g => g.Variant)
                .Select(x => new { value = x.Key ?? "Release", count = x.Count() }).OrderByDescending(x => x.count).ToListAsync();
            // Genre facet: genres are comma-joined on the card anchor, so split + count in memory.
            var genreStrings = await q.Where(g => g.Genres != null).Select(g => g.Genres).ToListAsync();
            var genres = genreStrings
                .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .GroupBy(x => x)
                .Select(x => new { value = x.Key, count = x.Count() })
                .OrderByDescending(x => x.count).Take(40).ToList();
            return Json(new
            {
                total = cardKeys.Count,
                multiplayer = await q.CountAsync(g => g.MaxPlayers >= 2),
                systems, regions, variants, genres,
            });
        }

        private async Task<IQueryable<ArcadeGame>> AgeVisibleGamesAsync(int userId)
        {
            var ageRestriction = await GetAgeRestrictionAsync(userId);
            return movieDb.ArcadeGames.Where(g => g.IsEnabled && g.RatingCeiling <= ageRestriction);
        }

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

            /// <summary>Resume from a specific snapshot slot (≥1) instead of the Continue slot 0. 0 = Continue.</summary>
            public int SeedSlot { get; set; }

            /// <summary>Per-room video encoder bitrate in kbps (arcade per-room quality). 0 = use the worker's
            /// config default. Clamped server-side; only the creator's choice takes effect (one encoder/room).</summary>
            public int VideoBitrateKbps { get; set; }

            /// <summary>Per-room opus FEC: 0 = config default, 1 = force on (remote-friendly), 2 = force off
            /// (LAN-only, saves audio-packet bytes). Rides the WS URL to the worker like the other room flags.</summary>
            public int AudioFec { get; set; }

            /// <summary>In-frame packet pacing window in ms (worker patch 0028), 0 = off. The lobby's
            /// Network profile sets it (Remote 5, 5G 8); the worker spreads each encoded frame's RTP
            /// burst over this window so it doesn't slam cellular/shallow-buffer queues. LAN keeps 0 —
            /// wire-speed bursts are the minimum-latency default.</summary>
            public int PaceMs { get; set; }

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

        /// <summary>The full offer for one ROM: its stored rows (curated option cheats first — they carry
        /// negative ordinals — then the community codes in upstream order), plus any option cheats that apply
        /// to the whole system. Code cheats are withheld on systems whose core ignores them, so the picker
        /// can't show a toggle that provably does nothing.</summary>
        private async Task<List<CheatOffer>> BuildCheatListAsync(ArcadeGame game)
        {
            var rows = await movieDb.ArcadeCheats
                .Where(c => c.ArcadeGameId == game.Id)
                .OrderBy(c => c.Ordinal)
                .ToListAsync();

            var offers = new List<CheatOffer>();
            foreach (var r in rows)
            {
                if (r.Kind == "code" && !ArcadeCheatCatalog.SupportsCheatCodes(game.System)) continue;
                offers.Add(new CheatOffer("c" + r.Id, r.Name, r.Kind, r.DefaultOn, r.Note, r.OptionKey, r.OptionValue, r.Code));
            }

            // System-wide option cheats (Dreamcast/GameCube widescreen) sit above the per-game rows. Skipped
            // when a stored row already sets the same option key, so a game can override the system default.
            var haveKeys = offers.Where(o => o.OptionKey != null).Select(o => o.OptionKey!).ToHashSet(StringComparer.Ordinal);
            int insertAt = 0;
            foreach (var o in ArcadeCheatCatalog.SystemOptionCheats(game.System))
            {
                if (haveKeys.Contains(o.Key)) continue;
                offers.Insert(insertAt++, new CheatOffer("s:" + o.Key, o.Name, "option", o.DefaultOn, o.Note, o.Key, o.Value, null));
            }
            return offers;
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

            var session = new ArcadeSession
            {
                ArcadeGameId = game.Id,
                RoomCode = roomCode,
                CreatedByUserId = userId.Value,
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

            // Register live state with the creator in seat 0. The CloudRetro room isn't created yet — the
            // creator's browser does that (empty room_id) and then calls Bind (§8 steps 2–3).
            rooms.CreateRoom(roomCode, game.Id, game.MaxPlayers, userId.Value, codec);

            string launchKey;
            int discCount;
            string roomSystem;
            if (isCapture)
            {
                // The capture launch key IS the CloudRetroGameKey (== the heavy descriptor id == the
                // worker's .capture stub filename). The room's system is the literal "capture" so the
                // gateway routes it to the capture worker (zone) and the worker's branch fires on it.
                launchKey = game.CloudRetroGameKey!;
                discCount = 0;
                roomSystem = "capture";
            }
            else
            {
                (launchKey, discCount) = await ResolveLaunchAsync(game);
                roomSystem = game.System;
            }

            // Durable, user-scoped saves (docs/arcade-saves-plan.md): instead of an empty room id ("create
            // a random room"), the creator carries a DETERMINISTIC id encoding (user, game, slot 0, system)
            // with the launch key as the CloudRetro-resolvable suffix. This makes the session's save files
            // predictable, so the gateway seeds this user's save before boot and harvests it after — the
            // save belongs to the user+game, not the room. Slot 0 = the "Continue" slot (multi-slot is S3).
            // Capture rooms encode system="capture" here: HeavyVault owns their saves (the gateway skips the
            // CloudRetro save mount for them), and the worker parses this id for the owner + heavy app id.
            var saveId = ArcadeSaveId.Mint(userId.Value, game.Id, 0, roomSystem, launchKey);
            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, launchKey, roomSystem),
                roomCode, cloudRetroRoomId: saveId, playerSlot: 0, isCreator: true);

            // "New game": tell the gateway (via ?fresh=1 on the WS URL) to clear the mount so the game
            // boots clean instead of resuming the saved slot. Safe unsigned — it only clears the owner's own save.
            if (request.NewGame)
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
            var vbr = request.VideoBitrateKbps > 0
                ? Math.Clamp(request.VideoBitrateKbps, 500, 20000)
                : CloudRetroHost.DefaultVideoBitrateKbps(roomSystem); // "capture" → 1080p desktop ceiling
            descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&vbr=" + vbr };
            if (request.AudioFec is 1 or 2)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&fec=" + request.AudioFec };
            // In-frame packet pacing (patch 0028). Capture rooms DEFAULT it to 8 ms server-side (like vbr
            // defaults): the capture stream is the fattest we send (12 Mbps H.264, intra-refresh ⇒ every
            // frame is sizable), and un-paced bursts on tablet WiFi queue behind each other and jitter the
            // audio packets sharing the air, growing the browser's audio jitter buffer (plan §12C). An
            // explicit lobby choice still wins.
            var paceMs = request.PaceMs > 0 ? request.PaceMs : (isCapture ? 8 : 0);
            if (paceMs > 0)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&pace=" + Math.Clamp(paceMs, 1, 20) };
            // Codec rides the WS URL like vbr/fec — but unlike them it ALSO rides every joiner's URL
            // (see Join/ClaimSeat): the shim echoes it in INIT_WEBRTC, where each peer's track mime is
            // fixed, and every track must match the room's one encoder.
            if (codec != "")
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&codec=" + codec };

            // Per-launch GL/Vulkan force (play-button dropdown). Allowlist hard AND re-gate on
            // SupportsHwToggle server-side — never trust the client to only ever send this for an
            // eligible system. Unlike codec, this does NOT need to ride Join/ClaimSeat: it only
            // affects the creator's one-time CoreLoad when the room's core boots; joiners just attach
            // to the already-running core. Meaningless for capture rooms (a native desktop capture,
            // not a libretro core).
            var hwctx = CloudRetroHost.SupportsHwToggle(game.System)
                ? request.HwContext?.Trim().ToLowerInvariant() switch { "gl" => "gl", "vulkan" => "vulkan", _ => "" }
                : "";
            if (hwctx != "" && !isCapture)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&hwctx=" + hwctx };

            // Per-room cheats (arcade cheats feature). Resolve the ids the creator ticked against what this
            // exact ROM actually offers — never trust the client's idea of what a cheat is, because a code is
            // a raw memory poke and one aimed at another game's addresses corrupts state rather than failing.
            if (!isCapture && request.Cheats is { Count: > 0 })
            {
                var offered = await BuildCheatListAsync(game);
                var picked = offered.Where(o => request.Cheats.Contains(o.Id, StringComparer.Ordinal))
                    .Take(ArcadeCheatCatalog.MaxCheatsPerRoom).ToList();

                var coreOptions = picked.Where(o => o.Kind == "option" && o.OptionKey != null)
                    .GroupBy(o => o.OptionKey!, StringComparer.Ordinal)   // one value per key; first wins
                    .ToDictionary(g => g.Key, g => g.First().OptionValue ?? "enabled", StringComparer.Ordinal);

                // Master switches: some option cheats are read by the core only behind a gate option
                // (pcsx2_half_pixel_offset does nothing unless pcsx2_enable_hw_hacks is on). The catalog
                // owns the mapping; an explicit pick of the gate key wins over the implied value.
                foreach (var key in coreOptions.Keys.ToList())
                    foreach (var (impliedKey, impliedValue) in ArcadeCheatCatalog.ImpliedOptionsFor(key))
                        if (!coreOptions.ContainsKey(impliedKey))
                            coreOptions[impliedKey] = impliedValue;

                var codes = picked.Where(o => o.Kind == "code" && !string.IsNullOrEmpty(o.Code))
                    .Select(o => o.Code!).ToList();

                if (coreOptions.Count > 0 || codes.Count > 0)
                    descriptor = descriptor with { CoreOptions = coreOptions, CheatCodes = codes };
            }

            return Json(ToJson(descriptor, discCount));
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
        /// played" strip.</summary>
        [HttpGet("/API/Arcade/RecentlyPlayed")]
        public async Task<IActionResult> RecentlyPlayed(int take = 12)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            take = Math.Clamp(take, 1, 30);

            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);
            var recent = await movieDb.ArcadeSaves
                .Where(s => s.UserId == userId.Value)
                .GroupBy(s => s.ArcadeGameId)
                .Select(g => new { ArcadeGameId = g.Key, LastPlayedUtc = g.Max(s => s.UpdatedUtc), SaveCount = g.Count() })
                .OrderByDescending(x => x.LastPlayedUtc)
                .Take(take)
                .ToListAsync();
            if (recent.Count == 0) return Json(Array.Empty<object>());

            // A game can vanish from the lobby (disabled, or the viewer's age restriction tightened)
            // without its save rows going away — silently drop those rather than 500 or show a ghost card.
            var gameIds = recent.Select(r => r.ArcadeGameId).ToList();
            var games = await movieDb.ArcadeGames
                .Where(g => gameIds.Contains(g.Id) && g.IsEnabled && g.RatingCeiling <= ageRestriction)
                .Select(g => new { g.Id, g.Title, g.System, g.BoxArtPath })
                .ToDictionaryAsync(g => g.Id);

            var result = recent
                .Where(r => games.ContainsKey(r.ArcadeGameId))
                .Select(r =>
                {
                    var g = games[r.ArcadeGameId];
                    return new
                    {
                        gameId = g.Id,
                        title = g.Title,
                        system = g.System,
                        artId = g.Id,
                        hasBoxArt = g.BoxArtPath != null,
                        lastPlayedUtc = r.LastPlayedUtc,
                        saveCount = r.SaveCount,
                    };
                });
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
            var joinSystem = ArcadeSaveId.TryParse(boundRoomId, out _, out _, out _, out var jsys, out _) ? jsys : game.System;
            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, launchKey, joinSystem),
                code, boundRoomId, join.PlayerSlot, isCreator: false);

            // The room's codec (patch 0036): a joiner's track mime is fixed at INIT_WEBRTC and must match
            // the room's one encoder, so every joiner echoes the creator's choice.
            var roomCodec = rooms.RoomVideoCodec(code);
            if (roomCodec != "")
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&codec=" + roomCodec };

            return Json(ToJson(descriptor, discCount));
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
            var claimSystem = ArcadeSaveId.TryParse(boundRoomId, out _, out _, out _, out var csys, out _) ? csys : game.System;
            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, launchKey, claimSystem),
                code, boundRoomId, claim.PlayerSlot, isCreator: false);

            // Input-only sessions never attach media, but their peer still negotiates a track — keep its
            // mime consistent with the room's encoder (patch 0036) like every other descriptor.
            var claimCodec = rooms.RoomVideoCodec(code);
            if (claimCodec != "")
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&codec=" + claimCodec };

            return Json(ToJson(descriptor, discCount));
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

        [HttpPost("/API/Arcade/Room/{code}/Heartbeat")]
        public async Task<IActionResult> Heartbeat(string code)
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

            return Json(new
            {
                bound = status.Bound,
                maxPlayers = status.MaxPlayers,
                yourSlot = status.YourSlot,
                players,
                spectators,
                youAreSpectator = status.YouAreSpectator,
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

        private static object ToJson(ArcadeJoinDescriptor d, int discCount = 0) => new
        {
            roomCode = d.RoomCode,
            wsUrl = d.WsUrl,
            playerSlot = d.PlayerSlot,
            // Watch-only seat (playerSlot -1): the shim skips t=108 and never opens its input pump, so this
            // browser holds no controller port. Derived, so the token stays the single source of truth.
            spectator = d.PlayerSlot == ArcadeRoomService.SpectatorSlot,
            gameKey = d.GameKey,
            iceConfig = d.IceConfig.Select(i => new { urls = i.Urls }).ToList(),
            isCreator = d.IsCreator,
            system = d.System,
            discCount,
            // The shim copies these straight into its t=104 GAME_START. Omitted when empty so a room with no
            // cheats sends the same packet it always did.
            coreOptions = d.CoreOptions is { Count: > 0 } ? d.CoreOptions : null,
            cheats = d.CheatCodes is { Count: > 0 } ? d.CheatCodes : null,
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
