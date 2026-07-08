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

        // ONE CARD PER GAME (docs/arcade-dedupe-multidisc-plan.md): rows are grouped by (System, Title) into
        // games, each carrying a version dropdown (region/rev/edition/disc/hack), so the same game's many
        // ROMs collapse to a single card. Filters gate CARDS by version existence — a game shows iff it has
        // ≥1 version matching. Defaults are English + non-modded; region/variant "all" broadens, a specific
        // value narrows. Grouping is query-time so ingests fold in automatically. Age gate always applies.
        [HttpGet("/API/Arcade/Games")]
        public async Task<IActionResult> Games(
            string system = null, string region = null, int? maxPlayers = null,
            string variant = null, string search = null, int page = 1, int pageSize = 60)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            if (!host.IsConfigured)
                return StatusCode(501, new { message = "The arcade is not configured on this server." });

            bool searching = !string.IsNullOrWhiteSpace(search);
            // No explicit choice → default English + non-modded (but a name search spans everything).
            var reg = string.IsNullOrWhiteSpace(region) ? (searching ? "all" : "english") : region.Trim().ToLowerInvariant();
            var var_ = string.IsNullOrWhiteSpace(variant) ? (searching ? "all" : "release") : variant.Trim().ToLowerInvariant();

            var baseQ = await AgeVisibleGamesAsync(userId.Value);

            // The match set: rows that make a game QUALIFY for a card.
            var matchQ = baseQ;
            if (!string.IsNullOrWhiteSpace(system)) matchQ = matchQ.Where(g => g.System == system);
            if (maxPlayers is int mp && mp > 1) matchQ = matchQ.Where(g => g.MaxPlayers >= mp);
            if (searching) { var s = search.Trim(); matchQ = matchQ.Where(g => g.Title.Contains(s)); }

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

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 120);
            var groupedQ = matchQ.GroupBy(g => new { g.System, g.Title })
                .Select(grp => new { grp.Key.System, grp.Key.Title, Sort = grp.Min(x => x.SortTitle) });
            var totalCount = await groupedQ.CountAsync();
            var pageKeys = await groupedQ.OrderBy(x => x.Sort).ThenBy(x => x.Title)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            // All age-visible versions of the paged games (superset by System/Title IN, trimmed to exact
            // page keys in memory) — the dropdown lists every version, not just the ones that matched.
            var pageSystems = pageKeys.Select(k => k.System).Distinct().ToList();
            var pageTitles = pageKeys.Select(k => k.Title).Distinct().ToList();
            var versionRows = await baseQ.Where(g => pageSystems.Contains(g.System) && pageTitles.Contains(g.Title)).ToListAsync();
            var keySet = pageKeys.Select(k => (k.System, k.Title)).ToHashSet();
            var byGame = versionRows.Where(g => keySet.Contains((g.System, g.Title)))
                .GroupBy(g => (g.System, g.Title))
                .ToDictionary(x => x.Key, x => x.ToList());

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
                return new
                {
                    key = k.System + "|" + k.Title,
                    title = k.Title,
                    system = k.System,
                    artId = artRow?.Id ?? rep?.Id ?? 0,
                    hasBoxArt = vs.Any(g => g.BoxArtPath != null),
                    year = rep?.Year,
                    maxPlayers = versions.Count > 0 ? versions.Max(v => v.MaxPlayers) : (byte)1,
                    versionCount = versions.Count,
                    versions = versions.Select(v => new
                    {
                        id = v.Id, label = v.Label, region = v.Region,
                        variant = v.Variant, year = v.Year, maxPlayers = v.MaxPlayers, discCount = v.DiscCount,
                    }).ToList(),
                };
            }).ToList();

            return Json(new { games, totalCount, page, pageSize });
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
            var systems = await q.GroupBy(g => g.System)
                .Select(x => new { value = x.Key, count = x.Count() }).OrderByDescending(x => x.count).ToListAsync();
            var regions = await q.GroupBy(g => g.Region)
                .Select(x => new { value = x.Key ?? "Unknown", count = x.Count() }).OrderByDescending(x => x.count).ToListAsync();
            var variants = await q.GroupBy(g => g.Variant)
                .Select(x => new { value = x.Key ?? "Release", count = x.Count() }).OrderByDescending(x => x.count).ToListAsync();
            return Json(new
            {
                total = await q.CountAsync(),
                multiplayer = await q.CountAsync(g => g.MaxPlayers >= 2),
                systems, regions, variants,
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

            var playerIds = snapshot.SelectMany(r => r.PlayerUserIds).Distinct().ToList();
            var names = await movieDb.Users
                .Where(u => playerIds.Contains(u.UserID))
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
                    seatsFree = Math.Max(0, r.MaxPlayers - r.PlayerUserIds.Count),
                    maxPlayers = r.MaxPlayers,
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

            // Register live state with the creator in seat 0. The CloudRetro room isn't created yet — the
            // creator's browser does that (empty room_id) and then calls Bind (§8 steps 2–3).
            rooms.CreateRoom(roomCode, game.Id, game.MaxPlayers, userId.Value);

            var (launchKey, discCount) = await ResolveLaunchAsync(game);

            // Durable, user-scoped saves (docs/arcade-saves-plan.md): instead of an empty room id ("create
            // a random room"), the creator carries a DETERMINISTIC id encoding (user, game, slot 0, system)
            // with the launch key as the CloudRetro-resolvable suffix. This makes the session's save files
            // predictable, so the gateway seeds this user's save before boot and harvests it after — the
            // save belongs to the user+game, not the room. Slot 0 = the "Continue" slot (multi-slot is S3).
            var saveId = ArcadeSaveId.Mint(userId.Value, game.Id, 0, game.System, launchKey);
            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, launchKey, game.System),
                roomCode, cloudRetroRoomId: saveId, playerSlot: 0, isCreator: true);

            // "New game": tell the gateway (via ?fresh=1 on the WS URL) to clear the mount so the game
            // boots clean instead of resuming the saved slot. Safe unsigned — it only clears the owner's own save.
            if (request.NewGame)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&fresh=1" };
            // Resume-from-snapshot: seed a chosen snapshot slot's bytes into the room (arcade-saves-plan S3).
            else if (request.SeedSlot > 0)
                descriptor = descriptor with { WsUrl = descriptor.WsUrl + "&seedslot=" + request.SeedSlot };

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
            var ext = row.Kind == "sram" ? "srm" : "dat";
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
            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, launchKey, game.System),
                code, boundRoomId, join.PlayerSlot, isCreator: false);

            return Json(ToJson(descriptor, discCount));
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

            var names = await movieDb.Users
                .Where(u => status.PlayerUserIds.Contains(u.UserID))
                .Select(u => new { u.UserID, u.Username })
                .ToDictionaryAsync(u => u.UserID, u => u.Username);

            var players = status.PlayerUserIds
                .Select(id => new { name = names.GetValueOrDefault(id) ?? "Someone", you = id == userId.Value })
                .ToList();

            return Json(new
            {
                bound = status.Bound,
                maxPlayers = status.MaxPlayers,
                yourSlot = status.YourSlot,
                players,
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
            gameKey = d.GameKey,
            iceConfig = d.IceConfig.Select(i => new { urls = i.Urls }).ToList(),
            isCreator = d.IsCreator,
            system = d.System,
            discCount,
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
