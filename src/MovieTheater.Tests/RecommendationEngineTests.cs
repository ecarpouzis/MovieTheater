using System.Collections.Generic;
using System.Linq;
using MovieTheater.Db;
using MovieTheater.Services.Recommendations;
using Xunit;

namespace MovieTheater.Tests
{
    public class RecommendationEngineTests
    {
        private static readonly RecommendationEngine Engine = new();

        private static TitleFeatures T(int id, string? title = null, (string key, double pres)[]? feats = null,
            double? imdb = null, double? tomato = null, double? energy = null, IEnumerable<string>? comps = null)
            => new()
            {
                SubjectId = id,
                Kind = InsightSubjectKind.Movie,
                Title = title ?? $"t{id}",
                Features = feats?.ToDictionary(f => f.key, f => f.pres) ?? new Dictionary<string, double>(),
                ImdbRating = imdb,
                RtTomato = tomato,
                Energy = energy,
                CompTitles = comps?.ToList() ?? new List<string>(),
            };

        private static RatedTitle R(TitleFeatures f, double score) => new() { Features = f, Score = score, RecencyRank = 0 };

        // Filler titles carrying a feature, to inflate its document frequency (for IDF tests).
        private static IEnumerable<TitleFeatures> Filler(int startId, int count, string featureKey)
            => Enumerable.Range(startId, count).Select(i => T(i, feats: new[] { (featureKey, 1.0) }));

        [Fact]
        public void Affinity_is_signed_liked_features_positive_disliked_negative()
        {
            var lib = new List<TitleFeatures>();
            lib.AddRange(Filler(1000, 40, "tag:Subgenre:good"));
            lib.AddRange(Filler(2000, 40, "tag:Subgenre:bad"));
            var stats = RecommendationEngine.BuildLibraryStats(lib);

            var ratings = new List<RatedTitle>
            {
                R(T(1, feats: new[] { ("tag:Subgenre:good", 1.0) }), 90),
                R(T(2, feats: new[] { ("tag:Subgenre:good", 1.0) }), 88),
                R(T(3, feats: new[] { ("tag:Subgenre:good", 1.0) }), 92),
                R(T(4, feats: new[] { ("tag:Subgenre:bad", 1.0) }), 40),
                R(T(5, feats: new[] { ("tag:Subgenre:bad", 1.0) }), 35),
                R(T(6, feats: new[] { ("tag:Subgenre:bad", 1.0) }), 45),
            };

            var p = Engine.BuildProfile(ratings, stats);

            Assert.True(p.Features["tag:Subgenre:good"].Weight > 0, "liked feature should be positive");
            Assert.True(p.Features["tag:Subgenre:bad"].Weight < 0, "disliked feature should be negative");
        }

        [Fact]
        public void Idf_ranks_a_rarer_feature_above_a_common_one_at_equal_affinity()
        {
            // Both features appear on the SAME rated titles (equal affinity), but "common" is everywhere in
            // the library and "rare" is not — so IDF must give "rare" the larger weight.
            var lib = new List<TitleFeatures>();
            lib.AddRange(Filler(1000, 200, "tag:Subgenre:common")); // high df
            lib.AddRange(Filler(2000, 3, "tag:Subgenre:rare"));     // low df
            var stats = RecommendationEngine.BuildLibraryStats(lib);

            var both = new[] { ("tag:Subgenre:common", 1.0), ("tag:Subgenre:rare", 1.0) };
            var ratings = new List<RatedTitle>
            {
                R(T(1, feats: both), 95),
                R(T(2, feats: both), 90),
                R(T(3, feats: both), 92),
                R(T(4, feats: new[] { ("tag:Subgenre:neutral", 1.0) }), 60),
            };

            var p = Engine.BuildProfile(ratings, stats);

            Assert.True(p.Features["tag:Subgenre:rare"].Weight > p.Features["tag:Subgenre:common"].Weight,
                "the rarer feature should outweigh the common one at equal affinity");
        }

        [Fact]
        public void Shrinkage_trusts_a_feature_seen_many_times_more_than_one_seen_once()
        {
            var lib = new List<TitleFeatures>();
            lib.AddRange(Filler(1000, 20, "tag:Subgenre:many"));
            lib.AddRange(Filler(2000, 20, "tag:Subgenre:once"));
            var stats = RecommendationEngine.BuildLibraryStats(lib);

            // Same per-title deviation (+high) for both features, but "many" appears 5×, "once" appears 1×.
            var ratings = new List<RatedTitle>
            {
                R(T(1, feats: new[] { ("tag:Subgenre:many", 1.0) }), 90),
                R(T(2, feats: new[] { ("tag:Subgenre:many", 1.0) }), 90),
                R(T(3, feats: new[] { ("tag:Subgenre:many", 1.0) }), 90),
                R(T(4, feats: new[] { ("tag:Subgenre:many", 1.0) }), 90),
                R(T(5, feats: new[] { ("tag:Subgenre:many", 1.0) }), 90),
                R(T(6, feats: new[] { ("tag:Subgenre:once", 1.0) }), 90),
                R(T(7, feats: new[] { ("tag:Subgenre:neutral", 1.0) }), 50),
            };

            var p = Engine.BuildProfile(ratings, stats);

            // Affinity excludes IDF, isolating the shrinkage effect.
            Assert.True(p.Features["tag:Subgenre:many"].Affinity > p.Features["tag:Subgenre:once"].Affinity,
                "a feature seen many times should be trusted more than one seen once");
        }

