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
        private readonly Microsoft.Extensions.Logging.ILogger<ChannelController> logger;

        public ChannelController(MovieDb movieDb, ChannelScheduleService scheduleService, ChannelSkipService skipService,
            Microsoft.Extensions.Logging.ILogger<ChannelController> logger)
        {
            this.movieDb = movieDb;
            this.scheduleService = scheduleService;
            this.skipService = skipService;
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

        [HttpGet("/API/Channel/List")]
        public async Task<IActionResult> List()
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);

            var now = DateTime.UtcNow;
            // Global shelf (category) order — admins arrange it; a category with no row sorts last (a new
            // catalog category just appends until placed). Channels then group by shelf in the guide/rail.
            // Defensive: a missing table just means "no custom order" rather than failing the whole list.
            Dictionary<string, int> shelfOrder;
            try { shelfOrder = await movieDb.ChannelShelves.ToDictionaryAsync(s => s.Category, s => s.SortOrder); }
            catch { shelfOrder = new(); }
            int ShelfRank(string? cat) => cat != null && shelfOrder.TryGetValue(cat, out var so) ? so : int.MaxValue;
            var channels = (await movieDb.Channels.Where(c => c.Enabled).ToListAsync())
                .OrderBy(c => ShelfRank(c.Category))
                .ThenBy(c => c.SortOrder)
                .ThenBy(c => c.Id)
                .ToList();

            // The age gate only matters for an age-restricted viewer; ceilings top out at 7 (Unknown) and an
            // unrestricted viewer defaults to 100, so skip the (cold, expensive) ceiling computation for them.
            // Computing every channel's ceiling inline is what made List stall and return "no channels" right
            // after a restart. For a restricted viewer, bound the cold work like GuideGrid: cached ceilings
            // are free, compute a few cold ones, skip the rest (they appear once the maintainer warms them).
            bool unrestricted = ageRestriction >= 7;
            var visible = new List<object>();
            int coldBudget = MaxColdCeilingsPerRequest;
            foreach (var c in channels)
            {
                if (!ChannelSeason.InSeason(c, now))
                    continue; // seasonal channels are hidden outside their window (lineup stays warm)
                if (!unrestricted)
                {
                    if (!scheduleService.TryGetCachedCeiling(c, out var ceiling))
                    {
                        if (coldBudget <= 0) continue;
                        coldBudget--;
                        ceiling = await scheduleService.GetCeilingAsync(c);
                    }
                    if (ceiling > ageRestriction) continue;
                }
                visible.Add(new { id = c.Id, name = c.Name, description = c.Description, category = c.Category, logoPath = c.LogoPath });
            }

            if (sw.ElapsedMilliseconds > 1000)
                logger.LogWarning("Channel List slow: {Ms}ms ({Visible} of {Total} channels visible)", sw.ElapsedMilliseconds, visible.Count, channels.Count);
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

            var nextItems = items.Skip(currentIndex + 1).Take(5).Select(i =>
            {
                var t = titles.GetValueOrDefault(i.PlayableId);
                return new
                {
                    playableId = i.PlayableId,
                    movieId = t.LinkId,
                    posterId = t.PosterId,
                    kind = t.Kind ?? "movie",
                    posterVersion = t.PosterVersion,
                    title = t.Title ?? "",
                    startsAtUtc = i.StartUtc,
                };
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

            var cur = titles.GetValueOrDefault(current.PlayableId);
            return Json(new
            {
                current = new
                {
                    itemId = current.Id,
                    playableId = current.PlayableId,
                    movieId = cur.LinkId,
                    posterId = cur.PosterId,
                    kind = cur.Kind ?? "movie",
                    posterVersion = cur.PosterVersion,
                    title = cur.Title ?? "",
                    plot = cur.Plot,
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

        public class SeekRequest
        {
            public long ItemId { get; set; }
            public double OffsetSeconds { get; set; }
        }

        [HttpPost("/API/Channel/{id}/Seek")]
        public async Task<IActionResult> Seek(int id, [FromBody] SeekRequest request)
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
                return Json(new { seeked = false });

            // Scrubbing moves the whole shared timeline continuously, so unlike skip/restart it can't be a
            // vote — it's only offered to a lone viewer. Touch first so our own presence counts, then refuse
            // if anyone else is watching, or if the channel is frozen (resume before seeking).
            skipService.Touch(id, current.Id, userId.Value);
            if (skipService.ViewerIds(id).Count > 1 || skipService.PausedSince(id) != null)
                return Json(new { seeked = false });

            // The seek must be about the item the client is actually watching; a stale itemId just no-ops.
            if (request == null || (request.ItemId != 0 && request.ItemId != current.Id))
                return Json(new { seeked = false });

            bool seeked = await scheduleService.SeekCurrentAsync(channel, current.Id, request.OffsetSeconds);
            return Json(new { seeked });
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

            var guide = windowed.Select(i =>
            {
                var t = titles.GetValueOrDefault(i.PlayableId);
                return new
                {
                    playableId = i.PlayableId,
                    movieId = t.LinkId,
                    posterId = t.PosterId,
                    kind = t.Kind ?? "movie",
                    posterVersion = t.PosterVersion,
                    title = t.Title ?? "",
                    startUtc = i.StartUtc,
                    endUtc = i.EndUtc,
                };
            });

            return Json(guide);
        }

        // How many cold (uncached-ceiling) channels a single GuideGrid request will compute inline before
        // deferring the rest to the background maintainer. Keeps the request bounded no matter the channel
        // count — a cold channel just fills in on a later refresh once the maintainer has warmed it.
        private const int MaxColdCeilingsPerRequest = 8;

        /// <summary>
        /// Cross-channel "what's on everywhere" for the grid guide (the EPG): every visible channel keyed
        /// by id, with its lineup across a window from now. Designed to scale to many channels — it's a
        /// bounded read: ceilings come from cache (the background maintainer keeps them warm), the lineup
        /// is one bulk query, and it never extends/prunes/touches presence. The client owns row order and
        /// channel numbers (from <see cref="List"/>); this just supplies lineups to join by id.
        /// </summary>
        [HttpGet("/API/Channel/GuideGrid")]
        public async Task<IActionResult> GuideGrid(int hours = 6)
        {
            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ageRestriction = await GetAgeRestrictionAsync(userId.Value);
            hours = Math.Clamp(hours, 1, 24);
            var now = DateTime.UtcNow;
            var until = now.AddHours(hours);

            var channels = (await movieDb.Channels
                .Where(c => c.Enabled)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Id)
                .ToListAsync())
                .Where(c => ChannelSeason.InSeason(c, now))
                .ToList();

            // Unrestricted viewers (the common case) see every channel — skip the ceiling gating entirely so
            // scrolling never reveals a "missing" channel that was merely deferred as cold. For a restricted
            // viewer, bound the cold work: cached ceilings are free, compute at most a few cold ones inline,
            // skip the rest (they surface on the next 60s refresh once the maintainer has warmed them).
            bool unrestricted = ageRestriction >= 7;
            var visibleIds = new List<int>();
            int coldBudget = MaxColdCeilingsPerRequest;
            foreach (var c in channels)
            {
                if (unrestricted) { visibleIds.Add(c.Id); continue; }
                if (!scheduleService.TryGetCachedCeiling(c, out var ceiling))
                {
                    if (coldBudget <= 0)
                        continue;
                    coldBudget--;
                    ceiling = await scheduleService.GetCeilingAsync(c);
                }
                if (ceiling <= ageRestriction)
                    visibleIds.Add(c.Id);
            }

            // One bulk query for the whole window across all visible channels, then one for titles.
            var windowed = await scheduleService.WindowedItemsAsync(visibleIds, now, until);
            var titles = await TitlesForAsync(windowed.Values.SelectMany(list => list.Select(i => i.PlayableId)));

            var result = visibleIds.Select(id =>
            {
                var items = windowed.GetValueOrDefault(id) ?? new List<ChannelScheduleItem>();
                return new
                {
                    id,
                    // paused/viewers are in-memory (the skip singleton) — cheap, no DB.
                    paused = skipService.PausedSince(id) != null,
                    viewers = skipService.ViewerIds(id).Count,
                    items = items.Select(i =>
                    {
                        var t = titles.GetValueOrDefault(i.PlayableId);
                        return new
                        {
                            playableId = i.PlayableId,
                            movieId = t.LinkId,
                            posterId = t.PosterId,
                            kind = t.Kind ?? "movie",
                            posterVersion = t.PosterVersion,
                            title = t.Title ?? "",
                            plot = t.Plot,
                            // Pin Kind=Utc so these serialize with a trailing 'Z' and the client parses them
                            // in the same frame as serverNowUtc — EF hands back Unspecified, which would
                            // otherwise be read as browser-local and slide blocks off the "now" line.
                            startUtc = DateTime.SpecifyKind(i.StartUtc, DateTimeKind.Utc),
                            endUtc = DateTime.SpecifyKind(i.EndUtc, DateTimeKind.Utc),
                        };
                    }),
                };
            });

            if (sw.ElapsedMilliseconds > 1500)
                logger.LogWarning("Channel GuideGrid slow: {Ms}ms ({Visible} of {Total} channels, {Cold} cold ceilings computed)",
                    sw.ElapsedMilliseconds, visibleIds.Count, channels.Count, MaxColdCeilingsPerRequest - coldBudget);
            // serverNowUtc lets the client align the "now" line to the server clock, not the browser's.
            return Json(new { serverNowUtc = now, hours, items = result });
        }

        // Poster + link info for one schedule item. PosterId + Kind ("movie"|"series"|"misc") pick the
        // right /Image route; PosterVersion cache-busts; LinkId is the watch-link id (0 = no link). A
        // value struct so a missing dictionary entry defaults cleanly (Kind/Title null → handled at use).
        private readonly struct TitleInfo
        {
            public TitleInfo(int posterId, string kind, int posterVersion, int linkId, string title, string? plot)
            { PosterId = posterId; Kind = kind; PosterVersion = posterVersion; LinkId = linkId; Title = title; Plot = plot; }
            public int PosterId { get; }
            public string Kind { get; }
            public int PosterVersion { get; }
            public int LinkId { get; }
            public string Title { get; }
            public string? Plot { get; }
        }

        // A one-liner for the guide blocks — trim a plot to a bounded length so the cross-channel payload
        // stays small (the block clamps it to a couple of lines anyway).
        private static string? ShortPlot(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : (s.Length > 160 ? s.Substring(0, 160).TrimEnd() + "…" : s);

        // Resolve schedule-item PlayableIds to poster/link info for the lineup readout. Channels air
        // movies, episodes, and misc — each needs the right poster route. An episode shows its SERIES
        // poster (Kind="series", PosterId=SeriesId). PosterVersion is a column join (no N+1), same as
        // APIController.ToCardDto. LinkId stays the legacy movie/series id the watch link uses.
        private async Task<Dictionary<int, TitleInfo>> TitlesForAsync(IEnumerable<int> playableIds)
        {
            var ids = playableIds.Distinct().ToList();
            var map = new Dictionary<int, TitleInfo>();

            var movies = await movieDb.Movies
                .Where(m => m.PlayableId != null && ids.Contains(m.PlayableId.Value))
                .Select(m => new { Pid = m.PlayableId!.Value, m.id, m.Title, m.Plot, Ver = m.PosterDetails != null ? m.PosterDetails.PosterVersion : 0 })
                .ToListAsync();
            foreach (var m in movies) map[m.Pid] = new TitleInfo(m.id, "movie", m.Ver, m.id, m.Title ?? "", ShortPlot(m.Plot));

            var eps = await movieDb.Episodes
                .Where(e => e.PlayableId != null && ids.Contains(e.PlayableId.Value))
                .Select(e => new { Pid = e.PlayableId!.Value, e.SeriesId, SeriesTitle = e.Series!.Title, SeriesPlot = e.Series!.Plot, e.SeasonNumber, e.EpisodeNumber, e.Title, Ver = e.Series!.PosterDetails != null ? e.Series.PosterDetails.PosterVersion : 0 })
                .ToListAsync();
            foreach (var e in eps)
            {
                var code = $"S{e.SeasonNumber:00}E{e.EpisodeNumber:00}";
                var title = string.IsNullOrWhiteSpace(e.Title)
                    ? $"{e.SeriesTitle} – {code}"
                    : $"{e.SeriesTitle} – {code} {e.Title}";
                int sid = e.SeriesId ?? 0;
                map[e.Pid] = new TitleInfo(sid, "series", e.Ver, sid, title, ShortPlot(e.SeriesPlot));
            }

            var misc = await movieDb.MiscVideos
                .Where(mv => ids.Contains(mv.PlayableId))
                .Select(mv => new { mv.PlayableId, mv.Id, mv.Title, mv.RelatedMovieId, mv.RelatedSeriesId })
                .ToListAsync();
            foreach (var mv in misc)
            {
                if (mv.RelatedMovieId is int rm) map[mv.PlayableId] = new TitleInfo(rm, "movie", 0, rm, mv.Title ?? "", null);
                else if (mv.RelatedSeriesId is int rs) map[mv.PlayableId] = new TitleInfo(rs, "series", 0, rs, mv.Title ?? "", null);
                else map[mv.PlayableId] = new TitleInfo(mv.Id, "misc", 0, 0, mv.Title ?? "", null);
            }

            return map;
        }
    }
}
