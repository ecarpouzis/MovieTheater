using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;

namespace MovieTheater.Channels
{
    /// <summary>
    /// Builds and extends the materialized TV schedule (streaming-plan.md §8). The lineup
    /// is rows in <see cref="ChannelScheduleItem"/>, generated ahead lazily and never
    /// rewritten — so every viewer of a channel sees the same movie at the same offset,
    /// and the order stays stable when the library changes.
    /// </summary>
    public class ChannelScheduleService
    {
        private const long TicksPerSecond = 10_000_000;
        private static readonly TimeSpan ScheduleHorizon = TimeSpan.FromHours(48);
        private static readonly TimeSpan PruneAge = TimeSpan.FromDays(3);

        // The rating ceiling is a full eligible-set scan, so it's cached: the answer only moves when
        // the filter or the library changes, but the age gate (List, Now, GuideGrid) needs it on every
        // call. Keyed by channel id + filter so an admin filter edit busts it; a short TTL absorbs
        // library growth. This is what lets the guide stay cheap with many channels.
        private static readonly TimeSpan CeilingTtl = TimeSpan.FromMinutes(15);

        // Per-channel generation gates (see EnsureScheduleAsync). Static so all scoped instances share them.
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> generationGates = new();

        private readonly MovieDb movieDb;
        private readonly IMemoryCache cache;
        private readonly ILogger<ChannelScheduleService> logger;

        public ChannelScheduleService(MovieDb movieDb, IMemoryCache cache, ILogger<ChannelScheduleService> logger)
        {
            this.movieDb = movieDb;
            this.cache = cache;
            this.logger = logger;
        }

        private static string CeilingKey(Channel channel) =>
            $"channel-ceiling:{channel.Id}:{(channel.FilterJson ?? string.Empty).GetHashCode()}";

        public record EligibleMovie(int MovieId, int PlayableId, long DurationTicks, int RatingId);

        /// <summary>
        /// The channel's current eligible set plus its effective rating ceiling (the max
        /// rating id present, unless the filter caps it lower). Eligible movies have a
        /// playable file and a known duration.
        /// </summary>
        public async Task<(List<EligibleMovie> Movies, int Ceiling)> BuildEligibleAsync(Channel channel, CancellationToken cancel = default)
        {
            var filter = ChannelFilter.Parse(channel.FilterJson);

            var query = movieDb.Movies
                .Where(m => m.Playable != null && m.Playable.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null));

            if (filter.ExcludeRemoveFromRandom)
                query = query.Where(m => !m.RemoveFromRandom);

            if (filter.YearMin is int yMin)
                query = query.Where(m => m.ReleaseDate != null && m.ReleaseDate.Value.Year >= yMin);
            if (filter.YearMax is int yMax)
                query = query.Where(m => m.ReleaseDate != null && m.ReleaseDate.Value.Year <= yMax);

            if (filter.GenreIds.Count > 0)
            {
                if (string.Equals(filter.GenreMode, "all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var gid in filter.GenreIds)
                    {
                        var g = gid;
                        query = query.Where(m => m.MovieGenres.Any(mg => mg.GenreId == g));
                    }
                }
                else
                {
                    var ids = filter.GenreIds;
                    query = query.Where(m => m.MovieGenres.Any(mg => ids.Contains(mg.GenreId)));
                }
            }

            if (filter.UnwatchedByUserId is int uid)
                query = query.Where(m => !movieDb.Viewings.Any(v => v.UserID == uid && v.MovieID == m.id && v.ViewingType == "Seen"));

            var rows = await query
                .Select(m => new
                {
                    m.id,
                    m.PlayableId,
                    m.Rating,
                    m.RuntimeMinutes,
                    DurationTicks = m.Playable!.Files
                        .Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null)
                        .Select(f => f.DurationTicks)
                        .FirstOrDefault(),
                })
                .ToListAsync(cancel);

            // RatingMap is tiny; resolve text→id in memory.
            var ratingMap = await movieDb.RatingMaps
                .Where(r => r.MovieRating != null)
                .ToDictionaryAsync(r => r.MovieRating!, r => r.MPARatingID, cancel);

            var eligible = new List<EligibleMovie>(rows.Count);
            int ceiling = 0;
            foreach (var row in rows)
            {
                long durationTicks = row.DurationTicks
                    ?? (row.RuntimeMinutes is int min && min > 0 ? (long)min * 60 * TicksPerSecond : 0);
                if (durationTicks <= 0)
                    continue; // §8: skip movies with neither a file duration nor a runtime

                int ratingId = row.Rating != null && ratingMap.TryGetValue(row.Rating.Trim(), out var id) ? id : 0;

                if (filter.MaxMpaRatingId is int max)
                {
                    // A capped channel excludes both over-rated and unknown-rated movies.
                    if (ratingId == 0 || ratingId > max)
                        continue;
                }

                if (row.PlayableId == null)
                    continue; // every movie has a Playable post-cutover; skip defensively
                if (ratingId > ceiling)
                    ceiling = ratingId;
                eligible.Add(new EligibleMovie(row.id, row.PlayableId.Value, durationTicks, ratingId));
            }

