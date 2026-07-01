using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Channels;
using MovieTheater.Db;
using MovieTheater.Services.Recommendations;

namespace MovieTheater.Recommendations
{
    /// <summary>
    /// Shared engine-around-the-DB logic for personalized recommendations: turn the library into feature
    /// vectors, compute a user's recommendations, and persist them (recs + taste profile + the two "For
    /// You" channels + a schedule-tail refresh). Used by both the <c>compute-recommendations</c> CLI and
    /// the background <see cref="RecommendationMaintenanceService"/> so the loading/scoring lives in one place.
    /// </summary>
    public sealed class RecommendationRefresher
    {
        private const int TopActors = 6;

        private readonly RecommendationEngine engine;
        public RecommendationRefresher(RecommendationEngine? engine = null) => this.engine = engine ?? new RecommendationEngine();

        public int AlgoVersion => engine.Opt.AlgoVersion;

        public sealed record FeatureIndex(
            Dictionary<int, TitleFeatures> Movies, Dictionary<int, TitleFeatures> Series, LibraryStats Stats, int MaxLibId);

        public sealed record UserResult(
            TasteProfile Profile, int RatedUsed, int MovieCandidates, int SeriesCandidates,
            IReadOnlyList<Recommendation> MovieRecs, IReadOnlyList<Recommendation> SeriesRecs);

        // ── Staleness stamp ────────────────────────────────────────────────────────────────────────

        /// <summary>Fingerprint of a user's inputs: their latest rating + rating count, the library's max
        /// title id, and the algo version. Unchanged stamp ⇒ nothing to recompute.</summary>
        public async Task<string> StampAsync(MovieDb db, int userId, int maxLibId, CancellationToken cancel = default)
        {
            var agg = await db.Viewings
                .Where(v => v.UserID == userId && v.ViewingType == "Rated")
                .GroupBy(_ => 1)
                .Select(g => new { Max = g.Max(v => v.ViewingID), Count = g.Count() })
                .FirstOrDefaultAsync(cancel);
            return $"{agg?.Max ?? 0}:{agg?.Count ?? 0}:{maxLibId}:{AlgoVersion}";
        }

        /// <summary>Cheap library-growth fingerprint: the max title id across movies + series (all rows).
        /// Part of the staleness stamp so new library content makes every user's recs recomputable.</summary>
        public async Task<int> MaxLibIdAsync(MovieDb db, CancellationToken cancel = default)
        {
            int maxMovie = await db.Movies.MaxAsync(m => (int?)m.id, cancel) ?? 0;
            int maxSeries = await db.Series.MaxAsync(s => (int?)s.Id, cancel) ?? 0;
            return Math.Max(maxMovie, maxSeries);
        }

        /// <summary>Users with ratings whose stored profile stamp no longer matches — i.e. they've rated
        /// something new or the library grew. Returns each with its fresh stamp so the caller needn't
        /// recompute it. Cheap: no feature index is built.</summary>
        public async Task<List<(int UserId, string Stamp)>> StaleUsersAsync(MovieDb db, CancellationToken cancel = default)
        {
            int maxLibId = await MaxLibIdAsync(db, cancel);
            var raters = await db.Viewings.Where(v => v.ViewingType == "Rated")
                .Select(v => v.UserID).Distinct().ToListAsync(cancel);
            var stamps = await db.UserTasteProfiles.ToDictionaryAsync(p => p.UserId, p => p.RatingsStamp, cancel);
            var stale = new List<(int, string)>();
            foreach (var uid in raters.OrderBy(x => x))
            {
                var stamp = await StampAsync(db, uid, maxLibId, cancel);
                if (!stamps.TryGetValue(uid, out var have) || have != stamp) stale.Add((uid, stamp));
            }
            return stale;
        }

        // ── Compute ────────────────────────────────────────────────────────────────────────────────

