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

            var currentIndex = items.FindIndex(i => i.StartUtc <= now && now < i.EndUtc);
            if (currentIndex < 0)
                return Json(new { current = (object?)null, next = Array.Empty<object>() });

            var current = items[currentIndex];
            var titles = await TitlesForAsync(items.Skip(currentIndex).Take(6).Select(i => i.MovieID));

            var nextItems = items.Skip(currentIndex + 1).Take(5).Select(i => new
            {
                movieId = i.MovieID,
                title = titles.GetValueOrDefault(i.MovieID, ""),
                startsAtUtc = i.StartUtc,
            });

            // Polling Now is also the presence heartbeat for the skip-vote tally (§8).
            var skip = skipService.Touch(id, current.Id, userId.Value);

            return Json(new
            {
                current = new
                {
                    itemId = current.Id,
                    movieId = current.MovieID,
                    title = titles.GetValueOrDefault(current.MovieID, ""),
                    offsetSeconds = Math.Max(0, (now - current.StartUtc).TotalSeconds),
                    durationSeconds = (current.EndUtc - current.StartUtc).TotalSeconds,
                    endsAtUtc = current.EndUtc,
                },
                next = nextItems,
                skip = new { viewers = skip.Viewers, votes = skip.Votes, required = skip.Required, youVoted = skip.YouVoted },
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
                return Json(new { skipped = false, skip = new { stale.Viewers, stale.Votes, stale.Required, stale.YouVoted } });
            }

            var (carried, status) = skipService.Vote(id, current.Id, userId.Value);
            bool skipped = carried && await scheduleService.SkipCurrentAsync(channel, current.Id);

            return Json(new
            {
                skipped,
                skip = new { status.Viewers, status.Votes, status.Required, status.YouVoted },
            });
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
            var titles = await TitlesForAsync(windowed.Select(i => i.MovieID));

            var guide = windowed.Select(i => new
            {
                movieId = i.MovieID,
                title = titles.GetValueOrDefault(i.MovieID, ""),
                startUtc = i.StartUtc,
                endUtc = i.EndUtc,
            });

            return Json(guide);
        }

        private async Task<Dictionary<int, string>> TitlesForAsync(IEnumerable<int> movieIds)
        {
            var ids = movieIds.Distinct().ToList();
            return await movieDb.Movies
                .Where(m => ids.Contains(m.id))
                .Select(m => new { m.id, m.Title })
                .ToDictionaryAsync(m => m.id, m => m.Title ?? "");
        }
    }
}
