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
using MovieTheater.Web;

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

        /// <summary>
        /// How long a shared pause may hold a channel frozen before it lifts itself. The pause is durable
        /// on purpose (pause, walk away, come back to the same frame), but "durable" can't mean "forever":
        /// somebody who pauses and never returns would otherwise leave the channel stuck on one show
        /// indefinitely, so the next person to tune in — or the background sweep, if nobody ever does —
        /// finds a day-old still frame. Half a day is long enough to cover an evening-to-morning walk-away
        /// and short enough that a channel is always live again by the next viewing session.
        /// </summary>
        public static readonly TimeSpan StalePauseAge = TimeSpan.FromHours(12);

        // A weighted shuffle re-rolls its order on this cadence (keyed by slot start time) so a large pool
        // rotates through its whole catalog over days instead of forever cycling one anti-repeat window.
        private static readonly TimeSpan ShuffleRotation = TimeSpan.FromHours(3);

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

        /// <summary>A schedulable item: a playable, its duration and gating rating, plus the weight and
        /// ordering inputs the scheduling strategies consume (Phase C/D). <see cref="OrderRank"/> is a
        /// stable per-title order (movie/misc id, or series·season·episode) for Marathon/ReleaseDate
        /// modes; <see cref="GroupId"/> is the series id for episodes (0 otherwise).</summary>
        public record EligibleItem(
            int PlayableId,
            long DurationTicks,
            int RatingId,
            int? Rewatchability,
            double? Quality,
            long OrderRank,
            int GroupId,
            string SortKey,
            double? Weight = null);

        // The eligible-set build runs the heavy AI-insight/tag joins, so the result (set + ceiling) is
        // cached briefly; the 15-min ceiling cache below is a thin derivation of it. Keyed by channel id
        // + filter hash so an admin filter edit busts it.
        private static readonly TimeSpan EligibleTtl = TimeSpan.FromMinutes(2);

        private static string EligibleKey(Channel channel) =>
            $"channel-eligible:{channel.Id}:{(channel.FilterJson ?? string.Empty).GetHashCode()}";

        /// <summary>The channel's eligible set + effective rating ceiling, cached (see <see cref="EligibleTtl"/>).</summary>
        public async Task<(List<EligibleItem> Items, int Ceiling)> GetEligibleAsync(Channel channel, CancellationToken cancel = default)
        {
            if (cache.TryGetValue(EligibleKey(channel), out (List<EligibleItem> Items, int Ceiling) hit))
                return hit;
            var built = await BuildEligibleCoreAsync(channel, cancel);
            cache.Set(EligibleKey(channel), built, new MemoryCacheEntryOptions
            {
                Size = 1, // the shared cache enforces a size limit, so every entry must declare one
                AbsoluteExpirationRelativeToNow = EligibleTtl,
            });
            return built;
        }

        /// <summary>
        /// Drop the cached eligible set + ceiling for a channel so the next schedule extension rebuilds from
        /// live data. Called after a playlist's items change — otherwise the 2-min eligible cache would briefly
        /// hide a just-added (or keep a just-removed) title. Cheap: two cache removes.
        /// </summary>
        public void InvalidateEligible(Channel channel)
        {
            cache.Remove(EligibleKey(channel));
            cache.Remove(CeilingKey(channel));
        }

        // The "current" insight for a subject: the row with the highest SpecVersion, then the latest
        // GeneratedUtc, then the highest Id — expressed as "no strictly-better row exists" so it stays a
        // single EF-translatable EXISTS (not a client-evaluated GroupBy). The composite index on
        // (SubjectKind, SubjectId, SpecVersion, GeneratedUtc, Id) makes it a seek.
        private IQueryable<TitleInsight> CurrentInsights(InsightSubjectKind kind) =>
            movieDb.TitleInsights.Where(ti => ti.SubjectKind == kind &&
                !movieDb.TitleInsights.Any(o => o.SubjectKind == kind && o.SubjectId == ti.SubjectId &&
                    (o.SpecVersion > ti.SpecVersion
                     || (o.SpecVersion == ti.SpecVersion && o.GeneratedUtc > ti.GeneratedUtc)
                     || (o.SpecVersion == ti.SpecVersion && o.GeneratedUtc == ti.GeneratedUtc && o.Id > ti.Id))));

        // A slot shorter than this is bad metadata, not a short film: Flash Gordon (1980) carried a
        // DurationTicks of ~5.58 s for a 111-minute AVI and the scheduler duly minted a 5.58-second slot,
        // which the channel blew through in seconds. Legitimate shorts run minutes, so the floor is safe.
        private const long MinItemDurationTicks = 60 * TicksPerSecond;

        // Per-kind SQL projection: enough to resolve duration, the effective rating (precedence
        // A→B→C), the quality/rewatch weight inputs, and the order/group keys.
        //
        // Duration comes from the file the player would actually open — Primary first, by (Role, Id),
        // exactly as StreamController.Start picks it, and only from files that carry a duration at all.
        // Unordered, this took an arbitrary matching row, so a seconds-long Extra/Variant stub could
        // supply the scheduled length of the feature it hangs off.
        private sealed record Cand(
            int PlayableId, long? DurationTicks, int? RuntimeMinutes,
            string? RatingA, string? RatingB, string? RatingC,
            decimal? ImdbRating, int? Tomatometer,
            long OrderRank, int GroupId, string? SortKey);

        /// <summary>
        /// Builds the channel's eligible set across the kinds its filter allows (movies, series→episodes,
        /// misc), applying every predicate in SQL, then resolving duration + effective rating + ceiling in
        /// memory. Eligible items have a synced, present file and a known duration.
        /// </summary>
        private async Task<(List<EligibleItem> Items, int Ceiling)> BuildEligibleCoreAsync(Channel channel, CancellationToken cancel)
        {
            // A user playlist (and a watch party, which is the same thing with a Begin-gate) has an explicit
            // hand-ordered lineup in PlaylistItem rather than a filter over the library, so it takes a wholly
            // separate build path — see docs/playlists-watchparty-plan.md.
            if (channel.IsUserPlaylist)
                return await BuildPlaylistEligibleCoreAsync(channel, cancel);

            var filter = ChannelFilter.Parse(channel.FilterJson);
            // Only a date-windowed channel may air holiday-locked titles; everywhere else they're invisible
            // year-round (see ChannelCatalog.HolidayLockKeys). Derived here rather than stored in FilterJson.
            filter.AllowHolidayLocked = ChannelSeason.HasSeason(channel);

            var cands = new List<Cand>();
            if (filter.Kinds.HasFlag(ContentKinds.Movies))
                cands.AddRange(await MovieCandidates(filter).ToListAsync(cancel));
            if (filter.Kinds.HasFlag(ContentKinds.Series))
                cands.AddRange(await EpisodeCandidates(filter).ToListAsync(cancel));
            if (filter.Kinds.HasFlag(ContentKinds.Misc))
                cands.AddRange(await MiscCandidates(filter).ToListAsync(cancel));

            // Specials / OVAs / shorts / music videos tied to a series surface wherever that series airs,
            // grouped with it — so a Series-inclusive channel automatically carries the DBZ TV specials on
            // Dragon Ball, the Pokémon specials on Pokemon, the Peanuts holiday specials wherever Peanuts
            // airs, with no per-channel wiring. Bonus-feature "Extra" misc (deleted scenes, behind-the-
            // scenes) never air.
            if (filter.Kinds.HasFlag(ContentKinds.Series))
            {
                var seriesIds = cands.Where(c => c.GroupId != 0).Select(c => c.GroupId).Distinct().ToList();
                if (seriesIds.Count > 0)
                    cands.AddRange(await RelatedMiscCandidates(seriesIds).ToListAsync(cancel));
            }
            // A misc can arrive via both the explicit Misc kind and the related-series path — keep one.
            cands = cands.GroupBy(c => c.PlayableId).Select(g => g.First()).ToList();

            // Resolve each candidate's current-insight rewatchability with one map per kind, rather than a
            // correlated best-insight subquery per candidate (identical value — CurrentInsights is unique
            // per subject). Movies/misc key on the title id (OrderRank; a misc id misses the movie map →
            // null, as before); episodes key on the series id (GroupId).
            var movieSubjectIds = cands.Where(c => c.GroupId == 0).Select(c => (int)c.OrderRank).Distinct().ToList();
            var seriesSubjectIds = cands.Where(c => c.GroupId != 0).Select(c => c.GroupId).Distinct().ToList();
            var movieRewatch = movieSubjectIds.Count == 0 ? new Dictionary<int, int?>()
                : await CurrentInsights(InsightSubjectKind.Movie).Where(ti => movieSubjectIds.Contains(ti.SubjectId))
                    .Select(ti => new { ti.SubjectId, ti.Rewatchability }).ToDictionaryAsync(x => x.SubjectId, x => x.Rewatchability, cancel);
            var seriesRewatch = seriesSubjectIds.Count == 0 ? new Dictionary<int, int?>()
                : await CurrentInsights(InsightSubjectKind.Series).Where(ti => seriesSubjectIds.Contains(ti.SubjectId))
                    .Select(ti => new { ti.SubjectId, ti.Rewatchability }).ToDictionaryAsync(x => x.SubjectId, x => x.Rewatchability, cancel);

            // Personalized ("For You") channels: the per-user recommendation score becomes each item's
            // schedule Weight, so the weighted shuffle airs better-fit titles more often (see Copies).
            // A user has at most ~100 recs per kind, so load them whole (no big IN over the candidate ids).
            var recoMovieScore = new Dictionary<int, double>();
            var recoSeriesScore = new Dictionary<int, double>();
            if (filter.RecommendedForUserId is int ruid)
            {
                recoMovieScore = await movieDb.TitleRecommendations
                    .Where(r => r.UserId == ruid && r.SubjectKind == InsightSubjectKind.Movie)
                    .ToDictionaryAsync(r => r.SubjectId, r => r.Score, cancel);
                recoSeriesScore = await movieDb.TitleRecommendations
                    .Where(r => r.UserId == ruid && r.SubjectKind == InsightSubjectKind.Series)
                    .ToDictionaryAsync(r => r.SubjectId, r => r.Score, cancel);
            }

            // Real-bucket (1..6) rating map + adult set, resolved in memory like the rest of the age gate.
            var (ratingMap, adultIds) = await LoadRatingMapsAsync(cancel);
            int Effective(string? a, string? b, string? c) => EffectiveRating(ratingMap, a, b, c);

            var items = new List<EligibleItem>(cands.Count);
            int maxRating = 0;
            foreach (var c in cands)
            {
                long durationTicks = c.DurationTicks
                    ?? (c.RuntimeMinutes is int min && min > 0 ? (long)min * 60 * TicksPerSecond : 0);
                if (durationTicks <= 0)
                    continue; // §8: skip items with neither a file duration nor a runtime
                if (durationTicks < MinItemDurationTicks)
                {
                    logger.LogWarning("Channel {ChannelId}: playable {PlayableId} excluded from the lineup — duration {DurationSeconds:F2}s is under the {FloorSeconds}s floor (bad file metadata)",
                        channel.Id, c.PlayableId, durationTicks / (double)TicksPerSecond, MinItemDurationTicks / TicksPerSecond);
                    continue;
                }

                int ratingId = Effective(c.RatingA, c.RatingB, c.RatingC);
                if (filter.MaxMpaRatingId is int max && ratingId > max)
                    continue; // a capped channel excludes over-rated and unknown titles
                if (filter.ExcludeAdult && adultIds.Contains(ratingId))
                    continue; // NC-17 / X excluded by default

                double? quality = c.ImdbRating.HasValue ? (double)c.ImdbRating.Value / 10.0
                    : (c.Tomatometer.HasValue ? c.Tomatometer.Value / 100.0 : (double?)null);

                if (ratingId > maxRating) maxRating = ratingId;
                int? rewatch = c.GroupId != 0
                    ? seriesRewatch.GetValueOrDefault(c.GroupId)
                    : movieRewatch.GetValueOrDefault((int)c.OrderRank);
                double? recoWeight = null;
                if (filter.RecommendedForUserId != null)
                {
                    if (c.GroupId != 0) { if (recoSeriesScore.TryGetValue(c.GroupId, out var sv)) recoWeight = sv; }
                    else if (recoMovieScore.TryGetValue((int)c.OrderRank, out var mv)) recoWeight = mv;
                }
                items.Add(new EligibleItem(c.PlayableId, durationTicks, ratingId,
                    rewatch, quality, c.OrderRank, c.GroupId, c.SortKey ?? "", recoWeight));
            }

            int ceiling = filter.MaxMpaRatingId ?? (maxRating == 0 ? RatingGate.UnknownRatingId : maxRating);
            return (items, ceiling);
        }

        // Real-bucket (1..6) rating map + the adult (NC-17/X) id set, both resolved in memory like the age
        // gate (RatingGate). Shared by the filter and playlist eligible-set builders.
        private async Task<(Dictionary<string, int> RatingMap, HashSet<int> AdultIds)> LoadRatingMapsAsync(CancellationToken cancel)
        {
            var ratingRows = await movieDb.RatingMaps
                .Where(rm => rm.MovieRating != null && rm.MPARatingID >= 1 && rm.MPARatingID <= 6)
                .Select(rm => new { rm.MovieRating, rm.MPARatingID })
                .ToListAsync(cancel);
            var ratingMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ratingRows)
                if (r.MovieRating != null) ratingMap[r.MovieRating.Trim()] = r.MPARatingID;

            var adultIds = new HashSet<int>(await movieDb.RatingMpas
                .Where(rm => rm.MinAge >= 18).Select(rm => rm.RatingID).ToListAsync(cancel));
            return (ratingMap, adultIds);
        }

        private static int EffectiveRating(Dictionary<string, int> ratingMap, string? a, string? b, string? c)
        {
            foreach (var t in new[] { a, b, c })
                if (!string.IsNullOrWhiteSpace(t) && ratingMap.TryGetValue(t!.Trim(), out var id))
                    return id;
            return RatingGate.UnknownRatingId; // 7 = conservative (adults only)
        }

        /// <summary>
        /// Eligible set for a user playlist / watch party: its explicit, hand-ordered <see cref="PlaylistItem"/>
        /// rows, in <see cref="PlaylistItem.Position"/> order. Each item is resolved to duration + effective
        /// rating across whichever kind it is (movie / episode / misc), requiring a synced, present file with a
        /// known duration — an item whose file has gone missing simply drops out of the lineup. Unlike a filter
        /// channel there is no MPAA cap or adult exclusion (these are the user's own explicit choices), but the
        /// ceiling still tracks the highest rating so the per-viewer age gate in the controller applies. Order is
        /// carried in <see cref="EligibleItem.OrderRank"/> = Position, which the "Playlist" strategy sorts by.
        /// </summary>
        private async Task<(List<EligibleItem> Items, int Ceiling)> BuildPlaylistEligibleCoreAsync(Channel channel, CancellationToken cancel)
        {
            var rows = await movieDb.PlaylistItems
                .Where(p => p.ChannelId == channel.Id)
                .OrderBy(p => p.Position).ThenBy(p => p.Id)
                .Select(p => new { p.PlayableId, p.Position })
                .ToListAsync(cancel);
            if (rows.Count == 0)
                return (new List<EligibleItem>(), RatingGate.UnknownRatingId);

            var ids = rows.Select(p => p.PlayableId).Distinct().ToList();

            // Resolve each playable id → duration + effective-rating inputs across the three kinds. Same
            // present-file requirement and Cand shape as the filter path, just keyed by an explicit id set.
            var cands = new Dictionary<int, Cand>();
            foreach (var m in await movieDb.Movies
                .Where(m => m.PlayableId != null && ids.Contains(m.PlayableId.Value)
                    && m.Playable!.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null))
                .Select(m => new Cand(
                    m.PlayableId!.Value,
                    m.Playable!.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null && f.DurationTicks != null)
                        .OrderBy(f => f.Role).ThenBy(f => f.Id).Select(f => (long?)f.DurationTicks).FirstOrDefault(),
                    m.RuntimeMinutes,
                    m.MpaaRating, m.Rating, m.MpaaRatingInferred,
                    m.ImdbRatingScraped, m.RtTomatometer,
                    (long)m.id, 0, m.SimpleTitle))
                .ToListAsync(cancel))
                cands[m.PlayableId] = m;

            foreach (var e in await movieDb.Episodes
                .Where(e => e.PlayableId != null && ids.Contains(e.PlayableId.Value)
                    && e.Playable!.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null))
                .Select(e => new Cand(
                    e.PlayableId!.Value,
                    e.Playable!.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null && f.DurationTicks != null)
                        .OrderBy(f => f.Role).ThenBy(f => f.Id).Select(f => (long?)f.DurationTicks).FirstOrDefault(),
                    e.RuntimeMinutes,
                    e.Series!.MpaaRating, e.Series.Rating, e.Series.MpaaRatingInferred,
                    e.Series.ImdbRatingScraped, e.Series.RtTomatometer,
                    0L, e.SeriesId ?? 0, e.Series.SimpleTitle))
                .ToListAsync(cancel))
                cands[e.PlayableId] = e;

            foreach (var mv in await movieDb.MiscVideos
                .Where(mv => ids.Contains(mv.PlayableId)
                    && mv.Playable.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null))
                .Select(mv => new Cand(
                    mv.PlayableId,
                    mv.Playable.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null && f.DurationTicks != null)
                        .OrderBy(f => f.Role).ThenBy(f => f.Id).Select(f => (long?)f.DurationTicks).FirstOrDefault(),
                    null,
                    null, null, mv.MpaaRatingInferred,
                    null, null,
                    0L, 0, mv.SimpleTitle))
                .ToListAsync(cancel))
                cands[mv.PlayableId] = mv;

            var (ratingMap, _) = await LoadRatingMapsAsync(cancel);

            // Emit one EligibleItem per playlist row (so a title added twice airs twice), ordered by Position.
            var items = new List<EligibleItem>(rows.Count);
            int maxRating = 0;
            foreach (var pi in rows)
            {
                if (!cands.TryGetValue(pi.PlayableId, out var c))
                    continue; // no present file / unknown playable — skip, keep the rest of the lineup

                long durationTicks = c.DurationTicks
                    ?? (c.RuntimeMinutes is int min && min > 0 ? (long)min * 60 * TicksPerSecond : 0);
                if (durationTicks <= 0)
                    continue;
                if (durationTicks < MinItemDurationTicks)
                {
                    logger.LogWarning("Channel {ChannelId}: playable {PlayableId} excluded from the lineup — duration {DurationSeconds:F2}s is under the {FloorSeconds}s floor (bad file metadata)",
                        channel.Id, pi.PlayableId, durationTicks / (double)TicksPerSecond, MinItemDurationTicks / TicksPerSecond);
                    continue;
                }

                int ratingId = EffectiveRating(ratingMap, c.RatingA, c.RatingB, c.RatingC);
                if (ratingId > maxRating) maxRating = ratingId;
                items.Add(new EligibleItem(pi.PlayableId, durationTicks, ratingId,
                    null, null, pi.Position, 0, "", null));
            }

            int ceiling = maxRating == 0 ? RatingGate.UnknownRatingId : maxRating;
            return (items, ceiling);
        }

        // AI slider/tag predicates as subject-id sets, so the same shape applies to movies (m.id),
        // series (s.Id) and misc (mv.Id) — each becomes `subjectIds.Contains(<id>)` (an IN/EXISTS).
        // Multiple rules AND together; values inside a rule OR (any) or AND (all); Negate flips to NOT.
        private List<(IQueryable<int> Ids, bool Negate)> AiFilters(ChannelFilter filter, InsightSubjectKind kind)
        {
            var cur = CurrentInsights(kind);
            var req = filter.RequireRecognized;
            var f = new List<(IQueryable<int>, bool)>();

            if (filter.CultClassic is FilterRange s1) { int? lo = s1.Min is double a ? (int)Math.Round(a) : (int?)null, hi = s1.Max is double b ? (int)Math.Round(b) : (int?)null; f.Add((cur.Where(ti => (!req || ti.Recognized) && ti.CultClassic != null && (lo == null || ti.CultClassic >= lo) && (hi == null || ti.CultClassic <= hi)).Select(ti => ti.SubjectId), false)); }
            if (filter.Surrealism is FilterRange s2) { int? lo = s2.Min is double a ? (int)Math.Round(a) : (int?)null, hi = s2.Max is double b ? (int)Math.Round(b) : (int?)null; f.Add((cur.Where(ti => (!req || ti.Recognized) && ti.Surrealism != null && (lo == null || ti.Surrealism >= lo) && (hi == null || ti.Surrealism <= hi)).Select(ti => ti.SubjectId), false)); }
            if (filter.Intensity is FilterRange s3) { int? lo = s3.Min is double a ? (int)Math.Round(a) : (int?)null, hi = s3.Max is double b ? (int)Math.Round(b) : (int?)null; f.Add((cur.Where(ti => (!req || ti.Recognized) && ti.Intensity != null && (lo == null || ti.Intensity >= lo) && (hi == null || ti.Intensity <= hi)).Select(ti => ti.SubjectId), false)); }
            if (filter.Novelty is FilterRange s4) { int? lo = s4.Min is double a ? (int)Math.Round(a) : (int?)null, hi = s4.Max is double b ? (int)Math.Round(b) : (int?)null; f.Add((cur.Where(ti => (!req || ti.Recognized) && ti.Novelty != null && (lo == null || ti.Novelty >= lo) && (hi == null || ti.Novelty <= hi)).Select(ti => ti.SubjectId), false)); }
            if (filter.Rewatchability is FilterRange s5) { int? lo = s5.Min is double a ? (int)Math.Round(a) : (int?)null, hi = s5.Max is double b ? (int)Math.Round(b) : (int?)null; f.Add((cur.Where(ti => (!req || ti.Recognized) && ti.Rewatchability != null && (lo == null || ti.Rewatchability >= lo) && (hi == null || ti.Rewatchability <= hi)).Select(ti => ti.SubjectId), false)); }
            if (filter.Energy is FilterRange s6) { int? lo = s6.Min is double a ? (int)Math.Round(a) : (int?)null, hi = s6.Max is double b ? (int)Math.Round(b) : (int?)null; f.Add((cur.Where(ti => (!req || ti.Recognized) && ti.Energy != null && (lo == null || ti.Energy >= lo) && (hi == null || ti.Energy <= hi)).Select(ti => ti.SubjectId), false)); }

            // Holiday lock: a Christmas- or Halloween-SPECIFIC title airs only on a seasonal channel, and is
            // excluded from every other channel every day of the year — Marquee and genre channels included.
            if (!filter.AllowHolidayLocked)
            {
                var locks = ChannelCatalog.HolidayLockKeys;
                f.Add((cur.Where(ti => ti.Tags.Any(t => t.Category == TagCategory.Channel && locks.Contains(t.Value)))
                          .Select(ti => ti.SubjectId), true));
            }

            foreach (var tr in filter.Tags)
            {
                if (tr.Values.Count == 0) continue;
                var cat = tr.Category; var vals = tr.Values;
                if (tr.Negate)
                    f.Add((cur.Where(ti => ti.Tags.Any(t => t.Category == cat && vals.Contains(t.Value))).Select(ti => ti.SubjectId), true));
                else if (string.Equals(tr.Mode, "all", StringComparison.OrdinalIgnoreCase))
                    foreach (var v in vals) { var vv = v; f.Add((cur.Where(ti => (!req || ti.Recognized) && ti.Tags.Any(t => t.Category == cat && t.Value == vv)).Select(ti => ti.SubjectId), false)); }
                else
                    f.Add((cur.Where(ti => (!req || ti.Recognized) && ti.Tags.Any(t => t.Category == cat && vals.Contains(t.Value))).Select(ti => ti.SubjectId), false));
            }

            return f;
        }

        private IQueryable<Movie> MovieQuery(ChannelFilter filter)
        {
            var q = movieDb.Movies.Where(m => m.ReviewBatch == null && m.PlayableId != null
                && m.Playable!.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null));

            if (filter.ExcludeRemoveFromRandom) q = q.Where(m => !m.RemoveFromRandom);

            if (filter.YearMin is int y0) q = q.Where(m => (m.ImdbReleaseDate ?? m.ReleaseDate) != null && (m.ImdbReleaseDate ?? m.ReleaseDate)!.Value.Year >= y0);
            if (filter.YearMax is int y1) q = q.Where(m => (m.ImdbReleaseDate ?? m.ReleaseDate) != null && (m.ImdbReleaseDate ?? m.ReleaseDate)!.Value.Year <= y1);

            if (filter.GenreIds.Count > 0)
            {
                if (string.Equals(filter.GenreMode, "all", StringComparison.OrdinalIgnoreCase))
                    foreach (var gid in filter.GenreIds) { var g = gid; q = q.Where(m => m.MovieGenres.Any(mg => mg.GenreId == g)); }
                else { var ids = filter.GenreIds; q = q.Where(m => m.MovieGenres.Any(mg => ids.Contains(mg.GenreId))); }
            }

            if (filter.ImdbRating?.Min is double i0) q = q.Where(m => m.ImdbRatingScraped >= (decimal)i0);
            if (filter.ImdbRating?.Max is double i1) q = q.Where(m => m.ImdbRatingScraped <= (decimal)i1);
            if (filter.Tomatometer?.Min is double t0) q = q.Where(m => m.RtTomatometer >= (int)t0);
            if (filter.Tomatometer?.Max is double t1) q = q.Where(m => m.RtTomatometer <= (int)t1);
            if (filter.Popcornmeter?.Min is double p0) q = q.Where(m => m.RtPopcornmeter >= (int)p0);
            if (filter.Popcornmeter?.Max is double p1) q = q.Where(m => m.RtPopcornmeter <= (int)p1);
            if (filter.Popularity?.Min is double o0) q = q.Where(m => m.TmdbPopularity >= (decimal)o0);
            if (filter.Popularity?.Max is double o1) q = q.Where(m => m.TmdbPopularity <= (decimal)o1);
            if (filter.VoteCount?.Min is double c0) q = q.Where(m => m.TmdbVoteCount >= (int)c0);
            if (filter.VoteCount?.Max is double c1) q = q.Where(m => m.TmdbVoteCount <= (int)c1);
            if (filter.Runtime?.Min is double r0) q = q.Where(m => m.RuntimeMinutes >= (int)r0);
            if (filter.Runtime?.Max is double r1) q = q.Where(m => m.RuntimeMinutes <= (int)r1);

            if (filter.Languages.Count > 0) { var langs = filter.Languages; q = q.Where(m => m.OriginalLanguage != null && langs.Contains(m.OriginalLanguage)); }
            if (filter.ExcludeLanguages.Count > 0) { var ex = filter.ExcludeLanguages; q = q.Where(m => m.OriginalLanguage != null && !ex.Contains(m.OriginalLanguage)); } // "World Cinema" = a KNOWN non-English language, not unknown/null
            if (filter.Countries.Count > 0) { var cos = filter.Countries; q = q.Where(m => m.Country != null && cos.Any(c => m.Country.Contains(c))); }

            // Networks are a TV-only facet; a movie can't satisfy one, so a network channel excludes movies.
            if (filter.Networks.Count > 0) q = q.Where(m => false);

            foreach (var cr in filter.Credits)
            {
                if (cr.PersonIds.Count == 0) continue;
                var pids = cr.PersonIds; var role = cr.Role;
                q = q.Where(m => m.Credits.Any(c => pids.Contains(c.PersonId) && (role == null || c.Role == role)));
            }

            if (filter.PathContains.Count > 0) { var pats = filter.PathContains; q = q.Where(m => m.Playable!.Files.Any(f => pats.Any(p => f.Path.Contains(p)))); }

            if (filter.UnwatchedByUserId is int uid) q = q.Where(m => !movieDb.Viewings.Any(v => v.UserID == uid && v.MovieID == m.id && v.ViewingType == ViewingTypes.Seen));
            // "Wanted" = the user's queue, whoever placed each title there (a friend's Want IS a suggestion).
            if (filter.WantedByUserId is int wid) q = q.Where(m => movieDb.Viewings.Any(v => v.UserID == wid && v.MovieID == m.id && v.ViewingType == ViewingTypes.WantToWatch));
            if (filter.RecommendedForUserId is int rmid) q = q.Where(m => movieDb.TitleRecommendations.Any(r => r.UserId == rmid && r.SubjectKind == InsightSubjectKind.Movie && r.SubjectId == m.id));
            if (filter.MinViewers is int minv) q = q.Where(m => movieDb.Viewings.Where(v => v.MovieID == m.id && v.ViewingType == ViewingTypes.Seen).Select(v => v.UserID).Distinct().Count() >= minv);
            if (filter.AddedWithinDays is int days) { var since = DateTime.UtcNow.AddDays(-days); q = q.Where(m => m.UploadedDate != null && m.UploadedDate >= since); }
            if (filter.ReleasedWithinYears is int ry) { var cut = DateTime.UtcNow.Date.AddYears(-ry); q = q.Where(m => (m.ImdbReleaseDate ?? m.ReleaseDate) != null && (m.ImdbReleaseDate ?? m.ReleaseDate)! >= cut); }

            foreach (var (ids, neg) in AiFilters(filter, InsightSubjectKind.Movie))
                q = neg ? q.Where(m => !ids.Contains(m.id)) : q.Where(m => ids.Contains(m.id));

            return q;
        }

        private IQueryable<Cand> MovieCandidates(ChannelFilter filter)
        {
            // Rewatchability is resolved in BuildEligibleCoreAsync via one map per kind (keyed by the title
            // id carried in OrderRank), not a correlated best-insight subquery per row.
            return MovieQuery(filter).Select(m => new Cand(
                m.PlayableId!.Value,
                m.Playable!.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null && f.DurationTicks != null)
                    .OrderBy(f => f.Role).ThenBy(f => f.Id).Select(f => (long?)f.DurationTicks).FirstOrDefault(),
                m.RuntimeMinutes,
                m.MpaaRating, m.Rating, m.MpaaRatingInferred,
                m.ImdbRatingScraped, m.RtTomatometer,
                (long)m.id, 0, m.SimpleTitle));
        }

        private IQueryable<Series> SeriesQuery(ChannelFilter filter)
        {
            var q = movieDb.Series.Where(s => s.ReviewBatch == null);

            if (filter.ExcludeRemoveFromRandom) q = q.Where(s => !s.RemoveFromRandom);

            if (filter.YearMin is int y0) q = q.Where(s => (s.ImdbReleaseDate ?? s.ReleaseDate) != null && (s.ImdbReleaseDate ?? s.ReleaseDate)!.Value.Year >= y0);
            if (filter.YearMax is int y1) q = q.Where(s => (s.ImdbReleaseDate ?? s.ReleaseDate) != null && (s.ImdbReleaseDate ?? s.ReleaseDate)!.Value.Year <= y1);

            if (filter.GenreIds.Count > 0)
            {
                if (string.Equals(filter.GenreMode, "all", StringComparison.OrdinalIgnoreCase))
                    foreach (var gid in filter.GenreIds) { var g = gid; q = q.Where(s => s.SeriesGenres.Any(sg => sg.GenreId == g)); }
                else { var ids = filter.GenreIds; q = q.Where(s => s.SeriesGenres.Any(sg => ids.Contains(sg.GenreId))); }
            }

            if (filter.ImdbRating?.Min is double i0) q = q.Where(s => s.ImdbRatingScraped >= (decimal)i0);
            if (filter.ImdbRating?.Max is double i1) q = q.Where(s => s.ImdbRatingScraped <= (decimal)i1);
            if (filter.Tomatometer?.Min is double t0) q = q.Where(s => s.RtTomatometer >= (int)t0);
            if (filter.Tomatometer?.Max is double t1) q = q.Where(s => s.RtTomatometer <= (int)t1);
            if (filter.Popcornmeter?.Min is double p0) q = q.Where(s => s.RtPopcornmeter >= (int)p0);
            if (filter.Popcornmeter?.Max is double p1) q = q.Where(s => s.RtPopcornmeter <= (int)p1);
            if (filter.Popularity?.Min is double o0) q = q.Where(s => s.TmdbPopularity >= (decimal)o0);
            if (filter.Popularity?.Max is double o1) q = q.Where(s => s.TmdbPopularity <= (decimal)o1);
            if (filter.VoteCount?.Min is double c0) q = q.Where(s => s.TmdbVoteCount >= (int)c0);
            if (filter.VoteCount?.Max is double c1) q = q.Where(s => s.TmdbVoteCount <= (int)c1);
            if (filter.Runtime?.Min is double r0) q = q.Where(s => s.RuntimeMinutes >= (int)r0);
            if (filter.Runtime?.Max is double r1) q = q.Where(s => s.RuntimeMinutes <= (int)r1);

            if (filter.Languages.Count > 0) { var langs = filter.Languages; q = q.Where(s => s.OriginalLanguage != null && langs.Contains(s.OriginalLanguage)); }
            if (filter.ExcludeLanguages.Count > 0) { var ex = filter.ExcludeLanguages; q = q.Where(s => s.OriginalLanguage != null && !ex.Contains(s.OriginalLanguage)); }
            if (filter.Countries.Count > 0) { var cos = filter.Countries; q = q.Where(s => s.Country != null && cos.Any(c => s.Country.Contains(c))); }

            if (filter.Networks.Count > 0) { var nets = filter.Networks; q = q.Where(s => s.Network != null && nets.Any(n => s.Network.Contains(n))); }

            foreach (var cr in filter.Credits)
            {
                if (cr.PersonIds.Count == 0) continue;
                var pids = cr.PersonIds; var role = cr.Role;
                q = q.Where(s => s.Credits.Any(c => pids.Contains(c.PersonId) && (role == null || c.Role == role)));
            }

            // A series matches a path channel when a MAJORITY of its streamable episodes live under a
            // matching path. Matching *any* episode let one stray file whose name happens to contain a
            // short pattern ("Lost", "Monster", "Chainsaw") drag in a whole 900-episode show; a real
            // show/collection folder satisfies the majority for every episode. (Movies still match on
            // their single file in MovieQuery — a film has no episodes for a stray to outvote.)
            if (filter.PathContains.Count > 0)
            {
                var pats = filter.PathContains;
                q = q.Where(s =>
                    s.Episodes.Count(e => e.Playable!.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null && pats.Any(p => f.Path.Contains(p)))) * 2
                    >= s.Episodes.Count(e => e.Playable!.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null)));
            }

            if (filter.UnwatchedByUserId is int uid) q = q.Where(s => !movieDb.Viewings.Any(v => v.UserID == uid && v.SeriesId == s.Id && v.ViewingType == ViewingTypes.Seen));
            if (filter.WantedByUserId is int wid) q = q.Where(s => movieDb.Viewings.Any(v => v.UserID == wid && v.SeriesId == s.Id && v.ViewingType == ViewingTypes.WantToWatch));
            if (filter.RecommendedForUserId is int rsid) q = q.Where(s => movieDb.TitleRecommendations.Any(r => r.UserId == rsid && r.SubjectKind == InsightSubjectKind.Series && r.SubjectId == s.Id));
            if (filter.MinViewers is int minv) q = q.Where(s => movieDb.Viewings.Where(v => v.SeriesId == s.Id && v.ViewingType == ViewingTypes.Seen).Select(v => v.UserID).Distinct().Count() >= minv);
            if (filter.AddedWithinDays is int days) { var since = DateTime.UtcNow.AddDays(-days); q = q.Where(s => s.UploadedDate != null && s.UploadedDate >= since); }
            if (filter.ReleasedWithinYears is int ry) { var cut = DateTime.UtcNow.Date.AddYears(-ry); q = q.Where(s => (s.ImdbReleaseDate ?? s.ReleaseDate) != null && (s.ImdbReleaseDate ?? s.ReleaseDate)! >= cut); }

            foreach (var (ids, neg) in AiFilters(filter, InsightSubjectKind.Series))
                q = neg ? q.Where(s => !ids.Contains(s.Id)) : q.Where(s => ids.Contains(s.Id));

            return q;
        }

        private IQueryable<Cand> EpisodeCandidates(ChannelFilter filter)
        {
            var sq = SeriesQuery(filter);
            // Rewatchability (the series' current insight) is resolved in BuildEligibleCoreAsync via a map
            // keyed by GroupId (the series id), not a correlated subquery per episode.
            return movieDb.Episodes
                .Where(e => e.SeriesId != null && e.PlayableId != null
                    && sq.Any(s => s.Id == e.SeriesId)
                    && e.Playable!.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null))
                .Select(e => new Cand(
                    e.PlayableId!.Value,
                    e.Playable!.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null && f.DurationTicks != null)
                        .OrderBy(f => f.Role).ThenBy(f => f.Id).Select(f => (long?)f.DurationTicks).FirstOrDefault(),
                    e.RuntimeMinutes,
                    e.Series!.MpaaRating, e.Series.Rating, e.Series.MpaaRatingInferred,
                    e.Series.ImdbRatingScraped, e.Series.RtTomatometer,
                    (long)e.SeriesId!.Value * 1_000_000L + e.SeasonNumber * 1000 + e.EpisodeNumber,
                    e.SeriesId!.Value,
                    e.Series.SimpleTitle));
        }

        private IQueryable<MiscVideo> MiscQuery(ChannelFilter filter)
        {
            // Misc carries no genres/credits/language/numeric scores — if a channel constrains on any of
            // those, a misc video can't satisfy it, so contribute nothing rather than wrongly include all.
            if (filter.GenreIds.Count > 0 || filter.Credits.Count > 0 || filter.Languages.Count > 0
                || filter.ExcludeLanguages.Count > 0 || filter.Countries.Count > 0 || filter.Networks.Count > 0
                || filter.ImdbRating != null || filter.Tomatometer != null || filter.Popcornmeter != null
                || filter.Popularity != null || filter.VoteCount != null || filter.Runtime != null)
                return movieDb.MiscVideos.Where(_ => false);

            var q = movieDb.MiscVideos.Where(mv => mv.ReviewBatch == null
                && mv.Playable.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null));

            if (filter.YearMin is int y0) q = q.Where(mv => mv.Year != null && mv.Year >= y0);
            if (filter.YearMax is int y1) q = q.Where(mv => mv.Year != null && mv.Year <= y1);
            // Misc videos carry only a year, no release date, so the rolling window rounds out to whole
            // calendar years here (slightly wider than the movie/series day-exact cut).
            if (filter.ReleasedWithinYears is int ry) { var minYear = DateTime.UtcNow.Year - ry; q = q.Where(mv => mv.Year != null && mv.Year >= minYear); }

            if (filter.PathContains.Count > 0) { var pats = filter.PathContains; q = q.Where(mv => mv.Playable.Files.Any(f => pats.Any(p => f.Path.Contains(p)))); }

            if (filter.UnwatchedByUserId is int uid) q = q.Where(mv => !movieDb.Viewings.Any(v => v.UserID == uid && v.MiscVideoId == mv.Id && v.ViewingType == ViewingTypes.Seen));

            foreach (var (ids, neg) in AiFilters(filter, InsightSubjectKind.MiscVideo))
                q = neg ? q.Where(mv => !ids.Contains(mv.Id)) : q.Where(mv => ids.Contains(mv.Id));

            return q;
        }

        private IQueryable<Cand> MiscCandidates(ChannelFilter filter)
        {
            // Misc has no insight rewatchability; the movie-map lookup in BuildEligibleCoreAsync misses its
            // (disjoint) id → null, exactly as the old per-row subquery returned.
            return MiscQuery(filter).Select(mv => new Cand(
                mv.PlayableId,
                mv.Playable.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null && f.DurationTicks != null)
                    .OrderBy(f => f.Role).ThenBy(f => f.Id).Select(f => (long?)f.DurationTicks).FirstOrDefault(),
                null,
                null, null, mv.MpaaRatingInferred,
                null, null,
                (long)mv.Id, 0, mv.SimpleTitle));
        }

        // Non-"Extra" misc videos (OVAs, specials, shorts, music videos, the odd mis-filed episode) that
        // belong to one of the given series — surfaced wherever that series airs and grouped with it. Bonus
        // features (Category "Extra") are excluded so deleted scenes and making-of clips never air.
        //
        // Ordering within the series rotation: a special has an air date but no episode number, so when we
        // know its year (backfilled into MiscVideo.Year from filename / mapped episode / TMDB) we slot it
        // CHRONOLOGICALLY — its OrderRank is that of the last regular episode that aired in or before that
        // year, so a 2014 Attack on Titan OVA sorts in among the 2014-era episodes. If the year predates every
        // episode (or the series' episodes carry no air dates) it falls back to (year-1900), ordering specials
        // among themselves by year near the start. When the year is unknown, we SPREAD the special across the
        // series' rotation (PlayableId % maxEpisodeRank) so it still surfaces periodically instead of clumping
        // at one end. Grouped by RelatedSeriesId.
        private IQueryable<Cand> RelatedMiscCandidates(List<int> seriesIds)
        {
            return movieDb.MiscVideos
                .Where(mv => mv.ReviewBatch == null
                    && mv.RelatedSeriesId != null && seriesIds.Contains(mv.RelatedSeriesId.Value)
                    && (mv.Category == null || mv.Category != "Extra")
                    && mv.Playable.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null))
                .Select(mv => new Cand(
                    mv.PlayableId,
                    mv.Playable.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null && f.DurationTicks != null)
                        .OrderBy(f => f.Role).ThenBy(f => f.Id).Select(f => (long?)f.DurationTicks).FirstOrDefault(),
                    null,
                    null, null, mv.MpaaRatingInferred,
                    null, null,
                    (long)mv.RelatedSeriesId!.Value * 1_000_000L + (mv.Year != null
                        ? (movieDb.Episodes
                            .Where(e => e.SeriesId == mv.RelatedSeriesId && e.AirDate != null && e.AirDate!.Value.Year <= mv.Year)
                            .Select(e => (long?)(e.SeasonNumber * 1000L + e.EpisodeNumber)).Max() ?? (long)(mv.Year.Value - 1900))
                        : (mv.PlayableId % (movieDb.Episodes
                            .Where(e => e.SeriesId == mv.RelatedSeriesId)
                            .Select(e => (long?)(e.SeasonNumber * 1000L + e.EpisodeNumber)).Max() ?? 1L))),
                    mv.RelatedSeriesId!.Value,
                    mv.SimpleTitle));
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

            var (_, ceiling) = await GetEligibleAsync(channel, cancel);
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
            if (cache.TryGetValue(CeilingKey(channel), out ceiling))
                return true;
            // Fall back to the persisted ceiling so a hot read (guide/list) stays cheap across restarts —
            // the in-memory cache is empty after a deploy, but the maintainer-stored value survives.
            if (channel.CachedCeiling is int persisted)
            {
                ceiling = persisted;
                return true;
            }
            return false;
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

        /// <summary>
        /// Read-only fast path for the hot poll endpoints (Now / single-channel Guide). Returns just the
        /// items overlapping [fromUtc, toUtc) as untracked rows — no prune scan, no generation gate, no
        /// full-schedule materialization. Only when the lineup doesn't already reach <paramref name="toUtc"/>
        /// (the background maintainer is behind, or the channel is brand new) does it fall back to a full
        /// <see cref="EnsureScheduleAsync"/> to extend + prune, then window the result.
        ///
        /// This matters because every viewer polls Now every ~10s: the old path re-loaded the channel's
        /// entire multi-day lineup as tracked entities (hundreds–thousands of rows on episode/shorts
        /// channels) and ran a stale-row prune on each of those beats.
        /// </summary>
        public async Task<List<ChannelScheduleItem>> GetReadWindowAsync(
            Channel channel, DateTime fromUtc, DateTime toUtc, CancellationToken cancel = default)
        {
            // Cheap scalar (indexed by (ChannelId, StartUtc)) telling us how far the lineup reaches.
            var maxEnd = await movieDb.ChannelScheduleItems
                .Where(i => i.ChannelId == channel.Id)
                .MaxAsync(i => (DateTime?)i.EndUtc, cancel);

            if (maxEnd == null || maxEnd < toUtc)
            {
                // Behind (or empty): extend to the standard horizon (also prunes), then window in memory.
                var all = await EnsureScheduleAsync(channel, DateTime.UtcNow.Add(ScheduleHorizon), cancel);
                return all.Where(i => i.EndUtc > fromUtc && i.StartUtc < toUtc)
                    .OrderBy(i => i.StartUtc).ToList();
            }

            return await movieDb.ChannelScheduleItems
                .AsNoTracking()
                .Where(i => i.ChannelId == channel.Id && i.EndUtc > fromUtc && i.StartUtc < toUtc)
                .OrderBy(i => i.StartUtc)
                .ToListAsync(cancel);
        }

        /// <summary>Enabled channel ids in display order — for the background maintainer's round-robin.
        /// Watch-party channels are excluded: their timeline must stay frozen in the lobby until the party
        /// presses Begin, so the maintainer must never materialize their lineup ahead. Paused channels are
        /// excluded for the same reason — their clock is stopped, so extending them would just pile up
        /// lineup that the resume shift has to push forward again.</summary>
        public Task<List<int>> EnabledChannelIdsAsync(CancellationToken cancel = default) =>
            movieDb.Channels.Where(c => c.Enabled && c.WatchpartyToken == null && c.PausedAtUtc == null)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .Select(c => c.Id).ToListAsync(cancel);

        /// <summary>
        /// Lift pauses older than <see cref="StalePauseAge"/>, so a channel frozen by someone who wandered
        /// off comes back to life on its own — no viewer has to tune in first (the tune-time path is
        /// ChannelController.TryAutoResumeAsync, which enforces the same age). Each resume slides the lineup
        /// forward by the freeze, exactly like a hand resume, so the channel picks up where it stopped
        /// rather than jumping to the live position.
        ///
        /// Watch parties are exempt: their freeze is the "wait until everyone's here" lobby gate, and an
        /// abandoned party is deleted outright by <see cref="WatchpartyReaperService"/> rather than started
        /// with nobody watching.
        ///
        /// Bounded per the long-job rule: at most <paramref name="limit"/> channels per call, oldest pause
        /// first, and each one is claimed with a compare-and-set so a viewer resuming in the same beat
        /// can't make the schedule shift twice. Returns how many were resumed.
        /// </summary>
        public async Task<int> ResumeStalePausesAsync(int limit, CancellationToken cancel = default)
        {
            var now = DateTime.UtcNow;
            var cutoff = now - StalePauseAge;

            // AsNoTracking: the claim below writes the pause columns straight through, so the row must not
            // also be sitting in the change tracker waiting to re-write them on the shift's SaveChanges.
            var stale = await movieDb.Channels
                .AsNoTracking()
                .Where(c => c.Enabled && c.WatchpartyToken == null && c.PausedAtUtc != null && c.PausedAtUtc < cutoff)
                .OrderBy(c => c.PausedAtUtc)
                .Take(limit)
                .ToListAsync(cancel);

            int resumed = 0;
            foreach (var channel in stale)
            {
                var pausedAt = channel.PausedAtUtc!.Value;
                int claimed = await movieDb.Channels
                    .Where(c => c.Id == channel.Id && c.PausedAtUtc == pausedAt)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.PausedAtUtc, (DateTime?)null)
                        .SetProperty(c => c.PausedByUserId, (int?)null), cancel);
                if (claimed != 1)
                    continue; // somebody resumed it first — theirs is the shift

                channel.PausedAtUtc = null;
                channel.PausedByUserId = null;
                await ShiftForResumeAsync(channel, now - pausedAt, cancel);
                resumed++;
                logger.LogInformation(
                    "Channel {ChannelId} ({Name}) resumed automatically after {Hours:F1}h paused.",
                    channel.Id, channel.Name, (now - pausedAt).TotalHours);
            }

            return resumed;
        }

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
            var ceiling = await GetCeilingAsync(channel, cancel);
            // Persist the ceiling so a restart (which empties the in-memory cache) doesn't re-scan to gate
            // visibility — readers fall back to this stored value. EnsureScheduleAsync just warmed the
            // eligible set, so GetCeilingAsync here is a cache hit, not a fresh scan.
            if (channel.CachedCeiling != ceiling)
            {
                channel.CachedCeiling = ceiling;
                await movieDb.SaveChangesAsync(cancel);
            }
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

            // Prune against the channel's own clock: a paused channel is frozen at PausedAtUtc, so measuring
            // staleness by the wall clock would eventually delete the very item it's holding on (and a
            // channel with no current item reads as "not paused" to everyone watching).
            var pruneBefore = (channel.PausedAtUtc ?? now) - PruneAge;
            var stale = await movieDb.ChannelScheduleItems
                .Where(i => i.ChannelId == channel.Id && i.EndUtc < pruneBefore)
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
                var (eligible, _) = await GetEligibleAsync(channel, cancel);
                if (eligible.Count > 0)
                {
                    var strategy = EffectiveStrategy(channel);
                    // Ordered strategies (marathon / round-robin / release / newest) are intentionally
                    // sequenced, so no anti-repeat cooldown; shuffles get one bounded by the pool size.
                    bool ordered = strategy is "Marathon" or "EpisodeRoundRobin" or "ReleaseDate" or "NewestFirst" or "Playlist";
                    int cooldownK = ordered ? 0 : Math.Min(100, Math.Max(1, eligible.Count - 1));

                    // Resume from the already-materialized tail so each *incremental* extension continues the
                    // lineup instead of restarting at the head of the canonical sequence. In steady state the
                    // maintainer appends ~1 item per tick; the old code re-seeded `round` to 0 and `recent` to
                    // just the last item every call, so it kept re-picking that head — collapsing a channel to
                    // one looping episode (round-robin) or two ping-ponging films (weighted shuffle). Seed the
                    // cooldown from the real last K titles; the ordered resume-skip below covers the
                    // no-cooldown strategies.
                    var recent = new Queue<int>();
                    if (cooldownK > 0)
                        foreach (var prev in items.Skip(Math.Max(0, items.Count - cooldownK)))
                            recent.Enqueue(prev.PlayableId);

                    // Round-robin keeps a cursor PER SHOW instead of one flat sequence, so it resumes and
                    // wraps per show (see RoundRobinRotation) — the other strategies keep the flat-queue
                    // resume-skip below.
                    var rotation = strategy == "EpisodeRoundRobin" ? new RoundRobinRotation(eligible, items) : null;

                    var queue = new Queue<EligibleItem>();
                    int reroll = 0;       // shuffles regenerated within this call (disambiguates a tiny pool re-shuffling inside one rotation window)
                    bool resumed = rotation != null;   // the rotation resumes itself from the tail
                    while (cursor < horizonUtc)
                    {
                        if (queue.Count == 0)
                        {
                            // Shuffles re-roll on a time cadence (by slot start) so a large pool rotates
                            // through its whole catalog over days instead of cycling one window forever;
                            // ordered strategies ignore the round number.
                            int round = ordered ? 0 : unchecked((int)(cursor.Ticks / ShuffleRotation.Ticks) + reroll++);
                            queue = rotation?.NextRound() ?? GenerateRound(eligible, channel.Seed, round, strategy);
                        }

                        // First round of an ordered strategy (no cooldown to lean on): advance past the
                        // last-aired title so the rotation resumes where it left off — round-robin moves to
                        // the next show, marathon to the next entry — instead of replaying element 0 each tick.
                        if (ordered && !resumed)
                        {
                            resumed = true;
                            if (items.Count > 0)
                            {
                                int lastPid = items[^1].PlayableId;
                                while (queue.Count > 0 && queue.Peek().PlayableId != lastPid) queue.Dequeue();
                                if (queue.Count > 0) queue.Dequeue(); // drop the last-aired item itself
                            }
                            if (queue.Count == 0) continue; // round consumed by the skip; regenerate the next one
                        }

                        var pick = queue.Dequeue();
                        // Cooldown: defer a title that aired within the last K slots (bounded retries so a
                        // tiny pool can't spin). Seeded from the real tail above, so it holds across ticks.
                        for (int attempt = 0; cooldownK > 0 && queue.Count > 0 && attempt < queue.Count && recent.Contains(pick.PlayableId); attempt++)
                        {
                            queue.Enqueue(pick);
                            pick = queue.Dequeue();
                        }

                        var end = cursor.AddTicks(pick.DurationTicks);
                        var sched = new ChannelScheduleItem
                        {
                            ChannelId = channel.Id,
                            PlayableId = pick.PlayableId,
                            StartUtc = cursor,
                            EndUtc = end,
                        };
                        movieDb.ChannelScheduleItems.Add(sched);
                        items.Add(sched);
                        recent.Enqueue(pick.PlayableId);
                        while (recent.Count > cooldownK) recent.Dequeue();
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

            // The item airing at the moment of pause is the one whose original window still contained
            // that instant; shift it and everything later back into the future. Loaded directly (not via
            // EnsureScheduleAsync) so a resume never extends the lineup while the clock is still frozen —
            // the pause can have lasted days, and the maintainer will catch the horizon up right after.
            var pausedAt = DateTime.UtcNow - wasPausedFor;
            var items = await movieDb.ChannelScheduleItems
                .Where(i => i.ChannelId == channel.Id && i.EndUtc > pausedAt)
                .ToListAsync(cancel);

            foreach (var item in items)
            {
                item.StartUtc += wasPausedFor;
                item.EndUtc += wasPausedFor;
            }

            if (movieDb.ChangeTracker.HasChanges())
                await movieDb.SaveChangesAsync(cancel);
        }

        private static string EffectiveStrategy(Channel c)
            => !string.IsNullOrWhiteSpace(c.ScheduleStrategy) ? c.ScheduleStrategy!
               : (string.Equals(c.ShuffleMode, "ReleaseDate", StringComparison.OrdinalIgnoreCase) ? "ReleaseDate" : "SeededShuffle");

        // How many copies of an item go into a WeightedShuffle round — more for the rewatchable and the
        // well-rated, so they recur a little more often. Deterministic (no clock/random), 1..3.
        private static int Copies(EligibleItem e)
        {
            // Personalized channels weight by the recommendation score (0..100 → 1..4 copies), so the
            // best-fit picks recur more; every other channel weights by rewatchability × quality.
            if (e.Weight is double score)
                return Math.Clamp((int)Math.Round(1 + Math.Clamp(score, 0, 100) / 100.0 * 3), 1, 4);
            double rewatch = 0.5 + (e.Rewatchability ?? 50) / 100.0;   // 0.5 .. 1.5
            double quality = 0.5 + (e.Quality ?? 0.5);                 // 0.5 .. 1.5
            return Math.Clamp((int)Math.Round(rewatch * quality * 1.5), 1, 3);
        }

        // Deterministic Fisher-Yates so a regenerated round reproduces (rows are never rewritten, but
        // determinism keeps races harmless).
        private static void FisherYates(IList<EligibleItem> list, int seed)
        {
            var rng = new Random(seed);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // One round of the lineup for a strategy. Shuffles are deterministic (seed + round); ordered
        // strategies replay a stable sequence each round (the channel loops).
        private static Queue<EligibleItem> GenerateRound(List<EligibleItem> source, int seed, int round, string strategy)
        {
            switch (strategy)
            {
                case "Playlist":
                    // A user playlist airs in the exact order the user arranged (OrderRank = Position),
                    // looping. No shuffle, no weighting — their list, their order.
                    return new Queue<EligibleItem>(source.OrderBy(e => e.OrderRank));
                case "Marathon":
                    return new Queue<EligibleItem>(source.OrderBy(e => e.SortKey, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.OrderRank));
                case "ReleaseDate":
                    return new Queue<EligibleItem>(source.OrderBy(e => e.OrderRank));
                case "NewestFirst":
                    return new Queue<EligibleItem>(source.OrderByDescending(e => e.OrderRank));
                case "EpisodeRoundRobin":
                    // The scheduler drives round-robin through RoundRobinRotation (per-show cursors that
                    // resume and wrap); this is the history-free equivalent for any other caller.
                    return new RoundRobinRotation(source, Array.Empty<ChannelScheduleItem>()).NextRound();
                case "WeightedShuffle":
                case "RecommendationWeighted": // same weighted shuffle; Copies() reads the per-user score
                {
                    var bag = source.SelectMany(e => Enumerable.Repeat(e, Copies(e))).ToList();
                    FisherYates(bag, unchecked(seed * 31 + round));
                    return new Queue<EligibleItem>(bag);
                }
                default: // SeededShuffle
                {
                    var list = new List<EligibleItem>(source);
                    FisherYates(list, unchecked(seed * 31 + round));
                    return new Queue<EligibleItem>(list);
                }
            }
        }

        /// <summary>
        /// The <c>EpisodeRoundRobin</c> rotation: one cursor per show, so every round airs exactly one
        /// episode of every show and a show that reaches its finale WRAPS to its first episode rather
        /// than leaving the rotation. Short shows therefore loop sooner; no show ever goes off the air.
        ///
        /// <para>This replaces a single flat pass (<c>for i in 0..max: each group[i] if it has one</c>),
        /// whose tail was necessarily a solid block of the longest show — every other series had been
        /// exhausted. Left running, that made Primetime Animation air 466 consecutive Simpsons episodes
        /// (~7 days) while Futurama, Daria, Duckman, Beavis, Clone High and The Critic never came up, and
        /// collapsed Classic Sitcoms to Seinfeld / All in the Family with seven other shows benched. The
        /// same fate was queued up for Cartoon Shorts (Looney Tunes), Read &amp; Learn and Preschool
        /// (Mister Rogers) and Nickelodeon (SpongeBob).</para>
        /// </summary>
        private sealed class RoundRobinRotation
        {
            private readonly List<List<EligibleItem>> shows;
            private readonly int[] cursors;
            private readonly int lead;   // which show opens every round (set by the resume)

            public RoundRobinRotation(List<EligibleItem> source, IReadOnlyList<ChannelScheduleItem> aired)
            {
                // Ordered by series id, then by episode order, so the rotation is stable across calls
                // (GroupBy's own ordering is only as stable as the candidate query's).
                shows = source.GroupBy(e => e.GroupId).OrderBy(g => g.Key)
                    .Select(g => g.OrderBy(e => e.OrderRank).ToList())
                    .ToList();
                cursors = new int[shows.Count];

                // Resume: replay the materialized tail so each show picks up after ITS last-aired episode
                // and the next round opens with the show after the one that aired last. A round covers
                // every show, so the retained tail always spans at least one full round.
                var placed = new Dictionary<int, (int Show, int Index)>();
                for (int s = 0; s < shows.Count; s++)
                    for (int i = 0; i < shows[s].Count; i++)
                        placed[shows[s][i].PlayableId] = (s, i);
                foreach (var item in aired)
                    if (placed.TryGetValue(item.PlayableId, out var at))
                    {
                        cursors[at.Show] = at.Index + 1;
                        lead = at.Show + 1;
                    }
            }

            /// <summary>One episode of every show, in rotation order.</summary>
            public Queue<EligibleItem> NextRound()
            {
                var round = new Queue<EligibleItem>();
                for (int i = 0; i < shows.Count; i++)
                {
                    int s = (lead + i) % shows.Count;
                    var show = shows[s];
                    round.Enqueue(show[cursors[s] % show.Count]);
                    cursors[s]++;
                }
                return round;
            }
        }
    }
}
