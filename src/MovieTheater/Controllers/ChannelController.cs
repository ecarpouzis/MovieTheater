using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Channels;
using MovieTheater.Db;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// TV channel surface (streaming-plan.md §8). Like the stream control plane it requires
    /// a password-verified session; channels are additionally hidden when their rating
    /// ceiling exceeds the viewer's age restriction (a shared timeline can't censor
    /// per-viewer, so the gate is per-channel).
    /// </summary>
    [Authorize(Policy = "StreamingUser")]
    public class ChannelController : Controller
    {
        private const long TicksPerSecond = 10_000_000;

        private readonly MovieDb movieDb;
        private readonly ChannelScheduleService scheduleService;
        private readonly ChannelSkipService skipService;

        public ChannelController(MovieDb movieDb, ChannelScheduleService scheduleService, ChannelSkipService skipService)
        {
            this.movieDb = movieDb;
            this.scheduleService = scheduleService;
            this.skipService = skipService;
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

        [HttpGet("/API/Channel/List")]
        public async Task<IActionResult> List()
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);

            var channels = await movieDb.Channels
                .Where(c => c.Enabled)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Id)
                .ToListAsync();

            var visible = new List<object>();
            foreach (var c in channels)
            {
                var ceiling = await scheduleService.GetCeilingAsync(c);
                if (ceiling <= ageRestriction)
                    visible.Add(new { id = c.Id, name = c.Name, description = c.Description });
            }

            return Json(visible);
        }

        [HttpGet("/API/Channel/{id}/Now")]
        public async Task<IActionResult> Now(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var channel = await movieDb.Channels.FirstOrDefaultAsync(c => c.Id == id && c.Enabled);
            if (channel == null)
                return NotFound(new { message = "Channel not found." });

            if (await scheduleService.GetCeilingAsync(channel) > await GetAgeRestrictionAsync(userId.Value))
                return StatusCode(403, new { message = "This channel isn't available on your account." });

            var now = DateTime.UtcNow;
            var items = await scheduleService.EnsureScheduleAsync(channel, now.Add(TimeSpan.FromHours(48)));

            // While the channel is paused the timeline is frozen, so read "what's on" against the
            // pause instant rather than the moving wall clock — the offset (and everything derived
            // from it) holds steady until someone resumes.
            var pausedAt = skipService.PausedSince(id);
            var clock = pausedAt ?? now;

            var currentIndex = items.FindIndex(i => i.StartUtc <= clock && clock < i.EndUtc);
            if (currentIndex < 0)
                return Json(new { current = (object?)null, next = Array.Empty<object>(), paused = false });

            var current = items[currentIndex];
            var titles = await TitlesForAsync(items.Skip(currentIndex).Take(6).Select(i => i.PlayableId));

            var nextItems = items.Skip(currentIndex + 1).Take(5).Select(i => new
            {
                movieId = titles.GetValueOrDefault(i.PlayableId).MovieId,
                title = titles.GetValueOrDefault(i.PlayableId).Title ?? "",
                startsAtUtc = i.StartUtc,
            });

            // Polling Now is also the presence heartbeat for the skip/restart tallies (§8).
            var status = skipService.Touch(id, current.Id, userId.Value);

            // Put names to the live presence so the viewer count can reveal who's connected. Yourself
            // sorts first and is flagged, the rest alphabetically.
            var viewerIds = skipService.ViewerIds(id);
            var viewerNames = await movieDb.Users
                .Where(u => viewerIds.Contains(u.UserID))
                .Select(u => new { u.UserID, u.Username })
                .ToListAsync();
            var viewers = viewerNames
                .OrderByDescending(u => u.UserID == userId.Value)
                .ThenBy(u => u.Username)
                .Select(u => new { name = u.Username ?? "Someone", you = u.UserID == userId.Value })
                .ToList();

            return Json(new
            {
                current = new
                {
                    itemId = current.Id,
                    movieId = titles.GetValueOrDefault(current.PlayableId).MovieId,
                    title = titles.GetValueOrDefault(current.PlayableId).Title ?? "",
                    offsetSeconds = Math.Max(0, (clock - current.StartUtc).TotalSeconds),
                    durationSeconds = (current.EndUtc - current.StartUtc).TotalSeconds,
                    endsAtUtc = current.EndUtc,
                },
                next = nextItems,
                paused = pausedAt != null,
                viewers = new { count = viewers.Count, names = viewers },
                skip = new { viewers = status.Skip.Viewers, votes = status.Skip.Votes, required = status.Skip.Required, youVoted = status.Skip.YouVoted },
                restart = new { viewers = status.Restart.Viewers, votes = status.Restart.Votes, required = status.Restart.Required, youVoted = status.Restart.YouVoted },
            });
        }

        public class SkipRequest
        {
            public long ItemId { get; set; }
        }

        [HttpPost("/API/Channel/{id}/Skip")]
        public async Task<IActionResult> Skip(int id, [FromBody] SkipRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var channel = await movieDb.Channels.FirstOrDefaultAsync(c => c.Id == id && c.Enabled);
            if (channel == null)
                return NotFound(new { message = "Channel not found." });

            if (await scheduleService.GetCeilingAsync(channel) > await GetAgeRestrictionAsync(userId.Value))
                return StatusCode(403, new { message = "This channel isn't available on your account." });

            var now = DateTime.UtcNow;
            var items = await scheduleService.EnsureScheduleAsync(channel, now.Add(TimeSpan.FromHours(48)));
            var current = items.FirstOrDefault(i => i.StartUtc <= now && now < i.EndUtc);
            if (current == null)
                return Json(new { skipped = false, skip = (object?)null });

            // The vote must be about the item the client is actually watching; a stale itemId
            // (the channel moved on under them) just counts as presence on the real current item.
            if (request != null && request.ItemId != 0 && request.ItemId != current.Id)
            {
                var stale = skipService.Touch(id, current.Id, userId.Value);
                return Json(new { skipped = false, skip = new { stale.Skip.Viewers, stale.Skip.Votes, stale.Skip.Required, stale.Skip.YouVoted } });
            }

            var (carried, status) = skipService.VoteSkip(id, current.Id, userId.Value);
            bool skipped = carried && await scheduleService.SkipCurrentAsync(channel, current.Id);

            return Json(new
            {
                skipped,
                skip = new { status.Skip.Viewers, status.Skip.Votes, status.Skip.Required, status.Skip.YouVoted },
            });
        }

        [HttpPost("/API/Channel/{id}/Restart")]
        public async Task<IActionResult> Restart(int id, [FromBody] SkipRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var channel = await movieDb.Channels.FirstOrDefaultAsync(c => c.Id == id && c.Enabled);
            if (channel == null)
                return NotFound(new { message = "Channel not found." });

            if (await scheduleService.GetCeilingAsync(channel) > await GetAgeRestrictionAsync(userId.Value))
                return StatusCode(403, new { message = "This channel isn't available on your account." });

            var now = DateTime.UtcNow;
            var items = await scheduleService.EnsureScheduleAsync(channel, now.Add(TimeSpan.FromHours(48)));
            var current = items.FirstOrDefault(i => i.StartUtc <= now && now < i.EndUtc);
            if (current == null)
                return Json(new { restarted = false, restart = (object?)null });

            // As with Skip, a stale itemId just counts as presence on the real current item.
            if (request != null && request.ItemId != 0 && request.ItemId != current.Id)
            {
                var stale = skipService.Touch(id, current.Id, userId.Value);
                return Json(new { restarted = false, restart = new { stale.Restart.Viewers, stale.Restart.Votes, stale.Restart.Required, stale.Restart.YouVoted } });
            }

            var (carried, status) = skipService.VoteRestart(id, current.Id, userId.Value);
            bool restarted = carried && await scheduleService.RestartCurrentAsync(channel, current.Id);
            // The item id is unchanged by a restart, so clear the poll explicitly to allow another.
            if (restarted)
                skipService.ClearRestart(id, current.Id);

            return Json(new
            {
                restarted,
                restart = new { status.Restart.Viewers, status.Restart.Votes, status.Restart.Required, status.Restart.YouVoted },
            });
        }

        [HttpPost("/API/Channel/{id}/PlayPause")]
        public async Task<IActionResult> PlayPause(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var channel = await movieDb.Channels.FirstOrDefaultAsync(c => c.Id == id && c.Enabled);
            if (channel == null)
                return NotFound(new { message = "Channel not found." });

            if (await scheduleService.GetCeilingAsync(channel) > await GetAgeRestrictionAsync(userId.Value))
                return StatusCode(403, new { message = "This channel isn't available on your account." });

            var now = DateTime.UtcNow;
            var items = await scheduleService.EnsureScheduleAsync(channel, now.Add(TimeSpan.FromHours(48)));

            // Find the item the viewer is actually looking at — frozen-clock aware, so resuming
            // targets the same item that was airing when the channel was paused.
            var clock = skipService.PausedSince(id) ?? now;
            var current = items.FirstOrDefault(i => i.StartUtc <= clock && clock < i.EndUtc);
            if (current == null)
                return Json(new { paused = false });

            // Anyone watching may flip the shared pause — no vote. On resume, slide the schedule
            // forward by however long it was frozen so playback continues from where it stopped.
            var (pausedNow, wasPausedFor) = skipService.TogglePause(id, current.Id, userId.Value);
            if (pausedNow == null)
                await scheduleService.ShiftForResumeAsync(channel, wasPausedFor);

            return Json(new { paused = pausedNow != null });
        }

        [HttpGet("/API/Channel/{id}/Guide")]
        public async Task<IActionResult> Guide(int id, int hours = 12)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var channel = await movieDb.Channels.FirstOrDefaultAsync(c => c.Id == id && c.Enabled);
            if (channel == null)
                return NotFound(new { message = "Channel not found." });

            if (await scheduleService.GetCeilingAsync(channel) > await GetAgeRestrictionAsync(userId.Value))
                return StatusCode(403, new { message = "This channel isn't available on your account." });

            hours = Math.Clamp(hours, 1, 48);
            var now = DateTime.UtcNow;
            var until = now.AddHours(hours);
            var items = await scheduleService.EnsureScheduleAsync(channel, until);

            // From the currently-airing item forward through the window.
            var windowed = items.Where(i => i.EndUtc > now && i.StartUtc < until).ToList();
            var titles = await TitlesForAsync(windowed.Select(i => i.PlayableId));

            var guide = windowed.Select(i => new
            {
                movieId = titles.GetValueOrDefault(i.PlayableId).MovieId,
                title = titles.GetValueOrDefault(i.PlayableId).Title ?? "",
                startUtc = i.StartUtc,
                endUtc = i.EndUtc,
            });

            return Json(guide);
        }

        // Resolve a set of schedule-item PlayableIds to their movie (id + title) for the lineup readout.
        // Channels currently air movies; episode playables would resolve here too once scheduled.
        private async Task<Dictionary<int, (int MovieId, string Title)>> TitlesForAsync(IEnumerable<int> playableIds)
        {
            var ids = playableIds.Distinct().ToList();
            var rows = await movieDb.Movies
                .Where(m => m.PlayableId != null && ids.Contains(m.PlayableId.Value))
                .Select(m => new { PlayableId = m.PlayableId!.Value, m.id, m.Title })
                .ToListAsync();
            return rows.ToDictionary(r => r.PlayableId, r => (r.id, r.Title ?? ""));
        }
    }
}
