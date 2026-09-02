using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    /// <summary>
    /// The "Suggested" shelf: a purely metadata-driven recommender, ported from the standalone site's
    /// <c>suggestions-algorithm</c> onto v2. No model call, no external service — it reads what the user has read
    /// and liked, folds that into four weighted taste vectors, dot-products them against the series the user has
    /// NOT touched, and returns one representative issue per winning series.
    ///
    /// <para><b>The eight phases</b> (weights unchanged from the skill, which is the contract):
    /// (1) collect signals from <c>UserItemState</c> and <c>GroupMark(Series)</c>;
    /// (2) sum them into a per-series weight — finished 1.0, favourite 2.5, want 0.5, series-read 2.0,
    /// series-favourite 3.5, series-want 0.7, series-rating ÷20;
    /// (3) build tag / author / artist / publisher profiles from the CURRENT series insights, scaled by that
    /// series' weight × a confidence multiplier and, for tags, a per-category multiplier;
    /// (4) candidates = every series with a visible item that the user has not engaged with;
    /// (5) score each candidate by the same vectors, plus a quality bonus and an award bonus;
    /// (6) multiply by per-call noise in [0.6, 1.4] and take <c>count × 3</c>;
    /// (7) pick the earliest unread issue of each winner in reading order;
    /// (8) return them gated, in the winners' order.</para>
    ///
    /// <para><b>What changed in the port, and why.</b> Series are keyed by <c>SeriesId</c>, never by name string —
    /// v1's name keys detached whenever the series resolver ran. The dead <c>SeriesUserLists</c> signal is gone
    /// (the table has no rows and no writer). The per-library half of the input — insights, AI tags, per-series
    /// publishers — is DERIVED data, identical for every caller, so it is built once and memory-cached; only the
    /// user's own signals and the maturity-gated candidate set are computed per request. And the ordering is
    /// deterministic given a seed: <c>?seed=</c> makes a run reproducible (a test, or a stable daily shuffle), and
    /// ties always break on the series id rather than on dictionary order.</para>
    ///
    /// <para><b>Exclusions</b> are the point as much as the scoring: a series the user has read, wants, or rated
    /// is not a suggestion, an item they dismissed from their history is not a representative, and a shadow
    /// duplicate or an above-ceiling item is never either.</para>
    /// </summary>
    [ApiController]
    [Route("suggestions")]
    public sealed class SuggestionsController : ControllerBase
    {
        public const int MaxCount = 100;
        private const string CorpusCacheKey = "books:suggestions:corpus";
        private static readonly TimeSpan CorpusTtl = TimeSpan.FromMinutes(20);

        /// <summary>Roles that count as an author / an artist — the browse facets' vocabulary.</summary>
        private static readonly string[] AuthorRoles = { "Writer", "Author" };

        /// <summary>How many finished items feed the author/publisher augmentation (the skill's cap).</summary>
        private const int AugmentationItems = 200;

        private readonly BooksDb db;
        private readonly IMemoryCache cache;
        private readonly CatalogCacheVersion? version;
        public SuggestionsController(BooksDb db, IMemoryCache cache, CatalogCacheVersion? version = null)
        {
            this.db = db;
            this.cache = cache;
            this.version = version;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] int count = 12,
            [FromQuery] int? seed = null,
            CancellationToken ct = default)
        {
            if (BooksIdentity.UserId(User) is not int) return Forbid();
            var items = await SuggestAsync(count, seed, ct);
            return Ok(new { count = items.Count, items });
        }

        /// <summary>
        /// The shelf itself, without the HTTP envelope — the eight phases below, in the winners' order.
        ///
        /// <para>It is separate from <see cref="Get"/> because the Explore rails compose it: the "Suggested" rail
        /// on <c>/explore</c> IS this list, and a second implementation of a weighted recommender is exactly the
        /// kind of drift the port exists to remove. Returns an empty list — never an error — when the user has no
        /// signals yet, no candidate scores above zero, or no principal: a rail with nothing in it is simply not
        /// rendered.</para>
        /// </summary>
        internal async Task<List<ItemSummary>> SuggestAsync(int count, int? seed, CancellationToken ct)
        {
            if (BooksIdentity.UserId(User) is not int userId) return [];
            count = Math.Clamp(count, 1, MaxCount);

            // ── 1. signals ────────────────────────────────────────────────────────────────────────────────────
            var states = await db.UserItemStates.AsNoTracking().Where(s => s.UserId == userId)
                .Select(s => new { s.ItemId, s.Status, s.WantToRead, s.Favorite, s.HiddenFromHistory })
                .ToListAsync(ct);
            var seriesMarks = await db.GroupMarks.AsNoTracking()
                .Where(m => m.UserId == userId && m.GroupType == GroupType.Series)
                .Select(m => new { m.GroupKey, m.IsRead, m.WantToRead, m.IsFavorite, m.Rating })
                .ToListAsync(ct);

            var touched = states.Select(s => s.ItemId).Distinct().ToList();
            var seriesByItem = touched.Count == 0
                ? new Dictionary<int, int>()
                : await db.Items.AsNoTracking().Where(i => touched.Contains(i.Id) && i.SeriesId != null)
                    .Select(i => new { i.Id, SeriesId = i.SeriesId!.Value })
                    .ToDictionaryAsync(x => x.Id, x => x.SeriesId, ct);

            // ── 2. per-series weights ─────────────────────────────────────────────────────────────────────────
            var weights = new Dictionary<int, float>();
            void AddWeight(int seriesId, float w)
            {
                if (w == 0f) return;
                weights[seriesId] = weights.GetValueOrDefault(seriesId) + w;
            }

            var finishedItems = new HashSet<int>();
            var excludedItems = new HashSet<int>();
            foreach (var s in states)
            {
                if (s.Status == ReadStatus.Finished) finishedItems.Add(s.ItemId);
                // A representative must be something the user has not already read, queued or dismissed.
                if (s.Status == ReadStatus.Finished || s.WantToRead || s.Favorite || s.HiddenFromHistory)
                    excludedItems.Add(s.ItemId);
                if (!seriesByItem.TryGetValue(s.ItemId, out var seriesId)) continue;
                if (s.Status == ReadStatus.Finished) AddWeight(seriesId, 1.0f);
                if (s.Favorite) AddWeight(seriesId, 2.5f);
                if (s.WantToRead) AddWeight(seriesId, 0.5f);
            }
            foreach (var m in seriesMarks)
            {
                if (!int.TryParse(m.GroupKey, out var seriesId)) continue;   // v1 name keys are not series
                var w = 0f;
                if (m.IsRead) w += 2.0f;
                if (m.IsFavorite) w += 3.5f;
                if (m.WantToRead) w += 0.7f;
                if (m.Rating is int rating) w += rating / 20.0f;   // 0-100 → 0-5
                AddWeight(seriesId, w);
            }

            if (weights.Count == 0) return [];
            var known = weights.Keys.ToHashSet();

            // ── 3. taste profile ──────────────────────────────────────────────────────────────────────────────
            var corpus = await CorpusAsync(ct);
            var tagProfile = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var authorProfile = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var artistProfile = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var pubProfile = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            static void Acc(Dictionary<string, float> d, string key, float w) => d[key] = d.GetValueOrDefault(key) + w;

            foreach (var seriesId in known)
            {
                var weight = weights[seriesId];
                if (corpus.Series.TryGetValue(seriesId, out var meta))
                {
                    var effective = weight * ProfileConfidence(meta.Confidence);
                    foreach (var tag in corpus.TagsFor(seriesId))
                        Acc(tagProfile, tag.Key, effective * CatMult(tag.Category));
                    foreach (var name in SplitNames(meta.Author)) Acc(authorProfile, name, effective * 4.0f);
                    foreach (var name in SplitNames(meta.Artist)) Acc(artistProfile, name, effective * 2.5f);
                }
                if (corpus.Publisher.TryGetValue(seriesId, out var seriesPub) && seriesPub.Length > 0)
                    Acc(pubProfile, seriesPub, weight * 0.3f);
            }

            // Augmentation: the creators and publishers of the issues the user actually finished. This is what
            // carries a taste profile for series that have no insight row yet.
            var augmentIds = finishedItems.Take(AugmentationItems).ToList();
            if (augmentIds.Count > 0)
            {
                var credits = await db.ItemCredits.AsNoTracking()
                    .Where(c => augmentIds.Contains(c.ItemId) && c.Role != null && AuthorRoles.Contains(c.Role)
                                && c.Name != null && c.Name != "")
                    .Select(c => c.Name!).ToListAsync(ct);
                foreach (var name in credits) if (name.Length > 1) Acc(authorProfile, name, 1.5f);

                var pubs = await db.Items.AsNoTracking()
                    .Where(i => augmentIds.Contains(i.Id) && i.ResolvedPublisher != null && i.ResolvedPublisher != "")
                    .Select(i => i.ResolvedPublisher!).ToListAsync(ct);
                foreach (var pub in pubs) Acc(pubProfile, pub, 0.4f);
            }

            // ── 4. candidates ─────────────────────────────────────────────────────────────────────────────────
            var candidates = (await UserActivityQueries.AccessibleItems(db, User)
                    .Where(i => i.SeriesId != null).Select(i => i.SeriesId!.Value).Distinct().ToListAsync(ct))
                .Where(id => !known.Contains(id)).ToList();
            if (candidates.Count == 0) return [];

            // ── 5. scoring ────────────────────────────────────────────────────────────────────────────────────
            float Score(int seriesId)
            {
                var score = 0f;
                if (corpus.Series.TryGetValue(seriesId, out var meta))
                {
                    var cm = ScoringConfidence(meta.Confidence);
                    var hasAward = false;
                    foreach (var tag in corpus.TagsFor(seriesId))
                    {
                        if (tagProfile.TryGetValue(tag.Key, out var tv))
                            score += tv * cm * CatMult(tag.Category) * 0.25f;
                        if (tag.Category.Equals("award", StringComparison.OrdinalIgnoreCase)) hasAward = true;
                    }
                    foreach (var name in SplitNames(meta.Author))
                        if (authorProfile.TryGetValue(name, out var av)) score += av * cm * 0.35f;
                    foreach (var name in SplitNames(meta.Artist))
                        if (artistProfile.TryGetValue(name, out var av)) score += av * cm * 0.2f;
                    if (meta.Rating is int rating) score += rating / 100.0f * 4.0f;
                    if (hasAward) score += 2.0f;
                }
                if (corpus.Publisher.TryGetValue(seriesId, out var pub) && pubProfile.TryGetValue(pub, out var pv))
                    score += pv * 0.1f;
                return score;
            }

            var scored = candidates.Select(id => (Series: id, Score: Score(id))).Where(x => x.Score > 0f).ToList();
            if (scored.Count == 0) return [];

            // ── 6. noisy sampling ─────────────────────────────────────────────────────────────────────────────
            // Variety without randomness: the same seed replays the same shelf, and equal scores always resolve
            // on the series id, so nothing depends on hash order.
            var rng = seed.HasValue ? new Random(seed.Value) : new Random();
            var winners = scored
                .Select(x => (x.Series, Noisy: x.Score * (0.6f + (float)rng.NextDouble() * 0.8f)))
                .OrderByDescending(x => x.Noisy).ThenBy(x => x.Series)
                .Take(count * 3).Select(x => x.Series).ToList();

            // ── 7. one representative issue per winner ────────────────────────────────────────────────────────
            var pool = await (from i in UserActivityQueries.AccessibleItems(db, User)
                              where i.SeriesId != null && winners.Contains(i.SeriesId.Value) && !excludedItems.Contains(i.Id)
                              join r in db.ReadingOrderEntries.AsNoTracking() on i.Id equals r.ItemId into ro
                              from r in ro.DefaultIfEmpty()
                              select new
                              {
                                  i.Id,
                                  SeriesId = i.SeriesId!.Value,
                                  ReadIndex = r == null ? (int?)null : r.ReadIndex,
                                  i.ResolvedRating,
                              }).ToListAsync(ct);

            var repBySeries = pool.GroupBy(x => x.SeriesId).ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.ReadIndex ?? int.MaxValue)
                      .ThenByDescending(x => x.ResolvedRating ?? -1)
                      .ThenBy(x => x.Id).First().Id);

            var repIds = winners.Where(repBySeries.ContainsKey).Select(id => repBySeries[id]).Distinct().Take(count).ToList();

            // ── 8. project, in the winners' order ─────────────────────────────────────────────────────────────
            var summaries = await UserActivityQueries.SummariesAsync(db, User, repIds, ct);
            return repIds.Select(summaries.GetValueOrDefault).Where(s => s != null).Select(s => s!).ToList();
        }

        // ── the derived corpus (identical for every caller, so it is built once) ───────────────────────────────

        /// <summary>One series' current-insight facts. Tags live beside it, keyed by series.</summary>
        private sealed record SeriesMeta(Confidence Confidence, int? Rating, string? Author, string? Artist);

        /// <summary>One AI tag as the profile keys it: <c>"{category}:{value}"</c>, category kept for its multiplier.</summary>
        private sealed record SeriesTagKey(string Category, string Key);

        private sealed class SuggestionCorpus
        {
            public Dictionary<int, SeriesMeta> Series { get; init; } = new();
            public Dictionary<int, List<SeriesTagKey>> Tags { get; init; } = new();
            public Dictionary<int, string> Publisher { get; init; } = new();
            private static readonly List<SeriesTagKey> None = new();
            public List<SeriesTagKey> TagsFor(int seriesId) => Tags.GetValueOrDefault(seriesId) ?? None;
        }

        private async Task<SuggestionCorpus> CorpusAsync(CancellationToken ct)
        {
            if (cache.TryGetValue(CorpusCacheKey, out SuggestionCorpus? hit) && hit != null) return hit;

            var insights = await db.Insights.AsNoTracking()
                .Where(n => n.SubjectKind == SubjectKind.Series && n.IsCurrent && n.SubjectId != null)
                .Select(n => new { SeriesId = n.SubjectId!.Value, n.Confidence, n.Rating, n.Author, n.Artist })
                .ToListAsync(ct);

            var tags = await db.SeriesTags.AsNoTracking()
                .Where(t => t.Source == TagSource.AI && t.Value != "")
                .Select(t => new { t.SeriesId, t.Category, t.Value }).ToListAsync(ct);

            // The series' publisher, the way a person would name it: whichever the most of its issues resolved to.
            var pubCounts = await db.Items.AsNoTracking()
                .Where(i => i.SeriesId != null && i.ResolvedPublisher != null && i.ResolvedPublisher != "" && !i.IsExcluded)
                .GroupBy(i => new { SeriesId = i.SeriesId!.Value, Publisher = i.ResolvedPublisher! })
                .Select(g => new { g.Key.SeriesId, g.Key.Publisher, Count = g.Count() })
                .ToListAsync(ct);

            var corpus = new SuggestionCorpus
            {
                Series = insights.GroupBy(n => n.SeriesId).ToDictionary(
                    g => g.Key, g => new SeriesMeta(g.First().Confidence, g.First().Rating, g.First().Author, g.First().Artist)),
                Tags = tags.GroupBy(t => t.SeriesId).ToDictionary(
                    g => g.Key,
                    g => g.Select(t => new SeriesTagKey(t.Category, $"{t.Category}:{t.Value}"))
                          .DistinctBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList()),
                Publisher = pubCounts.GroupBy(p => p.SeriesId).ToDictionary(
                    g => g.Key, g => g.OrderByDescending(p => p.Count).ThenBy(p => p.Publisher, StringComparer.Ordinal).First().Publisher),
            };

            var entry = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CorpusTtl,
                Size = 1,
            };
            if (version != null) entry.AddExpirationToken(version.Token);   // the corpus expires with the catalog
            cache.Set(CorpusCacheKey, corpus, entry);
            return corpus;
        }

        // ── the skill's constants ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Trust High-confidence metadata most when learning what the user likes.</summary>
        private static float ProfileConfidence(Confidence c) => c switch
        {
            Confidence.High => 1.0f,
            Confidence.Medium => 0.75f,
            Confidence.Low => 0.4f,
            _ => 0.2f,
        };

        /// <summary>Be slightly more forgiving when scoring a less-documented candidate — deliberately not the same curve.</summary>
        private static float ScoringConfidence(Confidence c) => c switch
        {
            Confidence.High => 1.0f,
            Confidence.Medium => 0.8f,
            Confidence.Low => 0.5f,
            _ => 0.3f,
        };

        /// <summary>What kind of tag it is decides how much it says about taste: genre a lot, award barely.</summary>
        private static float CatMult(string category) => category.ToLowerInvariant() switch
        {
            "genre" => 3.0f,
            "theme" => 2.5f,
            "tone" => 2.0f,
            "character-focus" => 2.0f,
            "setting" => 1.5f,
            "audience" => 1.5f,
            "era" => 1.0f,
            "award" => 0.8f,
            _ => 1.0f,
        };

        private static IEnumerable<string> SplitNames(string? csv) =>
            string.IsNullOrEmpty(csv)
                ? []
                : csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                     .Where(n => n.Length > 1);
    }
}