            int effectiveCeiling = filter.MaxMpaRatingId ?? (ceiling == 0 ? 7 : ceiling);
            return (eligible, effectiveCeiling);
        }

        /// <summary>
        /// The effective rating ceiling alone — for the visibility gate. Cached (see <see cref="CeilingTtl"/>):
        /// an explicit cap is free, otherwise the eligible-set scan runs at most once per TTL per channel.
        /// </summary>
        public async Task<int> GetCeilingAsync(Channel channel, CancellationToken cancel = default)
        {
            var filter = ChannelFilter.Parse(channel.FilterJson);
            if (filter.MaxMpaRatingId is int explicitMax)
                return explicitMax;

            var key = CeilingKey(channel);
            if (cache.TryGetValue(key, out int cached))
                return cached;

            var (_, ceiling) = await BuildEligibleAsync(channel, cancel);
            cache.Set(key, ceiling, new MemoryCacheEntryOptions
            {
                Size = 1, // the shared cache enforces a size limit, so every entry must declare one
                AbsoluteExpirationRelativeToNow = CeilingTtl,
            });
            return ceiling;
        }

        /// <summary>
        /// The ceiling if it's free (explicit cap) or already cached, without triggering the expensive
        /// scan. Lets a hot read path (the guide) gate cheaply and leave any cold channel to the
        /// background maintainer rather than doing O(channels) scans inside one request.
        /// </summary>
        public bool TryGetCachedCeiling(Channel channel, out int ceiling)
        {
            var filter = ChannelFilter.Parse(channel.FilterJson);
            if (filter.MaxMpaRatingId is int explicitMax)
            {
                ceiling = explicitMax;
                return true;
            }
            return cache.TryGetValue(CeilingKey(channel), out ceiling);
        }

        /// <summary>
        /// Bulk-read the windowed lineup for many channels in a single query — the read primitive behind
        /// the grid guide. Pure read: no extend, no prune, no writes, so it stays O(1) queries regardless
        /// of how many channels are passed. Channels not yet materialized simply return no rows (the
        /// background maintainer fills them); returns a map of channelId → items ordered by start.
        /// </summary>
        public async Task<Dictionary<int, List<ChannelScheduleItem>>> WindowedItemsAsync(
            IReadOnlyCollection<int> channelIds, DateTime fromUtc, DateTime toUtc, CancellationToken cancel = default)
        {
            if (channelIds.Count == 0)
                return new Dictionary<int, List<ChannelScheduleItem>>();

            var rows = await movieDb.ChannelScheduleItems
                .Where(i => channelIds.Contains(i.ChannelId) && i.EndUtc > fromUtc && i.StartUtc < toUtc)
                .OrderBy(i => i.StartUtc)
                .ToListAsync(cancel);

            return rows.GroupBy(i => i.ChannelId).ToDictionary(g => g.Key, g => g.ToList());
        }

        /// <summary>Enabled channel ids in display order — for the background maintainer's round-robin.</summary>
        public Task<List<int>> EnabledChannelIdsAsync(CancellationToken cancel = default) =>
            movieDb.Channels.Where(c => c.Enabled).OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .Select(c => c.Id).ToListAsync(cancel);

