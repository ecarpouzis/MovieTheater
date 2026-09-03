using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Web
{
    /// <summary>
    /// The facet-rail query model binder: every facet the Movies/TV rail can set, as repeatable query
    /// params (<c>?genre=Crime&amp;genre=Drama&amp;exGenre=Horror&amp;person=Al%20Pacino&amp;mpa=3,4&amp;yearMin=1990
    /// &amp;tag=mood:cozy&amp;my=seen</c>). The SPA writes these from the catalog's URL contract
    /// (<c>f=token:value</c> / <c>x=token:value</c> / <c>y=</c> / <c>r=</c> / <c>my=</c>); the same shape rides
    /// the flat browse, the groups, the letters and the facet counts, so they cannot disagree.
    /// </summary>
    public sealed class BrowseFilterQuery
    {
        public string? q { get; set; }
        public string[]? genre { get; set; }
        public string[]? exGenre { get; set; }
        public string[]? franchise { get; set; }
        public string[]? exFranchise { get; set; }
        public string[]? person { get; set; }
        public string[]? exPerson { get; set; }
        /// <summary>Composite <c>category:value</c> — subgenre / mood / theme / setting / era / style / content.</summary>
        public string[]? tag { get; set; }
        public string[]? exTag { get; set; }
        /// <summary>Comma list of MPA lookup ids (the site's five stops; 5 covers X too).</summary>
        public string? mpa { get; set; }
        public int? yearMin { get; set; }
        public int? yearMax { get; set; }
        /// <summary>seen | want | rated — the caller's own lists.</summary>
        public string? my { get; set; }
    }

    /// <summary>
    /// The combinable browse filter behind the Movies/TV facet rail (R9 S2): ANDed includes, NOTed
    /// excludes, across genre / franchise / people / AI tags / MPA / years / the viewer's lists, plus the
    /// title text. Pure: it takes the caller's ALREADY-GATED base queries (quarantine + age gate live in
    /// the controller), like <see cref="BrowseGroups"/>, so it runs against SQLite in the tests as written.
    /// </summary>
    public sealed class BrowseFilter
    {
        public string Q { get; init; } = "";
        public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ExGenres { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Franchises { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ExFranchises { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Persons { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ExPersons { get; init; } = Array.Empty<string>();
        public IReadOnlyList<(TagCategory Category, string Value)> Tags { get; init; } = Array.Empty<(TagCategory, string)>();
        public IReadOnlyList<(TagCategory Category, string Value)> ExTags { get; init; } = Array.Empty<(TagCategory, string)>();
        public IReadOnlyList<int> Mpa { get; init; } = Array.Empty<int>();
        public int? YearMin { get; init; }
        public int? YearMax { get; init; }
        /// <summary>The viewer's own lists (seen / want / rated), ANDed — every named list must hold the title.</summary>
        public IReadOnlyList<string> My { get; init; } = Array.Empty<string>();

        public static readonly BrowseFilter Empty = new();

        /// <summary>The tag categories the rail offers, by their URL token.</summary>
        public static readonly IReadOnlyDictionary<string, TagCategory> TagTokens = new Dictionary<string, TagCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["subgenre"] = TagCategory.Subgenre,
            ["mood"] = TagCategory.Mood,
            ["theme"] = TagCategory.Theme,
            ["setting"] = TagCategory.Setting,
            ["era"] = TagCategory.Era,
            ["style"] = TagCategory.VisualStyle,
            ["content"] = TagCategory.ContentDescriptor,
        };

        public static string TagToken(TagCategory c) => TagTokens.First(kv => kv.Value == c).Key;

        public static BrowseFilter From(BrowseFilterQuery? fq)
        {
            if (fq == null) return Empty;
            // Repeatable params (?genre=A&genre=B) arrive as separate elements; values keep their commas.
            static List<string> Clean(string[]? xs) => (xs ?? Array.Empty<string>())
                .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            static List<(TagCategory, string)> Tags(string[]? xs)
            {
                var list = new List<(TagCategory, string)>();
                foreach (var raw in Clean(xs))
                {
                    var i = raw.IndexOf(':');
                    if (i <= 0) continue;
                    if (!TagTokens.TryGetValue(raw[..i], out var cat)) continue;
                    var v = raw[(i + 1)..].Trim();
                    if (v.Length > 0) list.Add((cat, v));
                }
                return list;
            }
            var mpa = (fq.mpa ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();
            // NC-17 (5) stands for X (6) too — one certificate as far as anyone browsing is concerned.
            if (mpa.Contains(5) && !mpa.Contains(6)) mpa.Add(6);
            return new BrowseFilter
            {
                Q = (fq.q ?? "").Trim(),
                Genres = Clean(fq.genre), ExGenres = Clean(fq.exGenre),
                Franchises = Clean(fq.franchise), ExFranchises = Clean(fq.exFranchise),
                Persons = Clean(fq.person), ExPersons = Clean(fq.exPerson),
                Tags = Tags(fq.tag), ExTags = Tags(fq.exTag),
                Mpa = mpa,
                YearMin = fq.yearMin is > 0 ? fq.yearMin : null,
                YearMax = fq.yearMax is > 0 ? fq.yearMax : null,
                My = MyLists(fq.my),
            };
        }

        private static IReadOnlyList<string> MyLists(string? my) =>
            (my ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToLowerInvariant()).Where(x => x is "seen" or "want" or "rated").Distinct().ToList();

        public bool IsEmpty =>
            Q.Length == 0 && Genres.Count == 0 && ExGenres.Count == 0 && Franchises.Count == 0 && ExFranchises.Count == 0
            && Persons.Count == 0 && ExPersons.Count == 0 && Tags.Count == 0 && ExTags.Count == 0 && Mpa.Count == 0
            && YearMin == null && YearMax == null && My.Count == 0;

        /// <summary>Everything but the text: the facet-count cache keys on the scope, not the search box.</summary>
        public bool HasFacets => !IsEmpty && !(Genres.Count == 0 && ExGenres.Count == 0 && Franchises.Count == 0 && ExFranchises.Count == 0
            && Persons.Count == 0 && ExPersons.Count == 0 && Tags.Count == 0 && ExTags.Count == 0 && Mpa.Count == 0 && YearMin == null && YearMax == null && My.Count == 0);

        /// <summary>A canonical signature for cache keys (order-independent within a dimension).</summary>
        public string Sig
        {
            get
            {
                if (IsEmpty) return "";
                static string J(IEnumerable<string> xs) => string.Join("|", xs.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal));
                static string T(IEnumerable<(TagCategory C, string V)> xs) => string.Join("|", xs.Select(x => $"{(int)x.C}:{x.V.ToLowerInvariant()}").OrderBy(x => x, StringComparer.Ordinal));
                return $"q={Q.ToLowerInvariant()};g={J(Genres)};xg={J(ExGenres)};f={J(Franchises)};xf={J(ExFranchises)};p={J(Persons)};xp={J(ExPersons)};t={T(Tags)};xt={T(ExTags)};m={string.Join(",", Mpa.OrderBy(x => x))};y={YearMin}-{YearMax};my={string.Join(",", My)}";
            }
        }

        // ── applying ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Filter the caller's gated queries. <paramref name="userId"/> feeds the "my" lists (null ⇒ nothing).</summary>
        public static (IQueryable<Movie> Movies, IQueryable<Series> Series) Apply(MovieDb db, IQueryable<Movie> mq, IQueryable<Series> sq, BrowseFilter f, int? userId)
        {
            if (f.IsEmpty) return (mq, sq);

            if (f.Q.Length > 0)
            {
                var v = f.Q;
                // The search box calls this row "in all fields", and a person's name is the field
                // people reach for first. It only ever read the two TITLE columns, so "Tom Hanks"
                // answered "No titles match" for an actor with 34 of them (Eric, 2026-09-03).
                // The people leg is the same reach as `person:` below — normalized credits plus the
                // legacy Actors/Director/Writer strings, for titles the credit tables never got.
                var people = PeopleMatching(db, v);
                mq = mq.Where(m => (m.SimpleTitle != null && m.SimpleTitle.Contains(v)) || (m.Title != null && m.Title.Contains(v))
                    || m.Credits.Any(c => people.Contains(c.PersonId))
                    || (m.Actors != null && m.Actors.Contains(v)) || (m.Director != null && m.Director.Contains(v)) || (m.Writer != null && m.Writer.Contains(v)));
                sq = sq.Where(s => (s.SimpleTitle != null && s.SimpleTitle.Contains(v)) || (s.Title != null && s.Title.Contains(v))
                    || s.Credits.Any(c => people.Contains(c.PersonId))
                    || (s.Actors != null && s.Actors.Contains(v)) || (s.Director != null && s.Director.Contains(v)) || (s.Writer != null && s.Writer.Contains(v)));
            }
            foreach (var g in f.Genres)
            {
                var gg = g;
                mq = mq.Where(m => m.MovieGenres.Any(x => x.Genre.Name == gg) || (m.Genre != null && m.Genre.Contains(gg)));
                sq = sq.Where(s => s.SeriesGenres.Any(x => x.Genre.Name == gg) || (s.Genre != null && s.Genre.Contains(gg)));
            }
            foreach (var g in f.ExGenres)
            {
                var gg = g;
                mq = mq.Where(m => !m.MovieGenres.Any(x => x.Genre.Name == gg) && (m.Genre == null || !m.Genre.Contains(gg)));
                sq = sq.Where(s => !s.SeriesGenres.Any(x => x.Genre.Name == gg) && (s.Genre == null || !s.Genre.Contains(gg)));
            }
            foreach (var fr in f.Franchises) (mq, sq) = WithTag(db, mq, sq, TagCategory.Franchise, fr, include: true);
            foreach (var fr in f.ExFranchises) (mq, sq) = WithTag(db, mq, sq, TagCategory.Franchise, fr, include: false);
            foreach (var (cat, val) in f.Tags) (mq, sq) = WithTag(db, mq, sq, cat, val, include: true);
            foreach (var (cat, val) in f.ExTags) (mq, sq) = WithTag(db, mq, sq, cat, val, include: false);
            foreach (var p in f.Persons)
            {
                var v = p;
                var people = PeopleMatching(db, v);
                mq = mq.Where(m => m.Credits.Any(c => people.Contains(c.PersonId))
                    || (m.Actors != null && m.Actors.Contains(v)) || (m.Director != null && m.Director.Contains(v)) || (m.Writer != null && m.Writer.Contains(v)));
                sq = sq.Where(s => s.Credits.Any(c => people.Contains(c.PersonId))
                    || (s.Actors != null && s.Actors.Contains(v)) || (s.Director != null && s.Director.Contains(v)) || (s.Writer != null && s.Writer.Contains(v)));
            }
            foreach (var p in f.ExPersons)
            {
                var v = p;
                var people = PeopleMatching(db, v);
                mq = mq.Where(m => !m.Credits.Any(c => people.Contains(c.PersonId))
                    && (m.Actors == null || !m.Actors.Contains(v)) && (m.Director == null || !m.Director.Contains(v)) && (m.Writer == null || !m.Writer.Contains(v)));
                sq = sq.Where(s => !s.Credits.Any(c => people.Contains(c.PersonId))
                    && (s.Actors == null || !s.Actors.Contains(v)) && (s.Director == null || !s.Director.Contains(v)) && (s.Writer == null || !s.Writer.Contains(v)));
            }
            if (f.Mpa.Count > 0)
            {
                var buckets = f.Mpa.ToList();
                mq = mq.Where(RatingGate.MovieEffectiveBucketIn(db, buckets));
                sq = sq.Where(RatingGate.SeriesEffectiveBucketIn(db, buckets));
            }
            if (f.YearMin is int lo)
            {
                mq = mq.Where(m => (m.ReleaseDate != null ? m.ReleaseDate.Value.Year : m.ImdbReleaseDate != null ? m.ImdbReleaseDate.Value.Year : 0) >= lo);
                sq = sq.Where(s => (s.StartYear ?? (s.ReleaseDate != null ? s.ReleaseDate.Value.Year : s.ImdbReleaseDate != null ? s.ImdbReleaseDate.Value.Year : 0)) >= lo);
            }
            if (f.YearMax is int hi)
            {
                mq = mq.Where(m => (m.ReleaseDate != null ? m.ReleaseDate.Value.Year : m.ImdbReleaseDate != null ? m.ImdbReleaseDate.Value.Year : 9999) <= hi);
                sq = sq.Where(s => (s.StartYear ?? (s.ReleaseDate != null ? s.ReleaseDate.Value.Year : s.ImdbReleaseDate != null ? s.ImdbReleaseDate.Value.Year : 9999)) <= hi);
            }
            if (f.My.Count > 0)
            {
                if (userId is not int uid) return (mq.Where(m => false), sq.Where(s => false));
                foreach (var list in f.My)
                {
                    var type = list switch { "seen" => "Seen", "want" => "WantToWatch", _ => "Rated" };
                    mq = mq.Where(m => db.Viewings.Any(v => v.UserID == uid && v.ViewingType == type && v.MovieID == m.id));
                    sq = sq.Where(s => db.Viewings.Any(v => v.UserID == uid && v.ViewingType == type && v.SeriesId == s.Id));
                }
            }
            return (mq, sq);
        }

        /// <summary>
        /// The Person ids whose name contains <paramref name="name"/>, as an un-enumerated subquery for a
        /// credit test to sit against.
        /// </summary>
        /// <remarks>
        /// The SHAPE is the point. Resolving the ids in their own subquery lets the credit test ride
        /// IX_MovieCredit_PersonId; joining Person inside the correlated EXISTS instead re-evaluates the
        /// name LIKE per title. Measured against prod (6,292 movies, 106,974 credits, 61,096 people),
        /// free-text "Tom Hanks" over titles+people: 194 ms this way, 420 ms with the join inside.
        /// Title-only — what `q` did before it searched people at all — was 79 ms.
        /// </remarks>
        private static IQueryable<int> PeopleMatching(MovieDb db, string name) =>
            db.People.Where(p => p.DisplayName != null && p.DisplayName.Contains(name)).Select(p => p.Id);

        /// <summary>A tag on the subject's NEWEST insight — a superseded generation's tag does not count (matches GetFranchiseRail).</summary>
        private static (IQueryable<Movie>, IQueryable<Series>) WithTag(MovieDb db, IQueryable<Movie> mq, IQueryable<Series> sq, TagCategory cat, string value, bool include)
        {
            var v = value;
            if (include)
            {
                mq = mq.Where(m => db.TitleTags.Any(t => t.Category == cat && t.Value == v
                    && t.Insight.SubjectKind == InsightSubjectKind.Movie && t.Insight.SubjectId == m.id
                    && t.Insight.GeneratedUtc == db.TitleInsights.Where(x => x.SubjectKind == InsightSubjectKind.Movie && x.SubjectId == m.id).Max(x => x.GeneratedUtc)));
                sq = sq.Where(s => db.TitleTags.Any(t => t.Category == cat && t.Value == v
                    && t.Insight.SubjectKind == InsightSubjectKind.Series && t.Insight.SubjectId == s.Id
                    && t.Insight.GeneratedUtc == db.TitleInsights.Where(x => x.SubjectKind == InsightSubjectKind.Series && x.SubjectId == s.Id).Max(x => x.GeneratedUtc)));
            }
            else
            {
                mq = mq.Where(m => !db.TitleTags.Any(t => t.Category == cat && t.Value == v
                    && t.Insight.SubjectKind == InsightSubjectKind.Movie && t.Insight.SubjectId == m.id
                    && t.Insight.GeneratedUtc == db.TitleInsights.Where(x => x.SubjectKind == InsightSubjectKind.Movie && x.SubjectId == m.id).Max(x => x.GeneratedUtc)));
                sq = sq.Where(s => !db.TitleTags.Any(t => t.Category == cat && t.Value == v
                    && t.Insight.SubjectKind == InsightSubjectKind.Series && t.Insight.SubjectId == s.Id
                    && t.Insight.GeneratedUtc == db.TitleInsights.Where(x => x.SubjectKind == InsightSubjectKind.Series && x.SubjectId == s.Id).Max(x => x.GeneratedUtc)));
            }
            return (mq, sq);
        }

        // ── facet counts ────────────────────────────────────────────────────────────────────────

        public sealed record FacetOption(string Value, string Label, int Count);

        public sealed class FacetCounts
        {
            public List<FacetOption> Types { get; init; } = new();
            public List<FacetOption> Genres { get; init; } = new();
            public List<FacetOption> Franchises { get; init; } = new();
            public List<FacetOption> Mpa { get; init; } = new();
            public List<FacetOption> Decades { get; init; } = new();
            /// <summary>By tag token (subgenre / mood / theme / setting / era / style / content), top values by count.</summary>
            public Dictionary<string, List<FacetOption>> Tags { get; init; } = new();
            public int Total { get; init; }
            public long ApproxBytes => 512 + (Types.Count + Genres.Count + Franchises.Count + Mpa.Count + Decades.Count + Tags.Values.Sum(t => t.Count)) * 48L;
        }

        public const int TagFacetTop = 40;
        public const int FranchiseMin = 2;

        /// <summary>
        /// Counts over the caller's gated scope (the Long Box rule: counts describe the scope, not the
        /// current selection — one pass, cached by the controller). Six aggregate queries per kind, all
        /// group-bys in SQL, plus one light pass for years and MPA (the effective bucket is a COALESCE over
        /// three lookups; resolved in memory from the RatingMaps once rather than per row in SQL).
        /// </summary>
        public static async Task<FacetCounts> CountAsync(MovieDb db, IQueryable<Movie> mq, IQueryable<Series> sq, int miscCount, CancellationToken ct = default)
        {
            var movieIds = mq.Select(m => m.id);
            var seriesIds = sq.Select(s => s.Id);

            var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in await mq.SelectMany(m => m.MovieGenres).GroupBy(x => x.Genre.Name).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct))
                if (!string.IsNullOrWhiteSpace(g.Key)) genreCounts[g.Key] = genreCounts.GetValueOrDefault(g.Key) + g.C;
            foreach (var g in await sq.SelectMany(s => s.SeriesGenres).GroupBy(x => x.Genre.Name).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct))
                if (!string.IsNullOrWhiteSpace(g.Key)) genreCounts[g.Key] = genreCounts.GetValueOrDefault(g.Key) + g.C;

            async Task<Dictionary<string, int>> TagCountsAsync(TagCategory cat)
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var movieTags = await db.TitleTags
                    .Where(t => t.Category == cat && t.Value != "" && t.Insight.SubjectKind == InsightSubjectKind.Movie && movieIds.Contains(t.Insight.SubjectId)
                        && t.Insight.GeneratedUtc == db.TitleInsights.Where(x => x.SubjectKind == InsightSubjectKind.Movie && x.SubjectId == t.Insight.SubjectId).Max(x => x.GeneratedUtc))
                    .Select(t => new { t.Value, t.Insight.SubjectId }).Distinct()
                    .GroupBy(t => t.Value).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct);
                foreach (var t in movieTags) counts[t.Key] = counts.GetValueOrDefault(t.Key) + t.C;
                var seriesTags = await db.TitleTags
                    .Where(t => t.Category == cat && t.Value != "" && t.Insight.SubjectKind == InsightSubjectKind.Series && seriesIds.Contains(t.Insight.SubjectId)
                        && t.Insight.GeneratedUtc == db.TitleInsights.Where(x => x.SubjectKind == InsightSubjectKind.Series && x.SubjectId == t.Insight.SubjectId).Max(x => x.GeneratedUtc))
                    .Select(t => new { t.Value, t.Insight.SubjectId }).Distinct()
                    .GroupBy(t => t.Value).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct);
                foreach (var t in seriesTags) counts[t.Key] = counts.GetValueOrDefault(t.Key) + t.C;
                return counts;
            }

            var franchiseCounts = await TagCountsAsync(TagCategory.Franchise);
            var tags = new Dictionary<string, List<FacetOption>>();
            foreach (var (token, cat) in TagTokens)
            {
                var c = await TagCountsAsync(cat);
                tags[token] = c.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(TagFacetTop).Select(kv => new FacetOption(kv.Key, Humanize(kv.Key), kv.Value)).ToList();
            }

            // One light pass for years, MPA and the type buckets.
            var lightMovies = await mq.Select(m => new { m.NormalizedTitleType, Year = m.ReleaseDate != null ? m.ReleaseDate.Value.Year : m.ImdbReleaseDate != null ? m.ImdbReleaseDate.Value.Year : (int?)null, m.MpaaRating, m.Rating, m.MpaaRatingInferred }).ToListAsync(ct);
            var lightSeries = await sq.Select(s => new { Year = s.StartYear ?? (s.ReleaseDate != null ? s.ReleaseDate.Value.Year : s.ImdbReleaseDate != null ? s.ImdbReleaseDate.Value.Year : (int?)null), s.MpaaRating, s.Rating, s.MpaaRatingInferred }).ToListAsync(ct);
            var maps = (await db.RatingMaps.Where(rm => rm.MPARatingID >= 1 && rm.MPARatingID <= RatingGate.MaxRealBucket).Select(rm => new { rm.MovieRating, rm.MPARatingID }).ToListAsync(ct))
                .GroupBy(x => x.MovieRating ?? "").ToDictionary(g => g.Key, g => g.First().MPARatingID, StringComparer.OrdinalIgnoreCase);
            var mpaNames = await db.RatingMpas.Where(r => r.RatingID >= 1 && r.RatingID <= RatingGate.MaxRealBucket).ToDictionaryAsync(r => r.RatingID, r => r.MPAName ?? "", ct);
            int Bucket(string? a, string? b, string? c) =>
                (a != null && maps.TryGetValue(a, out var x)) ? x : (b != null && maps.TryGetValue(b, out var y)) ? y : (c != null && maps.TryGetValue(c, out var z)) ? z : RatingGate.UnknownRatingId;

            var decades = new Dictionary<int, int>();
            var mpa = new Dictionary<int, int>();
            var types = new Dictionary<string, int>();
            foreach (var m in lightMovies)
            {
                var key = m.NormalizedTitleType == NormalizedTitleType.Short ? "Short" : "Movies";
                types[key] = types.GetValueOrDefault(key) + 1;
                if (m.Year is int y && y > 1800) decades[y / 10 * 10] = decades.GetValueOrDefault(y / 10 * 10) + 1;
                var b = Bucket(m.MpaaRating, m.Rating, m.MpaaRatingInferred); mpa[b] = mpa.GetValueOrDefault(b) + 1;
            }
            foreach (var s in lightSeries)
            {
                types["Series"] = types.GetValueOrDefault("Series") + 1;
                if (s.Year is int y && y > 1800) decades[y / 10 * 10] = decades.GetValueOrDefault(y / 10 * 10) + 1;
                var b = Bucket(s.MpaaRating, s.Rating, s.MpaaRatingInferred); mpa[b] = mpa.GetValueOrDefault(b) + 1;
            }
            if (miscCount > 0) types["Misc"] = miscCount;

            // NC-17 and X are one stop; the unknown bucket is not a rating anyone searches for.
            var mpaOptions = new List<FacetOption>();
            foreach (var id in new[] { 1, 2, 3, 4, 5 })
            {
                var count = mpa.GetValueOrDefault(id) + (id == 5 ? mpa.GetValueOrDefault(6) : 0);
                if (count > 0) mpaOptions.Add(new FacetOption(id.ToString(), mpaNames.GetValueOrDefault(id, id.ToString()), count));
            }

            return new FacetCounts
            {
                Total = lightMovies.Count + lightSeries.Count + miscCount,
                Types = new[] { "Movies", "Series", "Short", "Misc" }.Where(types.ContainsKey).Select(t => new FacetOption(t, t, types[t])).ToList(),
                Genres = genreCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => new FacetOption(kv.Key, kv.Key, kv.Value)).ToList(),
                Franchises = franchiseCounts.Where(kv => kv.Value >= FranchiseMin).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new FacetOption(kv.Key, BrowseGroups.FranchiseLabel(kv.Key), kv.Value)).ToList(),
                Mpa = mpaOptions,
                Decades = decades.OrderBy(kv => kv.Key).Select(kv => new FacetOption(kv.Key.ToString(), kv.Key + "s", kv.Value)).ToList(),
                Tags = tags,
            };
        }

        /// <summary>"post-apocalypse" → "Post apocalypse"; "neo-noir" → "Neo noir".</summary>
        public static string Humanize(string tag)
        {
            var words = tag.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) return tag;
            var s = string.Join(" ", words);
            return char.ToUpperInvariant(s[0]) + s[1..];
        }
    }
}
