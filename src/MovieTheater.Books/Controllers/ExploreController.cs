using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Books.Access;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.Books.Projections;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    /// <summary>
    /// <b>Explore.</b> The standalone site's Home page, composed on v2 and answered in the SITE-WIDE
    /// <see cref="ExploreResponse"/> envelope (plan §9.4) rather than in a Books-shaped payload. Books is simply
    /// the first section to have an Explore server; Movies, Music, Arcade, Photos and Boardgames answer the same
    /// <c>{ spotlight, rails, seed }</c> with the same <see cref="CardItem"/>s, and the SPA's Explore tab is one
    /// component for all of them. That generalization is the whole point — nothing here may grow a field only
    /// Books could fill.
    ///
    /// <para><b>The rails are the standalone's, thresholds and seeds unchanged</b> (see the constants below):
    /// a rotated spotlight of top-rated titles that actually carry prose, the highest-rated SERIES (one card per
    /// series, drawn with its cover issue), the big collected editions, the counterpart kind's top-shelf reads,
    /// the suggestions shelf, and the genuinely-newest arrivals. Everything except "fresh arrivals" is a
    /// deterministic Fisher–Yates pick from a ranked pool, seeded by the UTC DAY NUMBER — so the page rotates
    /// once a day instead of on every render, and <c>?seed=</c> re-rolls it reproducibly.</para>
    ///
    /// <para><b>Two rails are comics-only and are simply absent for <c>kind=book</c></b>: series identity and
    /// containment are the comics spine, and a rail with nothing in it is a heading over a blank row. Empty rails
    /// are dropped, always — the client renders what it is given.</para>
    ///
    /// <para><b>Caching.</b> The payload is a function of (who is asking, their ceiling, the seed) and nothing
    /// else — no user ACTION state — and it is expensive to assemble, so it is memory-cached under the same
    /// house key shape the browse heads use, for 24 h. That TTL is a backstop: <see cref="CacheWarmupService"/>
    /// re-runs this action for every known identity whenever the catalog fingerprint moves, so fresh arrivals
    /// appear within a poll and no visitor ever pays the assembly. An explicit <c>?seed=</c> re-roll keys
    /// separately, stays unwarmed, and simply expires.</para>
    /// </summary>
    [ApiController]
    [Route("explore")]
    public sealed class ExploreController : ControllerBase
    {
        // ── the standalone's thresholds, named ────────────────────────────────────────────────────────────────
        /// <summary>A spotlight title has to be genuinely well rated — the hero is an editorial claim.</summary>
        public const int SpotlightMinRating = 75;
        public const int SpotlightPool = 300;
        public const int SpotlightCount = 6;

        /// <summary>A series must be one the library actually HOLDS a run of, or the rail headlines one-shots.</summary>
        public const int TopSeriesMinIssues = 4;
        public const int TopSeriesMinRating = 72;
        public const int TopSeriesPool = 140;
        public const int TopSeriesCount = 14;

        /// <summary>"Big" collected edition: an omnibus or a fat trade, not a two-issue staple.</summary>
        public const int EditionMinContains = 6;
        public const int EditionPool = 160;
        public const int EditionCount = 12;

        public const int TopReadsMinRating = 60;
        public const int TopReadsPool = 120;
        public const int TopReadsCount = 14;

        public const int SuggestedCount = 14;
        public const int FreshCount = 28;

        // Per-rail seed salts, so two rails drawn on the same day from overlapping pools do not pick the same
        // titles. These exact constants are the standalone's.
        private const int SeriesSalt = 0x5bd1e995;
        private const int EditionSalt = 0x27d4eb2f;
        private const int ReadsSalt = 0x165667b1;
        private const int SuggestSalt = 0x1b873593;
        private const int KidsSalt = 0x2545f49;

        /// <summary>Backstop only — the warmer keeps today's entry fresh; this just lets yesterday's lapse.</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

        // ── kids ─────────────────────────────────────────────────────────────────────────────────────────────
        public const int KidsSeriesCount = 6;
        public const int KidsIssuesPerSeries = 8;

        private readonly BooksDb db;
        private readonly IMemoryCache cache;
        private readonly BooksOptions options;

        public ExploreController(BooksDb db, IMemoryCache cache, BooksOptions options)
        {
            this.db = db;
            this.cache = cache;
            this.options = options;
        }

        /// <summary>
        /// GET /explore?kind=comic|book&amp;seed= — the section's Explore payload.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string? kind = null, [FromQuery] int? seed = null,
            CancellationToken ct = default)
        {
            var itemKind = CatalogController.ParseKind(kind);
            var daySeed = seed ?? DaySeed();
            var key = $"books:explore:{UserSig()}:{itemKind}:{daySeed}";
            if (cache.TryGetValue(key, out ExploreResponse? hit) && hit != null) return Ok(hit);

            var response = await ComposeAsync(itemKind, daySeed, ct);
            Cache(key, response);
            return Ok(response);
        }

        /// <summary>
        /// GET /explore/kids?seed= — the kids landing, in the same envelope.
        ///
        /// <para><b>The ceiling is forced to 0 no matter who is asking</b> — that is the whole purpose of the
        /// view, and it is why an admin sees exactly what a child sees here. On top of the ceiling the series
        /// must also carry a tag from the admin-maintained <c>KidSafeTag</c> allow-list: the allow-list decides
        /// inclusion, the ceiling is the floor underneath it.</para>
        ///
        /// <para>One deliberate change from the standalone, whose kids home reshuffled on every request: this is
        /// SEEDED like every other rail here, so the day's shelf is stable, reproducible and cacheable. A child
        /// refreshing the page is not a reason to re-assemble it, and <c>?seed=</c> still re-rolls.</para>
        ///
        /// <para>The payload does not depend on the caller at all (the ceiling is fixed and nothing per-user is
        /// composed), so it caches under a seed-only key and one warm serves every account.</para>
        /// </summary>
        [HttpGet("kids")]
        public async Task<IActionResult> GetKids([FromQuery] int? seed = null, CancellationToken ct = default)
        {
            var daySeed = seed ?? DaySeed();
            var key = $"books:explore:kids:{daySeed}";
            if (cache.TryGetValue(key, out ExploreResponse? hit) && hit != null) return Ok(hit);

            var response = await ComposeKidsAsync(daySeed, ct);
            Cache(key, response);
            return Ok(response);
        }

        // ── composition ──────────────────────────────────────────────────────────────────────────────────────

        private async Task<ExploreResponse> ComposeAsync(ItemKind kind, int daySeed, CancellationToken ct)
        {
            var media = MediaUrls.For(options, User);
            var counterpart = kind == ItemKind.Book ? ItemKind.Comic : ItemKind.Book;

            // ── spotlight: top-rated titles that carry editorial prose, one per series, rotated daily ────────
            // v2 renders "has something to read" as ResolvedSynopsisSource: the resolver already decided which
            // leg won the synopsis and writes None when no leg has one, so the old two-way OR over the ComicVine
            // description and the insight synopsis is a single scalar on the row.
            var spotlightPool = await Visible(kind)
                .Where(i => i.ResolvedRating >= SpotlightMinRating && i.ResolvedSynopsisSource != SynopsisSource.None)
                .OrderByDescending(i => i.ResolvedRating).ThenBy(i => i.Id)
                .Select(i => new { i.Id, i.SeriesId })
                .Take(SpotlightPool)
                .ToListAsync(ct);
            var spotlightIds = SeededPick(OnePerSeries(spotlightPool.Select(x => (x.Id, x.SeriesId))), daySeed, SpotlightCount);

            // ── highest-rated series → one representative cover issue each ───────────────────────────────────
            var seriesPick = new List<SeriesPick>();
            var seriesReps = new Dictionary<int, int>();
            if (kind == ItemKind.Comic)
            {
                var seriesPool = await db.Series.AsNoTracking()
                    .Where(s => s.IssueCount >= TopSeriesMinIssues && s.ResolvedRating >= TopSeriesMinRating)
                    .OrderByDescending(s => s.ResolvedRating).ThenBy(s => s.Id)
                    .Select(s => new SeriesPick(s.Id, s.DisplayNameOverride ?? s.Name ?? "", s.ResolvedRating,
                        s.IssueCount, s.YearStart, s.YearEnd))
                    .Take(TopSeriesPool)
                    .ToListAsync(ct);
                seriesPick = SeededPick(seriesPool, daySeed ^ SeriesSalt, TopSeriesCount);
                seriesReps = await RepresentativesAsync(seriesPick.Select(s => s.Id).ToList(), ct);
            }

            // ── notable collected editions: the big collections, one per series ──────────────────────────────
            var editionSpans = new Dictionary<int, string?>();
            var editionIds = new List<int>();
            if (kind == ItemKind.Comic)
            {
                var editionPool = await (
                        from i in Visible(ItemKind.Comic)
                        join n in db.CollectionNodes.AsNoTracking() on i.Id equals n.ItemId
                        where n.ContainsCount >= EditionMinContains && n.TrackRole != TrackRole.Alternate
                        orderby (i.ResolvedRating ?? 0) descending, n.ContainsCount descending, i.Id
                        select new { i.Id, i.SeriesId, n.SpanLabel, n.SpanStart, n.SpanEnd })
                    .Take(EditionPool)
                    .ToListAsync(ct);
                editionIds = SeededPick(OnePerSeries(editionPool.Select(x => (x.Id, x.SeriesId))), daySeed ^ EditionSalt, EditionCount);
                foreach (var e in editionPool)
                    editionSpans[e.Id] = e.SpanLabel ?? (e.SpanStart != null && e.SpanEnd != null ? $"#{e.SpanStart}–{e.SpanEnd}" : null);
            }

            // ── the counterpart shelf: the comics page's books rail, and the books page's comics rail ────────
            var counterpartPool = await Visible(counterpart)
                .Where(i => i.ResolvedRating >= TopReadsMinRating)
                .OrderByDescending(i => i.ResolvedRating).ThenBy(i => i.Id)
                .Select(i => i.Id)
                .Take(TopReadsPool)
                .ToListAsync(ct);
            var counterpartIds = SeededPick(counterpartPool, daySeed ^ ReadsSalt, TopReadsCount);

            // ── suggestions: the slice-3 recommender, composed rather than re-implemented ────────────────────
            var suggested = await new SuggestionsController(db, cache) { ControllerContext = ControllerContext }
                .SuggestAsync(SuggestedCount, daySeed ^ SuggestSalt, ct);

            // ── freshest arrivals (NOT rotated — the point is that they are genuinely the newest) ────────────
            var freshIds = await Visible(kind)
                .OrderByDescending(i => i.IndexedAt).ThenByDescending(i => i.Id)
                .Select(i => i.Id)
                .Take(FreshCount)
                .ToListAsync(ct);

            // ── ONE projection pass over every id any rail references ────────────────────────────────────────
            var allIds = spotlightIds
                .Concat(seriesReps.Values).Concat(editionIds).Concat(counterpartIds).Concat(freshIds)
                .Concat(suggested.Select(s => s.Id))
                .Distinct().ToList();
            var byId = await UserActivityQueries.SummariesAsync(db, User, allIds, ct);

            List<CardItem> Cards(IEnumerable<int> ids, Func<ItemSummary, string?>? sortKey = null,
                Func<ItemSummary, IEnumerable<CardBadge>?>? badges = null) =>
                ids.Where(byId.ContainsKey)
                    .Select(id => byId[id])
                    .Select(s => CardFactory.FromItem(s, media, sortKey?.Invoke(s), badges?.Invoke(s)))
                    .ToList();

            static string? RatingKey(ItemSummary s) => s.Rating?.ToString(CultureInfo.InvariantCulture);

            var rails = new List<ExploreRail>
            {
                new("top-series", "Highest-rated series", "strip",
                    seriesPick.Where(s => seriesReps.ContainsKey(s.Id) && byId.ContainsKey(seriesReps[s.Id]))
                        .Select(s => CardFactory.FromSeries(s.Id, s.Name, byId[seriesReps[s.Id]].Publisher, s.Rating,
                            s.IssueCount, s.YearStart, s.YearEnd, byId[seriesReps[s.Id]], media,
                            sortKey: s.Rating?.ToString(CultureInfo.InvariantCulture)))
                        .ToList(),
                    new ExploreMore("/browse/groups?groupBy=series&kind=comic")),

                // No "more": containment is not part of the browse filter vocabulary, and a link that quietly
                // led somewhere else would be worse than no link. Slice 5's recompute owns that data.
                new("collected-editions", "Big collected editions", "strip",
                    Cards(editionIds, RatingKey,
                        s => editionSpans.GetValueOrDefault(s.Id) is string span ? [new CardBadge(span, "neutral", "Collects")] : null)),

                new("top-shelf-reads", counterpart == ItemKind.Book ? "Top-shelf reads" : "Best in comics", "strip",
                    Cards(counterpartIds, RatingKey),
                    new ExploreMore($"/odata/catalog?kind={KindName(counterpart)}&$filter=rating ge {TopReadsMinRating}&$orderby=rating desc")),

                new("suggested", "Suggested for you", "strip",
                    Cards(suggested.Select(s => s.Id), RatingKey),
                    new ExploreMore($"/suggestions?count={SuggestionsController.MaxCount}")),

                new("fresh-arrivals", "Fresh arrivals", "wall",
                    Cards(freshIds, s => s.IndexedAt?.ToString("O", CultureInfo.InvariantCulture)),
                    new ExploreMore($"/odata/catalog?kind={KindName(kind)}&$orderby=indexedAt desc")),
            };

            return new ExploreResponse(
                Cards(spotlightIds, RatingKey),
                rails.Where(r => r.Items.Count > 0).ToList(),
                daySeed);
        }

        private async Task<ExploreResponse> ComposeKidsAsync(int daySeed, CancellationToken ct)
        {
            var media = MediaUrls.For(options, User);
            var kidSeries = await KidsPolicy.KidSeriesAsync(db, ItemKind.Comic, ct);
            if (kidSeries.Count == 0) return new ExploreResponse([], [], daySeed);

            var seriesIds = kidSeries.Keys.ToList();
            var issueRows = await KidsPolicy.KidItems(db, ItemKind.Comic, seriesIds)
                .Select(i => new { i.Id, SeriesId = i.SeriesId!.Value })
                .ToListAsync(ct);

            var issuesBySeries = issueRows
                .GroupBy(r => r.SeriesId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).OrderBy(id => id).Take(KidsIssuesPerSeries).ToList());

            var eligible = kidSeries.Values.Where(s => issuesBySeries.ContainsKey(s.Id))
                .OrderByDescending(s => s.Rating ?? 0).ThenBy(s => s.Id).ToList();
            if (eligible.Count == 0) return new ExploreResponse([], [], daySeed);

            // Hero: a seeded pick from the best-rated 15, so the shelf is a good one and still rotates daily.
            var hero = SeededPick(eligible.Take(15).ToList(), daySeed ^ KidsSalt, 1).Single();
            var body = SeededPick(eligible.Where(s => s.Id != hero.Id).ToList(), daySeed, KidsSeriesCount - 1);

            var allIds = issuesBySeries[hero.Id]
                .Concat(body.SelectMany(s => issuesBySeries.GetValueOrDefault(s.Id, [])))
                .Distinct().ToList();
            var byId = (await db.Items.AsNoTracking().Where(i => allIds.Contains(i.Id))
                    .Select(ItemSummary.Project).ToListAsync(ct))
                .ToDictionary(s => s.Id);

            List<CardItem> Issues(int seriesId) =>
                issuesBySeries.GetValueOrDefault(seriesId, []).Where(byId.ContainsKey)
                    .Select(id => CardFactory.FromItem(byId[id], media)).ToList();

            var rails = new List<ExploreRail>();
            foreach (var s in body)
            {
                var items = Issues(s.Id);
                if (items.Count > 0)
                    rails.Add(new ExploreRail($"series:{s.Id}", s.Name, "strip", items,
                        new ExploreMore($"/kids/series/{s.Id}/items")));
            }

            return new ExploreResponse(Issues(hero.Id), rails, daySeed);
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One series' facts as the rail card needs them. A record, so <see cref="SeededPick"/> is generic over it.</summary>
        private sealed record SeriesPick(int Id, string Name, int? Rating, int IssueCount, int? YearStart, int? YearEnd);

        private IQueryable<Item> Visible(ItemKind kind) => ItemAccess.VisibleItems(db, User, kind);

        private static string KindName(ItemKind kind) => kind == ItemKind.Book ? "book" : "comic";

        /// <summary>
        /// The rotation seed: the UTC DAY NUMBER. It changes once a day, which is what makes the page feel
        /// curated rather than random, and it is a plain int so a client can echo it back as <c>?seed=</c>.
        /// </summary>
        public static int DaySeed() => (int)(DateTime.UtcNow.Date.Ticks / TimeSpan.TicksPerDay);

        /// <summary>
        /// Keep the FIRST (best-ranked) title of each series, so a rail shows twelve different books rather than
        /// twelve issues of one. A title with no series stands alone (negated id — it can never collide with a
        /// real series id).
        /// </summary>
        private static List<int> OnePerSeries(IEnumerable<(int Id, int? SeriesId)> pool) =>
            pool.GroupBy(x => x.SeriesId ?? -x.Id).Select(g => g.First().Id).ToList();

        /// <summary>
        /// Deterministic Fisher–Yates by seed, then take. The pool is already RANKED, so shuffling before taking
        /// is what makes the rail rotate through the good titles instead of showing the same top N for ever —
        /// and seeding it is what makes today's page identical on every render and every replica.
        /// </summary>
        public static List<T> SeededPick<T>(IReadOnlyList<T> pool, int seed, int take)
        {
            var arr = pool.ToList();
            var rng = new Random(seed);
            for (var i = arr.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (arr[i], arr[j]) = (arr[j], arr[i]);
            }
            return arr.Take(take).ToList();
        }

        /// <summary>
        /// The cover issue of each series: prefer one whose cover dimensions are known (so the card is not a
        /// placeholder), then the earliest in reading order, then the lowest id. Gated like everything else, so a
        /// series whose issues are all above the caller's ceiling simply drops out of the rail.
        /// </summary>
        private async Task<Dictionary<int, int>> RepresentativesAsync(List<int> seriesIds, CancellationToken ct)
        {
            if (seriesIds.Count == 0) return new Dictionary<int, int>();
            var rows = await (
                    from i in Visible(ItemKind.Comic)
                    where i.SeriesId != null && seriesIds.Contains(i.SeriesId.Value)
                    join r in db.ReadingOrderEntries.AsNoTracking() on i.Id equals r.ItemId into ro
                    from r in ro.DefaultIfEmpty()
                    select new
                    {
                        i.Id,
                        SeriesId = i.SeriesId!.Value,
                        HasCover = i.CoverAspect != null,
                        ReadIndex = r == null ? (int?)null : r.ReadIndex,
                    })
                .ToListAsync(ct);

            return rows.GroupBy(x => x.SeriesId).ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.HasCover).ThenBy(x => x.ReadIndex ?? int.MaxValue).ThenBy(x => x.Id).First().Id);
        }

        /// <summary>Every cache key carries the caller's facts: the gate changes what the rails contain.</summary>
        private string UserSig() =>
            $"{BooksIdentity.UserId(User)}:{BooksIdentity.CeilingFor(User)}:{(BooksIdentity.IsAdmin(User) ? 1 : 0)}";

        // Size = 1: the shared cache counts payloads, not bytes (see BrowseController).
        private void Cache(string key, ExploreResponse value) =>
            cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl, Size = 1 });
    }
}