        /// <summary>
        /// Extend one channel's lineup to the horizon and warm its ceiling cache — the unit of work the
        /// background maintainer repeats. Idempotent: a channel already materialized to the horizon is a
        /// cheap no-op. No-op too if the channel vanished or was disabled.
        /// </summary>
        public async Task EnsureAndWarmChannelAsync(int channelId, DateTime horizonUtc, CancellationToken cancel = default)
        {
            var channel = await movieDb.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.Enabled, cancel);
            if (channel == null)
                return;
            await EnsureScheduleAsync(channel, horizonUtc, cancel);
            await GetCeilingAsync(channel, cancel);
        }

        /// <summary>
        /// Ensures the channel has materialized items out to <paramref name="horizonUtc"/>,
        /// pruning items more than a few days past. Returns the channel's items ordered by
        /// start time. Already-written rows are never touched.
        /// </summary>
        public async Task<List<ChannelScheduleItem>> EnsureScheduleAsync(Channel channel, DateTime horizonUtc, CancellationToken cancel = default)
        {
            // Serialize generation per channel: the background maintainer and a concurrent viewer request
            // must not both read the same cursor and append overlapping rows. Per-channel gate, so distinct
            // channels never contend; static because this service is scoped (a fresh instance per request).
            var gate = generationGates.GetOrAdd(channel.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancel);
            try
            {
                return await EnsureScheduleCoreAsync(channel, horizonUtc, cancel);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<List<ChannelScheduleItem>> EnsureScheduleCoreAsync(Channel channel, DateTime horizonUtc, CancellationToken cancel)
        {
            var now = DateTime.UtcNow;

            var stale = await movieDb.ChannelScheduleItems
                .Where(i => i.ChannelId == channel.Id && i.EndUtc < now - PruneAge)
                .ToListAsync(cancel);
            if (stale.Count > 0)
                movieDb.ChannelScheduleItems.RemoveRange(stale);

            var items = await movieDb.ChannelScheduleItems
                .Where(i => i.ChannelId == channel.Id)
                .OrderBy(i => i.StartUtc)
                .ToListAsync(cancel);

            var cursor = items.Count > 0
                ? items[^1].EndUtc
                : (channel.AnchorUtc > now.AddHours(-6) ? channel.AnchorUtc : now);

            if (cursor < horizonUtc)
            {
                var (eligible, _) = await BuildEligibleAsync(channel, cancel);
                if (eligible.Count > 0)
                {
                    int lastPlayableId = items.Count > 0 ? items[^1].PlayableId : -1;
                    int round = 0;
                    var queue = new Queue<EligibleMovie>();

                    while (cursor < horizonUtc)
                    {
                        if (queue.Count == 0)
                            queue = ShuffleRound(eligible, channel.Seed, round++, channel.ShuffleMode);

                        var movie = queue.Dequeue();
                        // Avoid playing the same film back-to-back across a round boundary.
                        if (movie.PlayableId == lastPlayableId && queue.Count > 0)
                        {
                            queue.Enqueue(movie);
                            movie = queue.Dequeue();
                        }

                        var end = cursor.AddTicks(movie.DurationTicks);
                        var item = new ChannelScheduleItem
                        {
                            ChannelId = channel.Id,
                            PlayableId = movie.PlayableId,
                            StartUtc = cursor,
                            EndUtc = end,
                        };
                        movieDb.ChannelScheduleItems.Add(item);
                        items.Add(item);
                        lastPlayableId = movie.PlayableId;
                        cursor = end;
                    }
                }
            }

            if (movieDb.ChangeTracker.HasChanges())
                await movieDb.SaveChangesAsync(cancel);

            return items.OrderBy(i => i.StartUtc).ToList();
        }

        /// <summary>
        /// Collapses the currently-airing item to end now and pulls every later item up by the
        /// same amount, so the channel jumps to the next movie for everyone while staying
        /// contiguous (streaming-plan.md §8 vote-to-skip). Guarded by <paramref name="expectedItemId"/>:
        /// if the channel has already advanced past that item, this is a no-op — which makes
        /// concurrent skip triggers for the same item safe (only the first one moves the line).
        /// </summary>
        public async Task<bool> SkipCurrentAsync(Channel channel, long expectedItemId, CancellationToken cancel = default)
        {
            var now = DateTime.UtcNow;
            var items = await EnsureScheduleAsync(channel, now.Add(ScheduleHorizon), cancel);

            var current = items.FirstOrDefault(i => i.StartUtc <= now && now < i.EndUtc);
            if (current == null || current.Id != expectedItemId)
                return false;

            var originalEnd = current.EndUtc;
            var delta = originalEnd - now;
            if (delta <= TimeSpan.Zero)
                return false;

            current.EndUtc = now;
            foreach (var item in items.Where(i => i.StartUtc >= originalEnd))
            {
                item.StartUtc -= delta;
                item.EndUtc -= delta;
            }

            await movieDb.SaveChangesAsync(cancel);
            return true;
        }

        /// <summary>
        /// Restarts the currently-airing item from the top: its start is reset to now and its end
        /// — along with every later item — is pushed back by however much had already played, so the
        /// channel replays the same movie from the beginning for everyone while staying contiguous
        /// (the mirror of <see cref="SkipCurrentAsync"/>). Guarded by <paramref name="expectedItemId"/>
        /// so concurrent restart triggers for the same item are safe.
        /// </summary>
        public async Task<bool> RestartCurrentAsync(Channel channel, long expectedItemId, CancellationToken cancel = default)
        {
            var now = DateTime.UtcNow;
            var items = await EnsureScheduleAsync(channel, now.Add(ScheduleHorizon), cancel);

            var current = items.FirstOrDefault(i => i.StartUtc <= now && now < i.EndUtc);
            if (current == null || current.Id != expectedItemId)
                return false;

            var elapsed = now - current.StartUtc;
            if (elapsed <= TimeSpan.Zero)
                return false;

            var originalEnd = current.EndUtc;
            current.StartUtc = now;
            current.EndUtc = originalEnd + elapsed;
            foreach (var item in items.Where(i => i.StartUtc >= originalEnd))
            {
                item.StartUtc += elapsed;
                item.EndUtc += elapsed;
            }

            await movieDb.SaveChangesAsync(cancel);
            return true;
        }

        /// <summary>
        /// Seeks the currently-airing item to <paramref name="targetOffsetSeconds"/>: shifts the item so
        /// that "now" lands at the requested offset, and slides every later item by the same delta to keep
        /// the line contiguous. A generalization of skip/restart — a positive delta rewinds the film (like
        /// <see cref="RestartCurrentAsync"/> to offset 0), a negative delta fast-forwards it (like
        /// <see cref="SkipCurrentAsync"/> toward the end). Used only for a lone viewer scrubbing the bar,
        /// since it moves the shared timeline continuously. Guarded by <paramref name="expectedItemId"/>.
        /// </summary>
        public async Task<bool> SeekCurrentAsync(Channel channel, long expectedItemId, double targetOffsetSeconds, CancellationToken cancel = default)
        {
            var now = DateTime.UtcNow;
            var items = await EnsureScheduleAsync(channel, now.Add(ScheduleHorizon), cancel);

            var current = items.FirstOrDefault(i => i.StartUtc <= now && now < i.EndUtc);
            if (current == null || current.Id != expectedItemId)
                return false;

            // Clamp into the film, leaving a second of tail so a seek-to-end doesn't land on the boundary
            // and immediately advance.
            var duration = (current.EndUtc - current.StartUtc).TotalSeconds;
            var target = TimeSpan.FromSeconds(Math.Clamp(targetOffsetSeconds, 0, Math.Max(0, duration - 1)));

            var newStart = now - target;
            var delta = newStart - current.StartUtc;
            if (delta == TimeSpan.Zero)
                return true;

            var originalEnd = current.EndUtc;
            current.StartUtc = newStart;
            current.EndUtc = originalEnd + delta;
            foreach (var item in items.Where(i => i.StartUtc >= originalEnd))
            {
                item.StartUtc += delta;
                item.EndUtc += delta;
            }

            await movieDb.SaveChangesAsync(cancel);
            return true;
        }

        /// <summary>
        /// Resume after a shared pause: slide the item that was airing when we froze — and every item
        /// after it — forward by <paramref name="wasPausedFor"/>, so the channel picks up exactly where
        /// it left off instead of jumping ahead by the wall-clock time spent paused. A contiguous shift,
        /// the same shape as <see cref="SkipCurrentAsync"/>/<see cref="RestartCurrentAsync"/>.
        /// </summary>
        public async Task ShiftForResumeAsync(Channel channel, TimeSpan wasPausedFor, CancellationToken cancel = default)
        {
            if (wasPausedFor <= TimeSpan.Zero)
                return;

            var now = DateTime.UtcNow;
            var items = await EnsureScheduleAsync(channel, now.Add(ScheduleHorizon), cancel);

            // The item airing at the moment of pause is the one whose original window still
            // contained that instant; shift it and everything later back into the future.
            var pausedAt = now - wasPausedFor;
            foreach (var item in items.Where(i => i.EndUtc > pausedAt))
            {
                item.StartUtc += wasPausedFor;
                item.EndUtc += wasPausedFor;
            }

            if (movieDb.ChangeTracker.HasChanges())
                await movieDb.SaveChangesAsync(cancel);
        }

        // Deterministic Fisher-Yates so a regenerated round reproduces (rows are never
        // rewritten, but determinism keeps races harmless). ReleaseDate mode orders by id
        // ascending as a stable proxy when no shuffle is wanted.
        private static Queue<EligibleMovie> ShuffleRound(List<EligibleMovie> source, int seed, int round, string mode)
        {
            var list = new List<EligibleMovie>(source);
            if (string.Equals(mode, "ReleaseDate", StringComparison.OrdinalIgnoreCase))
            {
                list.Sort((a, b) => a.MovieId.CompareTo(b.MovieId));
                return new Queue<EligibleMovie>(list);
            }

            var rng = new Random(unchecked(seed * 31 + round));
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return new Queue<EligibleMovie>(list);
        }
    }
}