        public async Task<UserResult> ComputeAsync(MovieDb db, FeatureIndex idx, int userId, int topN, CancellationToken cancel = default)
        {
            var viewings = await db.Viewings
                .Where(v => v.UserID == userId)
                .Select(v => new { v.ViewingID, v.ViewingType, v.MovieID, v.SeriesId, v.ViewingData })
                .ToListAsync(cancel);

            var excludedMovies = new HashSet<int>();
            var excludedSeries = new HashSet<int>();
            var ratedRaw = new List<(int id, bool movie, double score, int vid)>();
            foreach (var v in viewings)
            {
                if (v.ViewingType == "Rated" && int.TryParse(v.ViewingData, out var sc) && sc is >= 0 and <= 100)
                {
                    if (v.MovieID is int mid) ratedRaw.Add((mid, true, sc, v.ViewingID));
                    else if (v.SeriesId is int sid) ratedRaw.Add((sid, false, sc, v.ViewingID));
                }
                // Any engagement (Seen / WantToWatch / Rated) removes a title from "new discoveries".
                if (v.MovieID is int m) excludedMovies.Add(m);
                if (v.SeriesId is int s) excludedSeries.Add(s);
            }

            // Global recency ranking across both kinds (0 = most recently rated).
            var rated = new List<RatedTitle>();
            var ordered = ratedRaw.OrderByDescending(r => r.vid).ToList();
            for (int rank = 0; rank < ordered.Count; rank++)
            {
                var r = ordered[rank];
                var map = r.movie ? idx.Movies : idx.Series;
                if (map.TryGetValue(r.id, out var tf))
                    rated.Add(new RatedTitle { Features = tf, Score = r.score, RecencyRank = rank });
            }

            var profile = engine.BuildProfile(rated, idx.Stats);
            var movieCands = idx.Movies.Where(kv => !excludedMovies.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            var seriesCands = idx.Series.Where(kv => !excludedSeries.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            var movieRecs = engine.Rank(profile, movieCands, idx.Stats, topN);
            var seriesRecs = engine.Rank(profile, seriesCands, idx.Stats, topN);

            return new UserResult(profile, rated.Count, movieCands.Count, seriesCands.Count, movieRecs, seriesRecs);
        }

        // ── Persist ────────────────────────────────────────────────────────────────────────────────

        /// <summary>Write the user's recs + taste profile, ensure their two "For You" channels exist, and
        /// drop the future schedule tail of those channels so the fresh picks start airing within minutes.</summary>
        public async Task PersistAsync(MovieDb db, int userId, UserResult r, string stamp, CancellationToken cancel = default)
        {
            var nameOf = await PersonResolverAsync(db, r.MovieRecs.Concat(r.SeriesRecs), cancel);
            var now = DateTime.UtcNow;

            var old = await db.TitleRecommendations.Where(x => x.UserId == userId).ToListAsync(cancel);
            if (old.Count > 0) db.TitleRecommendations.RemoveRange(old);

            foreach (var rec in r.MovieRecs.Concat(r.SeriesRecs))
                db.TitleRecommendations.Add(new TitleRecommendation
                {
                    UserId = userId,
                    SubjectKind = rec.Kind,
                    SubjectId = rec.SubjectId,
                    Score = rec.Score,
                    Rank = rec.Rank,
                    ReasonText = RecommendationEngine.RenderReason(rec.ReasonKeys, nameOf, rec.SignalCount),
                    AlgoVersion = AlgoVersion,
                    GeneratedUtc = now,
                });

            var profile = await db.UserTasteProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancel);
            string json = JsonSerializer.Serialize(new
            {
                r.Profile.MeanRating,
                r.Profile.RatingCount,
                r.Profile.PersonalizationWeight,
                r.Profile.AcclaimAffinity,
                TopSignature = r.Profile.TopSignature.Select(kv => new { Feature = kv.Key, kv.Value }),
                Sliders = r.Profile.Sliders.Select(s => new { s.Name, s.Center, s.Importance }),
            });
            if (profile == null)
                db.UserTasteProfiles.Add(new UserTasteProfile { UserId = userId, ProfileJson = json, RatingsStamp = stamp, GeneratedUtc = now });
            else { profile.ProfileJson = json; profile.RatingsStamp = stamp; profile.GeneratedUtc = now; }

            EnsureRecoChannels(db, userId);
            await db.SaveChangesAsync(cancel);

            await DropScheduleTailAsync(db, userId, cancel);
        }

        // Drop the not-yet-aired schedule of the user's reco channels so the maintainer regenerates from the
        // refreshed pool (mirrors what ChannelAdminController.Save does on a filter change). The currently-
        // airing item is kept so playback isn't interrupted.
        private static async Task DropScheduleTailAsync(MovieDb db, int userId, CancellationToken cancel)
        {
            var channelIds = await db.Channels.Where(c => c.OwnerUserId == userId).Select(c => c.Id).ToListAsync(cancel);
            if (channelIds.Count == 0) return;
            var now = DateTime.UtcNow;
            var tail = await db.ChannelScheduleItems
                .Where(i => channelIds.Contains(i.ChannelId) && i.StartUtc > now)
                .ToListAsync(cancel);
            if (tail.Count == 0) return;
            db.ChannelScheduleItems.RemoveRange(tail);
            await db.SaveChangesAsync(cancel);
        }

        private static void EnsureRecoChannels(MovieDb db, int userId)
        {
            var have = new HashSet<string>(
                db.Channels.Where(c => c.OwnerUserId == userId).Select(c => c.Name).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var rng = new Random();
            void Ensure(string name, string desc, ContentKinds kinds, int sort)
            {
                if (have.Contains(name)) return;
                var filter = new ChannelFilter { Kinds = kinds, RecommendedForUserId = userId, UnwatchedByUserId = userId };
                db.Channels.Add(new Channel
                {
                    Name = name,
                    Description = desc,
                    OwnerUserId = userId,
                    Category = "For You",
                    Enabled = true,
                    CatalogKey = null,
                    FilterJson = filter.ToJson(),
                    ScheduleStrategy = "RecommendationWeighted",
                    ShuffleMode = "SeededShuffle",
                    Seed = rng.Next(1, int.MaxValue),
                    AnchorUtc = DateTime.UtcNow,
                    SortOrder = sort,
                });
            }
            Ensure("For You: Movies", "Movies picked just for you, from your ratings.", ContentKinds.Movies, 0);
            Ensure("For You: Shows", "Shows picked just for you, from your ratings.", ContentKinds.Series, 1);
        }

        public Task<Func<int, string?>> PersonResolverAsync(MovieDb db, IEnumerable<Recommendation> recs, CancellationToken cancel = default)
            => PersonResolverForKeysAsync(db, recs.SelectMany(r => r.ReasonKeys), cancel);

        /// <summary>Resolve a person id → display name for any reason/feature keys (recs and/or the taste
        /// profile's signature features), so the dossier and stored reasons both read with real names.</summary>
        public async Task<Func<int, string?>> PersonResolverForKeysAsync(MovieDb db, IEnumerable<string> keys, CancellationToken cancel = default)
        {
            var ids = keys.Select(ParsePersonId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
            var names = ids.Count == 0
                ? new Dictionary<int, string>()
                : await db.People.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName ?? "", cancel);
            return id => names.TryGetValue(id, out var n) && n.Length > 0 ? n : null;
        }

        // ── Feature-map construction (library → TitleFeatures) ─────────────────────────────────────

        public async Task<FeatureIndex> BuildIndexAsync(MovieDb db, CancellationToken cancel = default)
        {
            var movieRaw = await db.Movies
                .Where(m => m.ReviewBatch == null && m.PlayableId != null
                    && m.Playable!.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null))
                .Select(m => new { m.id, m.Title, D1 = m.ImdbReleaseDate, D2 = m.ReleaseDate, m.OriginalLanguage, m.ImdbRatingScraped, m.RtTomatometer, m.RtPopcornmeter, m.TmdbPopularity })
                .ToListAsync(cancel);
            var seriesRaw = await db.Series
                .Where(s => s.ReviewBatch == null
                    && s.Episodes.Any(e => e.PlayableId != null && e.Playable!.Files.Any(f => f.JellyfinItemId != null && f.MissingSinceUtc == null)))
                .Select(s => new { s.Id, s.Title, D1 = s.ImdbReleaseDate, D2 = s.ReleaseDate, s.OriginalLanguage, s.ImdbRatingScraped, s.RtTomatometer, s.RtPopcornmeter, s.TmdbPopularity })
                .ToListAsync(cancel);
            var movieRows = movieRaw.Select(m => new TitleCore { Id = m.id, Title = m.Title, Year = (m.D1 ?? m.D2)?.Year, Lang = m.OriginalLanguage, Imdb = (double?)m.ImdbRatingScraped, Tomato = m.RtTomatometer, Popcorn = m.RtPopcornmeter, Popularity = (double?)m.TmdbPopularity }).ToList();
            var seriesRows = seriesRaw.Select(s => new TitleCore { Id = s.Id, Title = s.Title, Year = (s.D1 ?? s.D2)?.Year, Lang = s.OriginalLanguage, Imdb = (double?)s.ImdbRatingScraped, Tomato = s.RtTomatometer, Popcorn = s.RtPopcornmeter, Popularity = (double?)s.TmdbPopularity }).ToList();
            var movieIds = movieRows.Select(r => r.Id).ToHashSet();
            var seriesIds = seriesRows.Select(r => r.Id).ToHashSet();

            var genreNames = await db.Genres.ToDictionaryAsync(g => g.Id, g => g.Name, cancel);
            var movieGenres = await db.MovieGenres.Select(g => new { g.MovieID, g.GenreId, g.Ordering }).ToListAsync(cancel);
            var seriesGenres = await db.SeriesGenres.Select(g => new { g.SeriesId, g.GenreId, g.Ordering }).ToListAsync(cancel);
            var mGenre = GroupFeatures(movieGenres.Where(g => movieIds.Contains(g.MovieID)), g => g.MovieID,
                g => ($"genre:{genreNames.GetValueOrDefault(g.GenreId, g.GenreId.ToString())}", g.Ordering == 0 ? 1.0 : 0.6));
            var sGenre = GroupFeatures(seriesGenres.Where(g => seriesIds.Contains(g.SeriesId)), g => g.SeriesId,
                g => ($"genre:{genreNames.GetValueOrDefault(g.GenreId, g.GenreId.ToString())}", g.Ordering == 0 ? 1.0 : 0.6));

            var movieCredits = await db.MovieCredits
                .Where(c => c.Role == CreditRole.Director || c.Role == CreditRole.Writer || c.Ordering < TopActors)
                .Select(c => new { c.MovieID, c.PersonId, c.Role, c.Ordering }).ToListAsync(cancel);
            var seriesCredits = await db.SeriesCredits
                .Where(c => c.Role == CreditRole.Director || c.Role == CreditRole.Writer || c.Ordering < TopActors)
                .Select(c => new { c.SeriesId, c.PersonId, c.Role, c.Ordering }).ToListAsync(cancel);
            var mCredit = GroupFeatures(movieCredits.Where(c => movieIds.Contains(c.MovieID)), c => c.MovieID, c => CreditFeature(c.Role, c.PersonId, c.Ordering));
            var sCredit = GroupFeatures(seriesCredits.Where(c => seriesIds.Contains(c.SeriesId)), c => c.SeriesId, c => CreditFeature(c.Role, c.PersonId, c.Ordering));

            var mInsight = await CurrentInsightsAsync(db, InsightSubjectKind.Movie, movieIds, cancel);
            var sInsight = await CurrentInsightsAsync(db, InsightSubjectKind.Series, seriesIds, cancel);

            var mViewers = await db.Viewings.Where(v => v.ViewingType == "Seen" && v.MovieID != null)
                .GroupBy(v => v.MovieID!.Value).Select(g => new { Id = g.Key, C = g.Select(v => v.UserID).Distinct().Count() })
                .ToDictionaryAsync(x => x.Id, x => x.C, cancel);
            var sViewers = await db.Viewings.Where(v => v.ViewingType == "Seen" && v.SeriesId != null)
                .GroupBy(v => v.SeriesId!.Value).Select(g => new { Id = g.Key, C = g.Select(v => v.UserID).Distinct().Count() })
                .ToDictionaryAsync(x => x.Id, x => x.C, cancel);

            var movies = movieRows.ToDictionary(r => r.Id, r => Build(r, InsightSubjectKind.Movie, mGenre, mCredit, mInsight, mViewers));
            var series = seriesRows.ToDictionary(r => r.Id, r => Build(r, InsightSubjectKind.Series, sGenre, sCredit, sInsight, sViewers));

            var stats = RecommendationEngine.BuildLibraryStats(movies.Values.Concat(series.Values).ToList());
            int maxLibId = await MaxLibIdAsync(db, cancel); // same basis the service uses for staleness stamps
            return new FeatureIndex(movies, series, stats, maxLibId);
        }

        private static TitleFeatures Build(
            TitleCore r, InsightSubjectKind kind,
            Dictionary<int, List<(string, double)>> genres, Dictionary<int, List<(string, double)>> credits,
            Dictionary<int, TitleInsight> insights, Dictionary<int, int> viewers)
        {
            var feats = new Dictionary<string, double>();
            void Put(string k, double v) { if (v > feats.GetValueOrDefault(k)) feats[k] = v; }

            foreach (var (k, v) in genres.GetValueOrDefault(r.Id) ?? Enumerable.Empty<(string, double)>()) Put(k, v);
            foreach (var (k, v) in credits.GetValueOrDefault(r.Id) ?? Enumerable.Empty<(string, double)>()) Put(k, v);
            if (r.Year is int y) Put($"decade:{y / 10 * 10}", 0.8);
            if (!string.IsNullOrWhiteSpace(r.Lang)) Put($"lang:{r.Lang}", 0.5);

            var comps = new List<string>();
            TitleInsight? ti = insights.GetValueOrDefault(r.Id);
            if (ti != null)
                foreach (var t in ti.Tags)
                {
                    if (t.Value == null) continue;
                    if (t.Category == TagCategory.CompTitle) { comps.Add(t.Value); continue; }
                    Put($"tag:{t.Category}:{t.Value}", (t.Weight ?? 60) / 100.0);
                }

            return new TitleFeatures
            {
                SubjectId = r.Id,
                Kind = kind,
                Title = r.Title,
                Features = feats,
                Surrealism = ti?.Surrealism, CultClassic = ti?.CultClassic, Intensity = ti?.Intensity,
                Novelty = ti?.Novelty, Rewatchability = ti?.Rewatchability, Energy = ti?.Energy,
                ImdbRating = r.Imdb, RtTomato = r.Tomato, RtPopcorn = r.Popcorn, Popularity = r.Popularity,
                Viewers = viewers.GetValueOrDefault(r.Id),
                CompTitles = comps,
            };
        }

        private static async Task<Dictionary<int, TitleInsight>> CurrentInsightsAsync(MovieDb db, InsightSubjectKind kind, HashSet<int> ids, CancellationToken cancel)
        {
            var all = await db.TitleInsights.AsNoTracking().Where(ti => ti.SubjectKind == kind).Include(ti => ti.Tags).ToListAsync(cancel);
            return all.Where(ti => ids.Contains(ti.SubjectId))
                .GroupBy(ti => ti.SubjectId)
                .ToDictionary(g => g.Key,
                    g => g.OrderByDescending(ti => ti.SpecVersion).ThenByDescending(ti => ti.GeneratedUtc).ThenByDescending(ti => ti.Id).First());
        }

        private static (string, double) CreditFeature(CreditRole role, int personId, int ordering) => role switch
        {
            CreditRole.Director => ($"dir:{personId}", 1.0),
            CreditRole.Writer => ($"wri:{personId}", 0.7),
            _ => ($"act:{personId}", Math.Max(0.4, 1.0 - 0.12 * ordering)),
        };

        private static Dictionary<int, List<(string, double)>> GroupFeatures<T>(
            IEnumerable<T> rows, Func<T, int> key, Func<T, (string, double)> feat)
        {
            var d = new Dictionary<int, List<(string, double)>>();
            foreach (var row in rows)
            {
                var k = key(row);
                if (!d.TryGetValue(k, out var list)) d[k] = list = new List<(string, double)>();
                list.Add(feat(row));
            }
            return d;
        }

        public static int? ParsePersonId(string reasonKey)
        {
            foreach (var p in new[] { "dir:", "act:", "wri:" })
                if (reasonKey.StartsWith(p, StringComparison.Ordinal) && int.TryParse(reasonKey[p.Length..], out var id))
                    return id;
            return null;
        }

        private sealed class TitleCore
        {
            public int Id { get; set; }
            public string? Title { get; set; }
            public int? Year { get; set; }
            public string? Lang { get; set; }
            public double? Imdb { get; set; }
            public double? Tomato { get; set; }
            public double? Popcorn { get; set; }
            public double? Popularity { get; set; }
        }
    }
}
