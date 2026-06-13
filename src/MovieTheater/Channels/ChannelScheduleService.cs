using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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

        private readonly MovieDb movieDb;
        private readonly ILogger<ChannelScheduleService> logger;

        public ChannelScheduleService(MovieDb movieDb, ILogger<ChannelScheduleService> logger)
        {
            this.movieDb = movieDb;
            this.logger = logger;
        }

        public record EligibleMovie(int MovieId, long DurationTicks, int RatingId);

        /// <summary>
        /// The channel's current eligible set plus its effective rating ceiling (the max
        /// rating id present, unless the filter caps it lower). Eligible movies have a
        /// playable file and a known duration.
        /// </summary>
        public async Task<(List<EligibleMovie> Movies, int Ceiling)> BuildEligibleAsync(Channel channel, CancellationToken cancel = default)
        {
            var filter = ChannelFilter.Parse(channel.FilterJson);

            var query = movieDb.Movies
                .Where(m => m.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null));

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
                    m.Rating,
                    m.RuntimeMinutes,
                    DurationTicks = m.Files
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

                if (ratingId > ceiling)
                    ceiling = ratingId;
                eligible.Add(new EligibleMovie(row.id, durationTicks, ratingId));
            }

            int effectiveCeiling = filter.MaxMpaRatingId ?? (ceiling == 0 ? 7 : ceiling);
            return (eligible, effectiveCeiling);
        }

        /// <summary>The effective rating ceiling alone — cheaper for the List visibility gate.</summary>
        public async Task<int> GetCeilingAsync(Channel channel, CancellationToken cancel = default)
        {
            var filter = ChannelFilter.Parse(channel.FilterJson);
            if (filter.MaxMpaRatingId is int explicitMax)
                return explicitMax;
            var (_, ceiling) = await BuildEligibleAsync(channel, cancel);
            return ceiling;
        }

        /// <summary>
        /// Ensures the channel has materialized items out to <paramref name="horizonUtc"/>,
        /// pruning items more than a few days past. Returns the channel's items ordered by
        /// start time. Already-written rows are never touched.
        /// </summary>
        public async Task<List<ChannelScheduleItem>> EnsureScheduleAsync(Channel channel, DateTime horizonUtc, CancellationToken cancel = default)
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
                    int lastMovieId = items.Count > 0 ? items[^1].MovieID : -1;
                    int round = 0;
                    var queue = new Queue<EligibleMovie>();

                    while (cursor < horizonUtc)
                    {
                        if (queue.Count == 0)
                            queue = ShuffleRound(eligible, channel.Seed, round++, channel.ShuffleMode);

                        var movie = queue.Dequeue();
                        // Avoid playing the same film back-to-back across a round boundary.
                        if (movie.MovieId == lastMovieId && queue.Count > 0)
                        {
                            queue.Enqueue(movie);
                            movie = queue.Dequeue();
                        }

                        var end = cursor.AddTicks(movie.DurationTicks);
                        var item = new ChannelScheduleItem
                        {
                            ChannelId = channel.Id,
                            MovieID = movie.MovieId,
                            StartUtc = cursor,
                            EndUtc = end,
                        };
                        movieDb.ChannelScheduleItems.Add(item);
                        items.Add(item);
                        lastMovieId = movie.MovieId;
                        cursor = end;
                    }
                }
            }

            if (movieDb.ChangeTracker.HasChanges())
                await movieDb.SaveChangesAsync(cancel);

            return items.OrderBy(i => i.StartUtc).ToList();
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
