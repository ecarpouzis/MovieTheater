using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Channels;
using MovieTheater.Db;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Watch-party lobby (docs/playlists-watchparty-plan.md). A watch party is a private playlist channel
    /// (see ChannelController's playlist endpoints) reached only by its <see cref="Db.Channel.WatchpartyToken"/>.
    /// Its shared timeline is frozen (anchor far in the future) until the lobby presses Begin, at which point
    /// it re-anchors to "now" and everyone watches in sync through the ordinary Channel player. This controller
    /// owns only the lobby: presence, ready-state, and the collective Begin. Like the other streaming planes it
    /// requires a password-verified session, and a party above the viewer's age ceiling is refused.
    /// </summary>
    [Authorize(Policy = "StreamingUser")]
    public class WatchpartyController : Controller
    {
        private readonly MovieDb movieDb;
        private readonly ChannelScheduleService scheduleService;
        private readonly WatchpartyService parties;
        private readonly ILogger<WatchpartyController> logger;

        public WatchpartyController(MovieDb movieDb, ChannelScheduleService scheduleService,
            WatchpartyService parties, ILogger<WatchpartyController> logger)
        {
            this.movieDb = movieDb;
            this.scheduleService = scheduleService;
            this.parties = parties;
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

        private Task<Channel?> ResolveAsync(string token) =>
            movieDb.Channels.FirstOrDefaultAsync(c => c.WatchpartyToken == token)!;

        // Build the lobby payload: roster (you first, then by name) + started flag + host flag + item count.
        private async Task<object> LobbyJsonAsync(Channel channel, int userId)
        {
            var roster = parties.Roster(channel.Id);
            var names = await movieDb.Users
                .Where(u => roster.Select(m => m.UserId).Contains(u.UserID))
                .Select(u => new { u.UserID, u.Username })
                .ToDictionaryAsync(u => u.UserID, u => u.Username);
            var members = roster
                .OrderByDescending(m => m.UserId == userId)
                .ThenBy(m => names.GetValueOrDefault(m.UserId) ?? "")
                .Select(m => new { name = names.GetValueOrDefault(m.UserId) ?? "Someone", you = m.UserId == userId, ready = m.Ready })
                .ToList();
            int count = await movieDb.PlaylistItems.CountAsync(p => p.ChannelId == channel.Id);
            return new
            {
                token = channel.WatchpartyToken,
                channelId = channel.Id,
                name = channel.Name,
                itemCount = count,
                started = channel.WatchpartyStartedUtc != null,
                amHost = channel.OwnerUserId == userId,
                roster = members,
            };
        }

        // The collective start: idempotent, and only fires when every present member is ready, or when the
        // host explicitly forces it. Re-anchors the frozen timeline to "now" and materializes the lineup, so
        // from here everyone watching /API/Channel/{channelId}/Now sees the same movie at the same offset.
        private async Task<bool> TryBeginAsync(Channel channel, int userId, bool hostForce)
        {
            if (channel.WatchpartyStartedUtc != null)
                return true;
            bool isHost = channel.OwnerUserId == userId;
            if (!(parties.AllPresentReady(channel.Id) || (isHost && hostForce)))
                return false;

            var now = DateTime.UtcNow;
            channel.WatchpartyStartedUtc = now;
            channel.AnchorUtc = now; // unfreeze: the schedule now generates from this instant
            await movieDb.SaveChangesAsync();
            scheduleService.InvalidateEligible(channel);
            await scheduleService.EnsureScheduleAsync(channel, now.AddHours(48));
            logger.LogInformation("Watch party {ChannelId} began ({User} started it).", channel.Id, userId);
            return true;
        }

        // Age-gate a party the same way channels are gated: a shared timeline can't censor per-viewer, so a
        // party whose ceiling exceeds the viewer's restriction is simply off-limits to them.
        private async Task<bool> AboveAgeAsync(Channel channel, int userId) =>
            await scheduleService.GetCeilingAsync(channel) > await GetAgeRestrictionAsync(userId);

        [HttpGet("/API/Watchparty/{token}")]
        public async Task<IActionResult> Resolve(string token)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var channel = await ResolveAsync(token);
            if (channel == null)
                return NotFound(new { message = "This watch party doesn't exist (or has ended)." });
            if (await AboveAgeAsync(channel, userId.Value))
                return StatusCode(403, new { message = "This watch party isn't available on your account." });

            parties.Touch(channel.Id, userId.Value);
            return Json(await LobbyJsonAsync(channel, userId.Value));
        }

        [HttpPost("/API/Watchparty/{token}/Heartbeat")]
        public async Task<IActionResult> Heartbeat(string token)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            var channel = await ResolveAsync(token);
            if (channel == null)
                return NotFound(new { message = "This watch party doesn't exist (or has ended)." });
            if (await AboveAgeAsync(channel, userId.Value))
                return StatusCode(403, new { message = "This watch party isn't available on your account." });

            parties.Touch(channel.Id, userId.Value);
            return Json(await LobbyJsonAsync(channel, userId.Value));
        }

        public class ReadyRequest
        {
            public bool Ready { get; set; }
        }

        [HttpPost("/API/Watchparty/{token}/Ready")]
        public async Task<IActionResult> Ready(string token, [FromBody] ReadyRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            var channel = await ResolveAsync(token);
            if (channel == null)
                return NotFound(new { message = "This watch party doesn't exist (or has ended)." });
            if (await AboveAgeAsync(channel, userId.Value))
                return StatusCode(403, new { message = "This watch party isn't available on your account." });

            parties.SetReady(channel.Id, userId.Value, request?.Ready ?? true);
            // Everyone ready ⇒ it begins for all — the core "when they're both ready, it starts" behavior.
            await TryBeginAsync(channel, userId.Value, hostForce: false);
            return Json(await LobbyJsonAsync(channel, userId.Value));
        }

        [HttpPost("/API/Watchparty/{token}/Begin")]
        public async Task<IActionResult> Begin(string token)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            var channel = await ResolveAsync(token);
            if (channel == null)
                return NotFound(new { message = "This watch party doesn't exist (or has ended)." });
            if (await AboveAgeAsync(channel, userId.Value))
                return StatusCode(403, new { message = "This watch party isn't available on your account." });

            parties.Touch(channel.Id, userId.Value);
            // The host may start the party without waiting; anyone may start it once everyone is ready.
            await TryBeginAsync(channel, userId.Value, hostForce: true);
            return Json(await LobbyJsonAsync(channel, userId.Value));
        }

        [HttpPost("/API/Watchparty/{token}/Leave")]
        public async Task<IActionResult> Leave(string token)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();
            var channel = await ResolveAsync(token);
            if (channel == null)
                return Json(new { ok = true });

            bool emptied = parties.Leave(channel.Id, userId.Value);
            // An unstarted party that everyone has left is abandoned — delete it now (cascades its items),
            // so a created-then-closed party doesn't linger. A started party is left for the reaper.
            if (emptied && channel.WatchpartyStartedUtc == null)
            {
                movieDb.Channels.Remove(channel);
                await movieDb.SaveChangesAsync();
            }
            return Json(new { ok = true });
        }
    }
}
