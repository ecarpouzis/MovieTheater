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
            string SortKey);

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

        // Per-kind SQL projection: enough to resolve duration, the effective rating (precedence
        // A→B→C), the quality/rewatch weight inputs, and the order/group keys.
        private sealed record Cand(
            int PlayableId, long? DurationTicks, int? RuntimeMinutes,
            string? RatingA, string? RatingB, string? RatingC,
            decimal? ImdbRating, int? Tomatometer, int? Rewatchability,
            long OrderRank, int GroupId, string? SortKey);

        /// <summary>
        /// Builds the channel's eligible set across the kinds its filter allows (movies, series→episodes,
        /// misc), applying every predicate in SQL, then resolving duration + effective rating + ceiling in
        /// memory. Eligible items have a synced, present file and a known duration.
        /// </summary>
        private async Task<(List<EligibleItem> Items, int Ceiling)> BuildEligibleCoreAsync(Channel channel, CancellationToken cancel)
        {
            var filter = ChannelFilter.Parse(channel.FilterJson);

            var cands = new List<Cand>();
            if (filter.Kinds.HasFlag(ContentKinds.Movies))
                cands.AddRange(await MovieCandidates(filter).ToListAsync(cancel));
            if (filter.Kinds.HasFlag(ContentKinds.Series))
                cands.AddRange(await EpisodeCandidates(filter).ToListAsync(cancel));
            if (filter.Kinds.HasFlag(ContentKinds.Misc))
                cands.AddRange(await MiscCandidates(filter).ToListAsync(cancel));

            // Real-bucket (1..6) rating map, resolved in memory like the rest of the age gate (RatingGate).
            var ratingRows = await movieDb.RatingMaps
                .Where(rm => rm.MovieRating != null && rm.MPARatingID >= 1 && rm.MPARatingID <= 6)
                .Select(rm => new { rm.MovieRating, rm.MPARatingID })
                .ToListAsync(cancel);
            var ratingMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ratingRows)
                if (r.MovieRating != null) ratingMap[r.MovieRating.Trim()] = r.MPARatingID;

            // Adult buckets (NC-17 / X): MinAge >= 18. Dropped when filter.ExcludeAdult (default true).
            var adultIds = new HashSet<int>(await movieDb.RatingMpas
                .Where(rm => rm.MinAge >= 18).Select(rm => rm.RatingID).ToListAsync(cancel));

            int Effective(string? a, string? b, string? c)
            {
                foreach (var t in new[] { a, b, c })
                    if (!string.IsNullOrWhiteSpace(t) && ratingMap.TryGetValue(t!.Trim(), out var id))
                        return id;
                return RatingGate.UnknownRatingId; // 7 = conservative (adults only)
            }

            var items = new List<EligibleItem>(cands.Count);
            int maxRating = 0;
            foreach (var c in cands)
            {
                long durationTicks = c.DurationTicks
                    ?? (c.RuntimeMinutes is int min && min > 0 ? (long)min * 60 * TicksPerSecond : 0);
                if (durationTicks <= 0)
                    continue; // §8: skip items with neither a file duration nor a runtime

                int ratingId = Effective(c.RatingA, c.RatingB, c.RatingC);
                if (filter.MaxMpaRatingId is int max && ratingId > max)
                    continue; // a capped channel excludes over-rated and unknown titles
                if (filter.ExcludeAdult && adultIds.Contains(ratingId))
                    continue; // NC-17 / X excluded by default

                double? quality = c.ImdbRating.HasValue ? (double)c.ImdbRating.Value / 10.0
                    : (c.Tomatometer.HasValue ? c.Tomatometer.Value / 100.0 : (double?)null);

                if (ratingId > maxRating) maxRating = ratingId;
                items.Add(new EligibleItem(c.PlayableId, durationTicks, ratingId,
                    c.Rewatchability, quality, c.OrderRank, c.GroupId, c.SortKey ?? ""));
            }

            int ceiling = filter.MaxMpaRatingId ?? (maxRating == 0 ? RatingGate.UnknownRatingId : maxRating);
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
            if (filter.ExcludeLanguages.Count > 0) { var ex = filter.ExcludeLanguages; q = q.Where(m => m.OriginalLanguage == null || !ex.Contains(m.OriginalLanguage)); }
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

            if (filter.UnwatchedByUserId is int uid) q = q.Where(m => !movieDb.Viewings.Any(v => v.UserID == uid && v.MovieID == m.id && v.ViewingType == "Seen"));
            if (filter.WantedByUserId is int wid) q = q.Where(m => movieDb.Viewings.Any(v => v.UserID == wid && v.MovieID == m.id && v.ViewingType == "WantToWatch"));
            if (filter.MinViewers is int minv) q = q.Where(m => movieDb.Viewings.Where(v => v.MovieID == m.id && v.ViewingType == "Seen").Select(v => v.UserID).Distinct().Count() >= minv);
            if (filter.AddedWithinDays is int days) { var since = DateTime.UtcNow.AddDays(-days); q = q.Where(m => m.UploadedDate != null && m.UploadedDate >= since); }

            foreach (var (ids, neg) in AiFilters(filter, InsightSubjectKind.Movie))
                q = neg ? q.Where(m => !ids.Contains(m.id)) : q.Where(m => ids.Contains(m.id));

            return q;
        }

        private IQueryable<Cand> MovieCandidates(ChannelFilter filter)
        {
            var cur = CurrentInsights(InsightSubjectKind.Movie);
            return MovieQuery(filter).Select(m => new Cand(
                m.PlayableId!.Value,
                m.Playable!.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null).Select(f => f.DurationTicks).FirstOrDefault(),
                m.RuntimeMinutes,
                m.MpaaRating, m.Rating, m.MpaaRatingInferred,
                m.ImdbRatingScraped, m.RtTomatometer,
                cur.Where(ti => ti.SubjectId == m.id).Select(ti => (int?)ti.Rewatchability).FirstOrDefault(),
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
            if (filter.ExcludeLanguages.Count > 0) { var ex = filter.ExcludeLanguages; q = q.Where(s => s.OriginalLanguage == null || !ex.Contains(s.OriginalLanguage)); }
            if (filter.Countries.Count > 0) { var cos = filter.Countries; q = q.Where(s => s.Country != null && cos.Any(c => s.Country.Contains(c))); }

            if (filter.Networks.Count > 0) { var nets = filter.Networks; q = q.Where(s => s.Network != null && nets.Any(n => s.Network.Contains(n))); }

            foreach (var cr in filter.Credits)
            {
                if (cr.PersonIds.Count == 0) continue;
                var pids = cr.PersonIds; var role = cr.Role;
                q = q.Where(s => s.Credits.Any(c => pids.Contains(c.PersonId) && (role == null || c.Role == role)));
            }

            // A series matches a path channel (e.g. "Looney Tunes") when any of its episodes' files do.
            if (filter.PathContains.Count > 0) { var pats = filter.PathContains; q = q.Where(s => s.Episodes.Any(e => e.Playable != null && e.Playable.Files.Any(f => pats.Any(p => f.Path.Contains(p))))); }

            if (filter.UnwatchedByUserId is int uid) q = q.Where(s => !movieDb.Viewings.Any(v => v.UserID == uid && v.SeriesId == s.Id && v.ViewingType == "Seen"));
            if (filter.WantedByUserId is int wid) q = q.Where(s => movieDb.Viewings.Any(v => v.UserID == wid && v.SeriesId == s.Id && v.ViewingType == "WantToWatch"));
            if (filter.MinViewers is int minv) q = q.Where(s => movieDb.Viewings.Where(v => v.SeriesId == s.Id && v.ViewingType == "Seen").Select(v => v.UserID).Distinct().Count() >= minv);
            if (filter.AddedWithinDays is int days) { var since = DateTime.UtcNow.AddDays(-days); q = q.Where(s => s.UploadedDate != null && s.UploadedDate >= since); }

            foreach (var (ids, neg) in AiFilters(filter, InsightSubjectKind.Series))
                q = neg ? q.Where(s => !ids.Contains(s.Id)) : q.Where(s => ids.Contains(s.Id));

            return q;
        }

        private IQueryable<Cand> EpisodeCandidates(ChannelFilter filter)
        {
            var sq = SeriesQuery(filter);
            var cur = CurrentInsights(InsightSubjectKind.Series);
            return movieDb.Episodes
                .Where(e => e.SeriesId != null && e.PlayableId != null
                    && sq.Any(s => s.Id == e.SeriesId)
                    && e.Playable!.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null))
                .Select(e => new Cand(
                    e.PlayableId!.Value,
                    e.Playable!.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null).Select(f => f.DurationTicks).FirstOrDefault(),
                    e.RuntimeMinutes,
                    e.Series!.MpaaRating, e.Series.Rating, e.Series.MpaaRatingInferred,
                    e.Series.ImdbRatingScraped, e.Series.RtTomatometer,
                    cur.Where(ti => ti.SubjectId == e.SeriesId).Select(ti => (int?)ti.Rewatchability).FirstOrDefault(),
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

            if (filter.PathContains.Count > 0) { var pats = filter.PathContains; q = q.Where(mv => mv.Playable.Files.Any(f => pats.Any(p => f.Path.Contains(p)))); }

            if (filter.UnwatchedByUserId is int uid) q = q.Where(mv => !movieDb.Viewings.Any(v => v.UserID == uid && v.MiscVideoId == mv.Id && v.ViewingType == "Seen"));

            foreach (var (ids, neg) in AiFilters(filter, InsightSubjectKind.MiscVideo))
                q = neg ? q.Where(mv => !ids.Contains(mv.Id)) : q.Where(mv => ids.Contains(mv.Id));

            return q;
        }

        private IQueryable<Cand> MiscCandidates(ChannelFilter filter)
        {
            return MiscQuery(filter).Select(mv => new Cand(
                mv.PlayableId,
                mv.Playable.Files.Where(f => f.JellyfinItemId != null && f.MissingSinceUtc == null).Select(f => f.DurationTicks).FirstOrDefault(),
                null,
                null, null, mv.MpaaRatingInferred,
                null, null, null,
                (long)mv.Id, 0, mv.SimpleTitle));
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
                var (eligible, _) = await GetEligibleAsync(channel, cancel);
                if (eligible.Count > 0)
                {
                    var strategy = EffectiveStrategy(channel);
                    // Ordered strategies (marathon / round-robin / release / newest) are intentionally
                    // sequenced, so no anti-repeat cooldown; shuffles get one bounded by the pool size.
                    bool ordered = strategy is "Marathon" or "EpisodeRoundRobin" or "ReleaseDate" or "NewestFirst";
                    int cooldownK = ordered ? 0 : Math.Min(100, Math.Max(1, eligible.Count - 1));
                    var recent = new Queue<int>();
                    if (items.Count > 0) recent.Enqueue(items[^1].PlayableId);

                    int round = 0;
                    var queue = new Queue<EligibleItem>();
                    while (cursor < horizonUtc)
                    {
                        if (queue.Count == 0)
                            queue = GenerateRound(eligible, channel.Seed, round++, strategy);

                        var pick = queue.Dequeue();
                        // Cooldown: defer a title that aired within the last K slots (bounded retries so a
                        // tiny pool can't spin).
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

        private static string EffectiveStrategy(Channel c)
            => !string.IsNullOrWhiteSpace(c.ScheduleStrategy) ? c.ScheduleStrategy!
               : (string.Equals(c.ShuffleMode, "ReleaseDate", StringComparison.OrdinalIgnoreCase) ? "ReleaseDate" : "SeededShuffle");

        // How many copies of an item go into a WeightedShuffle round — more for the rewatchable and the
        // well-rated, so they recur a little more often. Deterministic (no clock/random), 1..3.
        private static int Copies(EligibleItem e)
        {
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
                case "Marathon":
                    return new Queue<EligibleItem>(source.OrderBy(e => e.SortKey, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.OrderRank));
                case "ReleaseDate":
                    return new Queue<EligibleItem>(source.OrderBy(e => e.OrderRank));
                case "NewestFirst":
                    return new Queue<EligibleItem>(source.OrderByDescending(e => e.OrderRank));
                case "EpisodeRoundRobin":
                    return new Queue<EligibleItem>(RoundRobin(source));
                case "WeightedShuffle":
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

        // Interleave items across their groups (series) one per pass, so a TV channel rotates between
        // shows in episode order rather than bingeing a single series.
        private static List<EligibleItem> RoundRobin(List<EligibleItem> source)
        {
            var groups = source.GroupBy(e => e.GroupId)
                .Select(g => g.OrderBy(e => e.OrderRank).ToList())
                .ToList();
            var result = new List<EligibleItem>(source.Count);
            int max = groups.Count == 0 ? 0 : groups.Max(g => g.Count);
            for (int i = 0; i < max; i++)
                foreach (var g in groups)
                    if (i < g.Count) result.Add(g[i]);
            return result;
        }
    }
}
