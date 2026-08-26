using System.Globalization;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-library-ratings</c> — the blend that produces `Rating(Source=Library)` for every series and
    /// every item, with a rationale note per row. The port of the standalone's `compute_library_ratings.py`
    /// (and the adjudications `apply_adjudications.py` wrote), weights unchanged.
    ///
    /// <para><b>The signals, and why each is weighted as it is:</b></para>
    /// <list type="bullet">
    /// <item><b>LOCG community rating</b> is the general anchor but is far too tightly packed to use raw — the
    /// whole site averages ~3.78/5 — so it is z-score-STRETCHED against the population mean and shrunk toward
    /// that mean for low-vote entries (Bayesian, K=30 per series, K=15 per issue). Its weight grows with the
    /// log of the vote count and tops out at 1.3.</item>
    /// <item><b>The current insight's rating</b> is weighted by its own confidence (High 0.9, Medium 0.5,
    /// Low 0.2, Unknown 0) — a model that says it is unsure gets believed less.</item>
    /// <item><b>MangaUpdates' bayesian rating</b> (0.8) for matched manga, and the <b>reader's own rating</b>
    /// (0.7) — a small, high-trust set.</item>
    /// <item><b>Award tags</b> are an upward BOOST after the weighted mean, capped at +5, never a weighted
    /// signal: a Pulitzer does not average against a vote count, it lifts.</item>
    /// <item>An item with no per-issue signal <b>carries its series rating</b> (0.8) — which is what makes an
    /// unrated issue of a great run sort like part of that run.</item>
    /// </list>
    ///
    /// <para><b>An override always wins.</b> A `Rating(Source=Override)` row replaces the computed value
    /// outright and its note is appended, so a hand adjudication survives every recompute. That is the whole
    /// reason overrides are their own SOURCE rather than an edit to the Library row.</para>
    ///
    /// <para>Chunked: the series pass first (bounded by the series count), then the items by `Item.Id`. The
    /// resolver's `ResolvedRating` is re-materialized afterwards — this job writes rows, never scalars.</para>
    /// </summary>
    public static class LibraryRatingJob
    {
        public const string DerivedName = "Rating(Source=Library)";
        public const string ModelId = "library-blend-v3";

        public const long SeriesCursor = 0;
        public const long ItemsBase = 1_000_000;

        /// <summary>Bayesian shrinkage constants: how many "average" votes a thin sample is pulled toward.</summary>
        public const int SeriesShrinkK = 30;
        public const int IssueShrinkK = 15;

        /// <summary>A rare LOCG row carries a rating with a NULL vote count; 10 is the site's own minimum.</summary>
        public const int DefaultVotes = 10;

        public static readonly Dictionary<Confidence, double> ConfidenceWeight = new()
        {
            [Confidence.High] = 0.9, [Confidence.Medium] = 0.5, [Confidence.Low] = 0.2, [Confidence.Unknown] = 0.0,
        };

        public static readonly Dictionary<string, int> AwardBoost = new(StringComparer.OrdinalIgnoreCase)
        {
            ["eisner-winner"] = 3, ["harvey-winner"] = 2, ["hugo-winner"] = 3, ["pulitzer"] = 4,
            ["will-eisner-hall-of-fame"] = 3, ["landmark-series"] = 3,
            ["critically-acclaimed"] = 2, ["cult-classic"] = 1,
            ["eisner-nominee"] = 1, ["harvey-nominee"] = 1,
        };

        /// <summary>The LOCG population the stretch is measured against — the hot subset's own mean and sigma.</summary>
        public sealed record Population(double Mean, double Sigma)
        {
            /// <summary>Stretch a (shrunk) 0–5 LOCG rating onto 0–100 by its z-score against the site average.</summary>
            public double To100(double rating) => Math.Clamp(62 + 16 * ((rating - Mean) / Sigma), 12, 97);
        }

        public static Population Measure(TargetWriter hot)
        {
            var mean = hot.Scalar<double>("SELECT coalesce(avg(CommunityRating), 0) FROM LocgComic WHERE CommunityRating > 0");
            if (mean <= 0) return new Population(3.78, 0.6);   // the site's own long-run figures, for an empty file
            var variance = hot.Scalar<double>(
                "SELECT coalesce(avg((CommunityRating - $m) * (CommunityRating - $m)), 0) FROM LocgComic WHERE CommunityRating > 0", ("$m", mean));
            var sigma = Math.Sqrt(variance);
            return new Population(mean, sigma <= 0.0001 ? 0.6 : sigma);
        }

        public sealed record RunCounts(int Series, int Items, int Skipped)
        {
            public override string ToString() => $"series: {Series}, items: {Items}, skipped: {Skipped}";
        }

        /// <summary>One bounded phase. Returns true when the job is done.</summary>
        public static bool RunStep(TargetWriter hot, long cursor, int batchSize, Action<string> log, UnitCounts counts, out long nextCursor)
        {
            batchSize = Math.Clamp(batchSize, 100, 50_000);
            var population = Measure(hot);

            if (cursor == SeriesCursor)
            {
                var n = RateSeries(hot, population);
                log($"library-ratings: {n} series rated (LOCG mean {population.Mean:0.00}, sigma {population.Sigma:0.00})");
                counts.Bump("series-rated", n);
                nextCursor = ItemsBase;
                return false;
            }

            var after = cursor - ItemsBase;
            var (last, rated, skipped) = RateItems(hot, population, after, batchSize);
            counts.Bump("items-rated", rated);
            counts.Bump("items-skipped", skipped);
            nextCursor = ItemsBase + last;
            if (last == after)
            {
                Stamp(hot);
                log("library-ratings: registry stamped");
                return true;
            }
            return false;
        }

        public static RunCounts RunAll(TargetWriter hot, int batchSize, Action<string> log)
        {
            var counts = new UnitCounts();
            var cursor = SeriesCursor;
            while (true)
            {
                hot.Begin();
                var done = RunStep(hot, cursor, batchSize, log, counts, out cursor);
                hot.Commit();
                if (done) break;
            }
            return new RunCounts(counts.Detail.GetValueOrDefault("series-rated"), counts.Detail.GetValueOrDefault("items-rated"), counts.Detail.GetValueOrDefault("items-skipped"));
        }

        // ── the series pass ──────────────────────────────────────────────────────────────────────────────

        private static int RateSeries(TargetWriter hot, Population population)
        {
            var locg = LocgPerSeries(hot, population);
            var insight = InsightPerSeries(hot);
            var mu = MuPerSeries(hot);
            var user = UserPerSeries(hot);
            var awards = AwardsPerSeries(hot);
            var overrides = Overrides(hot, SubjectKind.Series);

            hot.Exec($"DELETE FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} AND Source = {(int)RatingSource.Library}");

            var rated = 0;
            foreach (var (seriesId, _) in hot.Pairs("SELECT Id, coalesce(Name, '') FROM Series ORDER BY Id"))
            {
                var sid = (int)seriesId;
                var parts = new List<(double Score, double Weight)>();
                var sources = new List<string>();
                var notes = new List<string>();

                if (locg.TryGetValue(sid, out var l))
                {
                    var score = population.To100(l.Shrunk);
                    var weight = 1.3 * Math.Min(1.0, Math.Log10(1 + l.Votes) / 3.0);
                    parts.Add((score, weight));
                    sources.Add("locg");
                    notes.Add($"LOCG community {l.Raw:0.00}/5 over {l.Issues} rated issue(s), {l.Votes:0} votes " +
                              $"({(l.Raw > population.Mean ? "above" : "below")} the {population.Mean:0.00} site average; stretched to {score:0}/100)");
                }

                Confidence? conf = null;
                if (insight.TryGetValue(sid, out var ins) && ins.Rating is int rating && ConfidenceWeight.GetValueOrDefault(ins.Confidence) > 0)
                {
                    conf = ins.Confidence;
                    parts.Add((rating, ConfidenceWeight[ins.Confidence]));
                    sources.Add("insight");
                    notes.Add($"model assessment {rating}/100 ({ins.Confidence} confidence)");
                }

                if (mu.TryGetValue(sid, out var bayes))
                {
                    var score = Math.Clamp(bayes * 10 - 5, 10, 97);
                    parts.Add((score, 0.8));
                    sources.Add("mangaupdates");
                    notes.Add($"MangaUpdates bayesian {bayes:0.00}/10 (~{score:0}/100)");
                }

                if (user.TryGetValue(sid, out var personal))
                {
                    parts.Add((personal, 0.7));
                    sources.Add("user");
                    notes.Add($"personal rating {personal:0}/100");
                }

                if (parts.Count == 0) continue;

                var value = parts.Sum(p => p.Score * p.Weight) / parts.Sum(p => p.Weight);

                if (awards.TryGetValue(sid, out var tags))
                {
                    var boost = Math.Min(5, tags.Sum(t => AwardBoost.GetValueOrDefault(t)));
                    if (boost > 0)
                    {
                        sources.Add("awards");
                        notes.Add($"award pedigree ({string.Join(", ", tags.OrderBy(t => t, StringComparer.Ordinal))}) +{boost}");
                        value += boost;
                    }
                }
                value = Math.Clamp(value, 1, 99);

                if (sources.Count == 1 && sources[0] == "insight" && conf == Confidence.Low)
                    notes.Add("weak signal only - treat as a rough placeholder");

                if (overrides.TryGetValue(sid, out var ov))
                {
                    value = ov.Value;
                    sources.Add("adjudicated");
                    notes.Add($"adjudication: {ov.Note}");
                }

                Write(hot, SubjectKind.Series, sid, (int)Math.Round(value), notes, sources);
                rated++;
            }
            return rated;
        }

        // ── the item pass ────────────────────────────────────────────────────────────────────────────────

        private static (long Last, int Rated, int Skipped) RateItems(TargetWriter hot, Population population, long after, int batchSize)
        {
            var upto = hot.Scalar<long>(
                "SELECT coalesce(max(Id), 0) FROM (SELECT Id FROM Item WHERE Id > $after ORDER BY Id LIMIT $n)",
                ("$after", after), ("$n", batchSize));
            if (upto <= after) return (after, 0, 0);

            var locg = LocgPerItem(hot, after, upto);
            var insight = InsightPerItem(hot, after, upto);
            var user = UserPerItem(hot, after, upto);
            var overrides = Overrides(hot, SubjectKind.Item, after, upto);
            var seriesScore = SeriesScores(hot);

            hot.Exec($"DELETE FROM Rating WHERE TargetKind = {(int)SubjectKind.Item} AND Source = {(int)RatingSource.Library} AND TargetId > {after} AND TargetId <= {upto}");

            int rated = 0, skipped = 0;
            foreach (var (itemId, payload) in hot.Pairs(
                $"SELECT Id, coalesce(CAST(SeriesId AS TEXT),'') FROM Item WHERE Id > {after} AND Id <= {upto} AND coalesce(IsExcluded, 0) = 0 ORDER BY Id"))
            {
                var id = (int)itemId;
                var seriesId = payload!.Length == 0 ? (int?)null : int.Parse(payload);
                var parts = new List<(double, double)>();
                var sources = new List<string>();
                var notes = new List<string>();

                if (locg.TryGetValue(id, out var l))
                {
                    var votes = l.Votes <= 0 ? DefaultVotes : l.Votes;
                    var shrunk = (l.Rating * votes + IssueShrinkK * population.Mean) / (votes + IssueShrinkK);
                    var score = population.To100(shrunk);
                    parts.Add((score, Math.Min(1.0, Math.Log10(1 + votes) / 2.5)));
                    sources.Add("locg");
                    notes.Add($"LOCG community {l.Rating:0.00}/5 ({votes} votes; stretched to {score:0}/100)");
                }

                if (seriesId is int sid && seriesScore.TryGetValue(sid, out var ss))
                {
                    parts.Add((ss, 0.8));
                    sources.Add("series");
                    notes.Add(sources.Count == 1
                        ? $"no per-issue community rating; carries the series rating {ss}/100"
                        : $"blended with series rating {ss}/100");
                }

                if (insight.TryGetValue(id, out var ins) && ins.Rating is int bookRating)
                {
                    parts.Add((bookRating, 1.0));
                    sources.Add("insight-book");
                    notes.Add($"literary assessment {bookRating}/100 ({ins.Confidence} confidence)");
                }

                if (user.TryGetValue(id, out var personal))
                {
                    parts.Add((personal, 0.7));
                    sources.Add("user");
                    notes.Add($"personal rating {personal:0}/100");
                }

                if (overrides.TryGetValue(id, out var ov))
                {
                    // An adjudication REPLACES the blend rather than joining it — that is the point of one.
                    parts.Clear();
                    parts.Add((ov.Value, 1.0));
                    sources.Add("adjudicated");
                    notes.Add($"adjudication: {ov.Note}");
                }

                if (parts.Count == 0) { skipped++; continue; }

                var value = Math.Clamp(parts.Sum(p => p.Item1 * p.Item2) / parts.Sum(p => p.Item2), 1, 99);
                Write(hot, SubjectKind.Item, id, (int)Math.Round(value), notes, sources);
                rated++;
            }
            return (upto, rated, skipped);
        }

        private static void Write(TargetWriter hot, SubjectKind kind, int targetId, int value, List<string> notes, List<string> sources)
        {
            hot.Upsert("Rating", new
            {
                TargetKind = kind,
                TargetId = targetId,
                Source = RatingSource.Library,
                Value = value,
                Note = string.Join("; ", notes) + ". [" + string.Join(",", sources) + "]",
                IsOverride = false,
                ModelId = ModelId,
                GeneratedAt = DateTime.UtcNow,
            });
        }

        // ── signal loaders ───────────────────────────────────────────────────────────────────────────────

        private static Dictionary<int, (double Raw, double Shrunk, double Votes, int Issues)> LocgPerSeries(TargetWriter hot, Population population)
        {
            var map = new Dictionary<int, (double, double, double, int)>();
            foreach (var (seriesId, payload) in hot.Pairs($@"
SELECT i.SeriesId,
       sum(lc.CommunityRating * coalesce(lc.RatingCount, {DefaultVotes})) || char(31)
    || sum(coalesce(lc.RatingCount, {DefaultVotes})) || char(31) || count(*)
FROM Item i
JOIN ItemProviderLink l ON l.ItemId = i.Id AND l.Provider = {(int)Provider.Locg} AND l.Status = {(int)LinkStatus.Matched}
JOIN LocgComic lc ON lc.LocgComicId = CAST(l.ProviderKey AS INTEGER)
WHERE lc.CommunityRating > 0 AND i.SeriesId IS NOT NULL
GROUP BY i.SeriesId"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                var weighted = double.Parse(p[0], CultureInfo.InvariantCulture);
                var votes = double.Parse(p[1], CultureInfo.InvariantCulture);
                if (votes <= 0) continue;
                map[(int)seriesId] = (weighted / votes, (weighted + SeriesShrinkK * population.Mean) / (votes + SeriesShrinkK), votes, int.Parse(p[2]));
            }
            return map;
        }

        private static Dictionary<int, (double Rating, int Votes)> LocgPerItem(TargetWriter hot, long after, long upto)
        {
            var map = new Dictionary<int, (double, int)>();
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT l.ItemId, max(lc.CommunityRating) || char(31) || coalesce(max(lc.RatingCount), 0)
FROM ItemProviderLink l JOIN LocgComic lc ON lc.LocgComicId = CAST(l.ProviderKey AS INTEGER)
WHERE l.Provider = {(int)Provider.Locg} AND l.Status = {(int)LinkStatus.Matched} AND lc.CommunityRating > 0
  AND l.ItemId > {after} AND l.ItemId <= {upto}
GROUP BY l.ItemId"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                map[(int)itemId] = (double.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1]));
            }
            return map;
        }

        private static Dictionary<int, (int? Rating, Confidence Confidence)> InsightPerSeries(TargetWriter hot) =>
            InsightMap(hot, SubjectKind.Series, null, null);

        private static Dictionary<int, (int? Rating, Confidence Confidence)> InsightPerItem(TargetWriter hot, long after, long upto) =>
            InsightMap(hot, SubjectKind.Item, after, upto);

        private static Dictionary<int, (int? Rating, Confidence Confidence)> InsightMap(TargetWriter hot, SubjectKind kind, long? after, long? upto)
        {
            var range = after == null ? "" : $" AND SubjectId > {after} AND SubjectId <= {upto}";
            var map = new Dictionary<int, (int?, Confidence)>();
            foreach (var (subjectId, payload) in hot.Pairs(
                $"SELECT SubjectId, coalesce(CAST(Rating AS TEXT),'') || char(31) || Confidence FROM Insight " +
                $"WHERE SubjectKind = {(int)kind} AND IsCurrent = 1 AND SubjectId IS NOT NULL{range}"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                map[(int)subjectId] = (p[0].Length == 0 ? null : int.Parse(p[0]), (Confidence)int.Parse(p[1]));
            }
            return map;
        }

        private static Dictionary<int, double> MuPerSeries(TargetWriter hot)
        {
            var map = new Dictionary<int, double>();
            foreach (var (seriesId, value) in hot.Pairs($@"
SELECT s.Id, CAST(mu.BayesianRating AS TEXT) FROM Series s
LEFT JOIN MuSeriesLink ml ON ml.SeriesId = s.Id AND ml.Status = {(int)LinkStatus.Matched}
JOIN MuSeries mu ON mu.Id = coalesce(ml.MuSeriesId, s.MuSeriesId)
WHERE mu.BayesianRating > 0"))
                map[(int)seriesId] = double.Parse(value!, CultureInfo.InvariantCulture);
            return map;
        }

        private static Dictionary<int, double> UserPerSeries(TargetWriter hot)
        {
            var map = new Dictionary<int, double>();
            foreach (var (seriesId, value) in hot.Pairs(
                $"SELECT CAST(GroupKey AS INTEGER), CAST(avg(Rating) AS TEXT) FROM GroupMark " +
                $"WHERE GroupType = {(int)GroupType.Series} AND Rating IS NOT NULL GROUP BY GroupKey"))
                if (value != null) map[(int)seriesId] = double.Parse(value, CultureInfo.InvariantCulture);
            return map;
        }

        private static Dictionary<int, double> UserPerItem(TargetWriter hot, long after, long upto)
        {
            var map = new Dictionary<int, double>();
            foreach (var (itemId, value) in hot.Pairs(
                $"SELECT TargetId, CAST(avg(Value) AS TEXT) FROM Rating WHERE TargetKind = {(int)SubjectKind.Item} " +
                $"AND Source = {(int)RatingSource.User} AND Value IS NOT NULL AND TargetId > {after} AND TargetId <= {upto} GROUP BY TargetId"))
                if (value != null) map[(int)itemId] = double.Parse(value, CultureInfo.InvariantCulture);
            return map;
        }

        private static Dictionary<int, List<string>> AwardsPerSeries(TargetWriter hot)
        {
            var map = new Dictionary<int, List<string>>();
            foreach (var (seriesId, tag) in hot.Pairs(
                $"SELECT SeriesId, Value FROM SeriesTag WHERE Category = 'award' AND Source = {(int)TagSource.AI}"))
            {
                var sid = (int)seriesId;
                if (!map.TryGetValue(sid, out var list)) map[sid] = list = new List<string>();
                if (tag != null && !list.Contains(tag, StringComparer.OrdinalIgnoreCase)) list.Add(tag);
            }
            return map;
        }

        private static Dictionary<int, (int Value, string? Note)> Overrides(TargetWriter hot, SubjectKind kind, long? after = null, long? upto = null)
        {
            var range = after == null ? "" : $" AND TargetId > {after} AND TargetId <= {upto}";
            var map = new Dictionary<int, (int, string?)>();
            foreach (var (targetId, payload) in hot.Pairs(
                $"SELECT TargetId, coalesce(CAST(Value AS TEXT),'') || char(31) || coalesce(Note,'') FROM Rating " +
                $"WHERE TargetKind = {(int)kind} AND Source = {(int)RatingSource.Override} AND Value IS NOT NULL{range}"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                map[(int)targetId] = (int.Parse(p[0]), p[1].Length == 0 ? null : p[1]);
            }
            return map;
        }

        private static Dictionary<int, int> SeriesScores(TargetWriter hot)
        {
            var map = new Dictionary<int, int>();
            foreach (var (seriesId, value) in hot.Pairs(
                $"SELECT TargetId, CAST(Value AS TEXT) FROM Rating WHERE TargetKind = {(int)SubjectKind.Series} " +
                $"AND Source = {(int)RatingSource.Library} AND Value IS NOT NULL"))
                map[(int)seriesId] = int.Parse(value!);
            return map;
        }

        internal static void Stamp(TargetWriter hot)
        {
            var entry = DerivedTables.All.First(e => e.Name == DerivedName);
            hot.Upsert("DerivedTable", new
            {
                Name = entry.Name,
                RebuildJob = entry.RebuildJob,
                InputFingerprint = ResolvePipeline.Fingerprint(hot, entry.FingerprintSql),
                LastRebuiltAt = DateTime.UtcNow,
                RowCount = (int)hot.Scalar<long>($"SELECT count(*) FROM Rating WHERE Source = {(int)RatingSource.Library}"),
            });
        }
    }
}
