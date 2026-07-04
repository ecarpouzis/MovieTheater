using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Arcade;
using MovieTheater.Db;
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

        public ArcadeController(MovieDb movieDb, IArcadeHost host, ArcadeRoomService rooms, ILogger<ArcadeController> logger)
        {
            this.movieDb = movieDb;
            this.host = host;
            this.rooms = rooms;
            this.logger = logger;
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
                // Best version first = the card's default selection + box-art source. When a specific region
                // is filtered, prefer that region so the card opens on what the user asked for.
                var versions = vs
                    .OrderBy(v => specificRegion != null && !string.Equals(v.Region, specificRegion, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(ArcadeVersions.Rank).ToList();
                var rep = versions.FirstOrDefault();
                return new
                {
                    key = k.System + "|" + k.Title,
                    title = k.Title,
                    system = k.System,
                    artId = rep?.Id ?? 0,
                    hasBoxArt = rep?.BoxArtPath != null,
                    year = rep?.Year,
                    maxPlayers = versions.Count > 0 ? versions.Max(v => v.MaxPlayers) : (byte)1,
                    versionCount = versions.Count,
                    versions = versions.Select(v => new
                    {
                        id = v.Id, label = ArcadeVersions.Label(v), region = v.Region,
                        variant = v.Variant, year = v.Year, maxPlayers = v.MaxPlayers,
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

            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, game.CloudRetroGameKey, game.System),
                roomCode, cloudRetroRoomId: string.Empty, playerSlot: 0, isCreator: true);

            return Json(ToJson(descriptor));
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
            var descriptor = host.BuildJoinDescriptor(
                userId.Value, new ArcadeGameDescriptor(game.Id, game.CloudRetroGameKey, game.System),
                code, boundRoomId, join.PlayerSlot, isCreator: false);

            return Json(ToJson(descriptor));
        }

        [HttpPost("/API/Arcade/Room/{code}/Heartbeat")]
        public async Task<IActionResult> Heartbeat(string code)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var status = rooms.Heartbeat(code, userId.Value);
            if (status == null)
                return NotFound(new { message = "Room not found." });

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

        private static object ToJson(ArcadeJoinDescriptor d) => new
        {
            roomCode = d.RoomCode,
            wsUrl = d.WsUrl,
            playerSlot = d.PlayerSlot,
            gameKey = d.GameKey,
            iceConfig = d.IceConfig.Select(i => new { urls = i.Urls }).ToList(),
            isCreator = d.IsCreator,
            system = d.System,
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