        [Fact]
        public void Recommend_ranks_a_candidate_sharing_a_loved_feature_first()
        {
            var lib = new List<TitleFeatures>();
            lib.AddRange(Filler(1000, 30, "tag:Subgenre:noir"));
            lib.AddRange(Filler(2000, 30, "tag:Subgenre:romcom"));
            var stats = RecommendationEngine.BuildLibraryStats(lib);

            var ratings = new List<RatedTitle>
            {
                R(T(1, feats: new[] { ("tag:Subgenre:noir", 1.0) }), 95),
                R(T(2, feats: new[] { ("tag:Subgenre:noir", 1.0) }), 92),
                R(T(3, feats: new[] { ("tag:Subgenre:romcom", 1.0) }), 40),
            };

            var onTaste = T(10, "Noir Pick", new[] { ("tag:Subgenre:noir", 1.0) });
            var offTaste = T(11, "RomCom Pick", new[] { ("tag:Subgenre:romcom", 1.0) });
            var recs = Engine.Recommend(ratings, new[] { offTaste, onTaste }, stats, topN: 2);

            var noir = recs.First(r => r.SubjectId == 10);
            var romRank = recs.First(r => r.SubjectId == 11).Rank;
            Assert.True(noir.Rank < romRank, "the on-taste candidate should rank ahead of the off-taste one");
            Assert.True(noir.SignalCount >= 1, "an on-taste pick should report at least one contributing signal");
        }

        [Fact]
        public void CompTitle_match_boosts_an_otherwise_equal_candidate()
        {
            var lib = new List<TitleFeatures>();
            lib.AddRange(Filler(1000, 30, "tag:Subgenre:noir"));
            var stats = RecommendationEngine.BuildLibraryStats(lib);

            // The user loved "Heat".
            var ratings = new List<RatedTitle>
            {
                R(T(1, "Heat", new[] { ("tag:Subgenre:noir", 1.0) }), 96),
                R(T(2, "Some Drama", new[] { ("tag:Subgenre:noir", 1.0) }), 90),
            };

            var comp = T(10, "Comp Match", new[] { ("tag:Subgenre:noir", 1.0) }, comps: new[] { "Heat" });
            var plain = T(11, "No Comp", new[] { ("tag:Subgenre:noir", 1.0) });
            var recs = Engine.Recommend(ratings, new[] { plain, comp }, stats, topN: 2);

            Assert.True(recs.First(r => r.SubjectId == 10).Rank < recs.First(r => r.SubjectId == 11).Rank,
                "a candidate that comps a loved title should be boosted");
            Assert.Contains(recs.First(r => r.SubjectId == 10).ReasonKeys, k => k.StartsWith("comp:"));
        }

        [Fact]
        public void ColdStart_with_no_ratings_falls_back_to_acclaim()
        {
            var lib = new List<TitleFeatures>
            {
                T(10, "Acclaimed", imdb: 9.0, tomato: 95),
                T(11, "Panned", imdb: 3.0, tomato: 20),
            };
            var stats = RecommendationEngine.BuildLibraryStats(lib);

            var recs = Engine.Recommend(new List<RatedTitle>(), lib, stats, topN: 2);

            Assert.Equal(10, recs.First(r => r.Rank == 0).SubjectId);
        }

        [Fact]
        public void Contrarian_gets_negative_acclaim_affinity()
        {
            // Rates the poorly-reviewed high and the acclaimed low.
            var lib = new List<TitleFeatures>();
            lib.AddRange(Enumerable.Range(1000, 20).Select(i => T(i, imdb: 5.0 + (i % 5), tomato: 40 + (i % 40))));
            var stats = RecommendationEngine.BuildLibraryStats(lib);

            var ratings = new List<RatedTitle>
            {
                R(T(1, imdb: 3.0, tomato: 20), 95),
                R(T(2, imdb: 3.5, tomato: 25), 90),
                R(T(3, imdb: 4.0, tomato: 30), 92),
                R(T(4, imdb: 9.0, tomato: 95), 30),
                R(T(5, imdb: 8.5, tomato: 92), 35),
                R(T(6, imdb: 8.8, tomato: 90), 25),
            };

            var p = Engine.BuildProfile(ratings, stats);
            Assert.True(p.AcclaimAffinity < 0, "a contrarian should have negative acclaim affinity");
        }

        [Fact]
        public void RenderReason_humanizes_keys_and_notes_the_aggregate()
        {
            var text = RecommendationEngine.RenderReason(
                new[] { "tag:Subgenre:neo-noir", "dir:42", "tag:Mood:melancholic" },
                id => id == 42 ? "Michael Mann" : null,
                totalSignals: 30);

            Assert.Contains("neo-noir", text);
            Assert.Contains("Michael Mann", text);
            Assert.Contains("melancholic", text);
            // The pick is a blend of many signals, so the copy must not imply only the named few explain it.
            Assert.Contains("27 more signals", text);
        }
    }
}
