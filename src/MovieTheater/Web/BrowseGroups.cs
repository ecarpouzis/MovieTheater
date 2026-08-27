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
    /// The grouped browse behind the catalog package's Extended / Shelves / Newspaper views for
    /// Movies/TV — the two-phase protocol the Books host already speaks (heads, then bands).
    ///
    /// <para><b>One light index per scope, then everything from memory.</b> The library is a few
    /// thousand titles, and the expensive part of any browse query is the age gate (three correlated
    /// rating lookups per row). Paying it once per scope — one pass over the gated queries producing
    /// a <see cref="LightRow"/> per (title, group) — and caching the result (the controller does, keyed
    /// by user facts + filter + mode) makes heads, the letter rail and every band a memory operation:
    /// a 20-genre band that scanned 15k gated rows per request becomes a sort of a few lists. The
    /// alternative (a windowed SQL query per group per band) still pays the gate over every member of
    /// every group on every scroll.</para>
    ///
    /// <list type="bullet">
    /// <item><b>Heads</b>: every group in scope with its true size, in display order — <c>totalGroups</c>
    /// is the head count and the letter rail is derived from it.</item>
    /// <item><b>Band</b>: for a page of heads, each group's members ordered by the browse sort with the
    /// flat endpoints' tiebreak (<c>SimpleTitle, Kind, Id</c>) and windowed by <c>perGroupSkip/Top</c>,
    /// so a window never dupes or skips across its boundaries. The controller hydrates ids to cards
    /// afterwards, exactly as <c>PageMergedAsync</c> does.</item>
    /// </list>
    ///
    /// Group modes (R9 S8 — the audited axis set):
    /// <list type="bullet">
    /// <item><c>genre</c> — a title sits in every genre it carries.</item>
    /// <item><c>decade</c> — release year, series' start year, misc's year; undated titles have no decade.</item>
    /// <item><c>franchise</c> — the Franchise tags on each subject's NEWEST insight (a superseded
    /// generation's tag does not count, matching <c>GetFranchiseRail</c>); groups of one are dropped.</item>
    /// <item><c>type</c> — the four Type-scope buckets (Movies / Series / Short / Misc), spelled exactly
    /// as the rail's Type facet writes them.</item>
    /// <item><c>director</c> — every <c>CreditRole.Director</c> credit, by the person's display name.</item>
    /// <item><c>mpa</c> — the EFFECTIVE MPA bucket, resolved the way the age gate resolves it (real →
    /// legacy → inferred) and folded onto the rail's five stops (NC-17 covers X). A title whose rating
    /// does not resolve is left out, exactly as an undated title has no decade: the rail has no NR stop
    /// (Eric), so an axis must not offer a shelf whose header could not drill anywhere.</item>
    /// <item>the AI tag categories — <c>subgenre</c> / <c>mood</c> / <c>era</c> / <c>setting</c> and the rest
    /// of <see cref="BrowseFilter.TagTokens"/> — read off each subject's newest insight. Unlike
    /// franchises these KEEP their singletons: one film really is that mood.</item>
    /// <item><c>my</c> — the CALLER's own lists (Seen / Want to watch / Rated). The only user-dependent
    /// axis: <see cref="BrowseCacheKeys"/> puts the user id in the key for it and the warmer never
    /// touches it.</item>
    /// </list>
    /// <c>letter</c> was RETIRED in R9 S8 — the A–Z strip is the letter axis, and a shelf per letter was
    /// the same index drawn twice.
    ///
    /// Pure and static: it takes the caller's ALREADY-GATED queries (quarantine, series exclusion and
    /// the age gate live on the base queries in the controller) and a light list of the misc videos in
    /// scope, so it runs against SQLite in the tests as written.
    /// </summary>
    public static class BrowseGroups
    {
        public const int DefaultGroupsTop = 20;
        public const int MaxGroupsTop = 50;
        /// <summary>Decade / type / MPA / my-list groups are whole-library slices; a band of them is most of the catalog.</summary>
        public const int MaxWideGroupsTop = 12;
        public const int DefaultPerGroupTop = 48;
        public const int MaxPerGroupTop = 500;

        public sealed record Head(string Key, string Label, int Count);
        /// <summary>A misc video in scope, the three facts the group modes read.</summary>
        public sealed record MiscLight(int Id, string? SimpleTitle, string? Title, int? Year);
        public sealed record Member(string Kind, int Id);
        public sealed record BandResult(IReadOnlyDictionary<string, List<Member>> Members);

        /// <summary>The light row every band orders: one per (title, group).</summary>
        public sealed class LightRow
        {
            public string Kind { get; set; } = "movie";
            public int Id { get; set; }
            public string? SimpleTitle { get; set; }
            public string GroupKey { get; set; } = "";
            public decimal? Imdb { get; set; }
            public int? Rt { get; set; }
            public int? Popcorn { get; set; }
            public DateTime? Added { get; set; }
        }

        /// <summary>The cached product of one pass over a scope: the rows and the heads derived from them.</summary>
        public sealed class GroupIndex
        {
            public string By { get; init; } = "genre";
            public IReadOnlyList<LightRow> Rows { get; init; } = Array.Empty<LightRow>();
            public IReadOnlyList<Head> Heads { get; init; } = Array.Empty<Head>();
            /// <summary>Rows per group key, in no particular order (bands sort).</summary>
            public IReadOnlyDictionary<string, List<LightRow>> ByKey { get; init; } = new Dictionary<string, List<LightRow>>();
            /// <summary>Rough byte size, for a size-budgeted cache.</summary>
            public long ApproxBytes => 256 + Rows.Count * 120L + Heads.Count * 96L;
        }

        /// <summary>The viewer's own lists, in the order the rail lists them; the keys the `my=` param uses.</summary>
        public static readonly IReadOnlyList<(string Key, string Label, string ViewingType)> MyLists = new[]
        {
            ("seen", "Seen", "Seen"),
            ("want", "Want to watch", "WantToWatch"),
            ("rated", "Rated", "Rated"),
        };

        /// <summary>The Type-scope buckets as group keys — the same spelling the rail's Type facet writes.</summary>
        public static readonly IReadOnlyList<string> TypeKeys = new[] { "Movies", "Series", "Short", "Misc" };

        /// <summary>The rail's five MPA stops, in certificate order. 6 (X) folds onto 5 (NC-17).</summary>
        public static readonly IReadOnlyList<int> MpaStops = new[] { 1, 2, 3, 4, 5 };

        public static string NormalizeGroupBy(string? by)
        {
            var v = (by ?? "").Trim().ToLowerInvariant();
            if (BrowseFilter.TagTokens.ContainsKey(v)) return v;
            return v switch
            {
                "decade" => "decade",
                "franchise" => "franchise",
                "type" => "type",
                "director" => "director",
                "mpa" => "mpa",
                "my" => "my",
                _ => "genre",
            };
        }

        /// <summary>True when the axis reads the CALLER's own lists — the cache key must carry the user id.</summary>
        public static bool IsUserDependent(string by) => by == "my";

        /// <summary>
        /// Axes whose every group is a whole-library slice: a band of them is most of the catalog, so the
        /// heads page is capped much lower.
        /// </summary>
        public static bool IsWide(string by) => by is "decade" or "type" or "mpa" or "my";

        /// <summary>Axes whose heads are in label order, so an A–Z rail over them means something.</summary>
        public static bool IsAlphabetical(string by) => !IsWide(by);

        public static int CapGroupsTop(string by, int requested)
        {
            var cap = IsWide(by) ? MaxWideGroupsTop : MaxGroupsTop;
            return requested <= 0 ? Math.Min(DefaultGroupsTop, cap) : Math.Min(requested, cap);
        }

        public static int CapPerGroupTop(int requested) => requested <= 0 ? DefaultPerGroupTop : Math.Min(requested, MaxPerGroupTop);

        public static string DecadeKey(int year) => (year / 10 * 10).ToString();
        public static string DecadeLabel(string key) => key + "s";

        /// <summary>
        /// A franchise tag as a shelf title: "studio-ghibli" / "a nightmare on elm street" → title case;
        /// a lone word of up to three letters reads as an acronym ("mcu" → "MCU"; "bond" stays "Bond").
        /// </summary>
        public static string FranchiseLabel(string tag)
        {
            var words = tag.Split(new[] { '-', ' ', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 1 && words[0].Length <= 3) return words[0].ToUpperInvariant();
            return string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        // ── The index ─────────────────────────────────────────────────────────────────────────────

        private static IQueryable<LightRow> MovieRows(IQueryable<Movie> mq) => mq.Select(m => new LightRow
        {
            Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle,
            Imdb = m.ImdbRatingScraped ?? m.imdbRating, Rt = m.RtTomatometer, Popcorn = m.RtPopcornmeter, Added = m.UploadedDate,
        });

        private static IQueryable<LightRow> SeriesRows(IQueryable<Series> sq) => sq.Select(s => new LightRow
        {
            Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle,
            Imdb = s.ImdbRatingScraped ?? s.imdbRating, Rt = s.RtTomatometer, Popcorn = s.RtPopcornmeter, Added = s.UploadedDate,
        });

        private static LightRow MiscRow(MiscLight v, string key) => new() { Kind = "misc", Id = v.Id, SimpleTitle = v.SimpleTitle ?? v.Title, GroupKey = key };

        private static LightRow WithKey(LightRow r, string key) => new()
        {
            Kind = r.Kind, Id = r.Id, SimpleTitle = r.SimpleTitle, GroupKey = key, Imdb = r.Imdb, Rt = r.Rt, Popcorn = r.Popcorn, Added = r.Added,
        };

        /// <summary>Tags of one category on each subject's newest insight.</summary>
        private static IQueryable<TitleTag> NewestTags(MovieDb db, TagCategory category) =>
            db.TitleTags.Where(t => t.Category == category && t.Value != ""
                && t.Insight.GeneratedUtc == db.TitleInsights
                    .Where(x => x.SubjectKind == t.Insight.SubjectKind && x.SubjectId == t.Insight.SubjectId)
                    .Max(x => x.GeneratedUtc));

        /// <summary>
        /// One pass over the scope: every (title, group) light row, then the heads from them.
        /// <paramref name="userId"/> is only read by the <c>my</c> axis (the caller's own lists); every
        /// other axis depends on nothing but the age gate the caller already applied, which is what
        /// makes them shareable and warmable (<see cref="BrowseCacheKeys"/>).
        /// </summary>
        public static async Task<GroupIndex> BuildIndexAsync(MovieDb db, IQueryable<Movie> mq, IQueryable<Series> sq, IReadOnlyList<MiscLight> misc, string by, int? userId = null, CancellationToken ct = default)
        {
            var rows = new List<LightRow>();

            // The tag axes (franchise + every TagCategory the rail offers) are ONE shape: the values on
            // each subject's newest insight, joined back to the light rows.
            async Task<List<LightRow>> TagRowsAsync(TagCategory category)
            {
                var movieIds = mq.Select(m => m.id);
                var seriesIds = sq.Select(s => s.Id);
                var tags = NewestTags(db, category);
                var movieTags = await tags.Where(t => t.Insight.SubjectKind == InsightSubjectKind.Movie && movieIds.Contains(t.Insight.SubjectId))
                    .Select(t => new { t.Value, t.Insight.SubjectId }).Distinct().ToListAsync(ct);
                var seriesTags = await tags.Where(t => t.Insight.SubjectKind == InsightSubjectKind.Series && seriesIds.Contains(t.Insight.SubjectId))
                    .Select(t => new { t.Value, t.Insight.SubjectId }).Distinct().ToListAsync(ct);
                var mIds = movieTags.Select(t => t.SubjectId).Distinct().ToList();
                var sIds = seriesTags.Select(t => t.SubjectId).Distinct().ToList();
                var mById = (mIds.Count > 0 ? await MovieRows(mq.Where(m => mIds.Contains(m.id))).ToListAsync(ct) : new List<LightRow>()).ToDictionary(r => r.Id);
                var sById = (sIds.Count > 0 ? await SeriesRows(sq.Where(s => sIds.Contains(s.Id))).ToListAsync(ct) : new List<LightRow>()).ToDictionary(r => r.Id);
                var list = new List<LightRow>();
                foreach (var t in movieTags) if (mById.TryGetValue(t.SubjectId, out var r)) list.Add(WithKey(r, t.Value));
                foreach (var t in seriesTags) if (sById.TryGetValue(t.SubjectId, out var r)) list.Add(WithKey(r, t.Value));
                return list;
            }

            if (BrowseFilter.TagTokens.TryGetValue(by, out var tagCategory))
            {
                // A subgenre / mood / era / setting of ONE title is still that subgenre — no singleton floor here.
                rows = await TagRowsAsync(tagCategory);
            }
            else
            switch (by)
            {
                case "decade":
                {
                    rows.AddRange(await mq.Where(m => m.ReleaseDate != null || m.ImdbReleaseDate != null)
                        .Select(m => new { Row = m, Decade = (m.ReleaseDate ?? m.ImdbReleaseDate)!.Value.Year / 10 * 10 })
                        .Select(x => new LightRow
                        {
                            Kind = "movie", Id = x.Row.id, SimpleTitle = x.Row.SimpleTitle, GroupKey = x.Decade.ToString(),
                            Imdb = x.Row.ImdbRatingScraped ?? x.Row.imdbRating, Rt = x.Row.RtTomatometer, Popcorn = x.Row.RtPopcornmeter, Added = x.Row.UploadedDate,
                        }).ToListAsync(ct));
                    rows.AddRange(await sq.Where(s => s.StartYear != null || s.ReleaseDate != null || s.ImdbReleaseDate != null)
                        .Select(s => new { Row = s, Decade = (s.StartYear ?? (s.ReleaseDate ?? s.ImdbReleaseDate)!.Value.Year) / 10 * 10 })
                        .Select(x => new LightRow
                        {
                            Kind = "series", Id = x.Row.Id, SimpleTitle = x.Row.SimpleTitle, GroupKey = x.Decade.ToString(),
                            Imdb = x.Row.ImdbRatingScraped ?? x.Row.imdbRating, Rt = x.Row.RtTomatometer, Popcorn = x.Row.RtPopcornmeter, Added = x.Row.UploadedDate,
                        }).ToListAsync(ct));
                    foreach (var v in misc) if (v.Year is int y) rows.Add(MiscRow(v, DecadeKey(y)));
                    break;
                }
                case "franchise":
                {
                    rows = await TagRowsAsync(TagCategory.Franchise);
                    // A franchise of one is not a shelf — the same floor GetFranchiseRail applies.
                    var keep = rows.GroupBy(r => r.GroupKey, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    rows = rows.Where(r => keep.Contains(r.GroupKey)).ToList();
                    break;
                }
                case "type":
                {
                    rows.AddRange(await mq.Select(m => new LightRow
                    {
                        Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle,
                        GroupKey = m.NormalizedTitleType == NormalizedTitleType.Short ? "Short" : "Movies",
                        Imdb = m.ImdbRatingScraped ?? m.imdbRating, Rt = m.RtTomatometer, Popcorn = m.RtPopcornmeter, Added = m.UploadedDate,
                    }).ToListAsync(ct));
                    foreach (var r in await SeriesRows(sq).ToListAsync(ct)) rows.Add(WithKey(r, "Series"));
                    foreach (var v in misc) rows.Add(MiscRow(v, "Misc"));
                    break;
                }
                case "director":
                {
                    rows.AddRange(await mq.SelectMany(m => m.Credits.Where(c => c.Role == CreditRole.Director), (m, c) => new LightRow
                    {
                        Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, GroupKey = c.Person.DisplayName ?? "",
                        Imdb = m.ImdbRatingScraped ?? m.imdbRating, Rt = m.RtTomatometer, Popcorn = m.RtPopcornmeter, Added = m.UploadedDate,
                    }).ToListAsync(ct));
                    rows.AddRange(await sq.SelectMany(s => s.Credits.Where(c => c.Role == CreditRole.Director), (s, c) => new LightRow
                    {
                        Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, GroupKey = c.Person.DisplayName ?? "",
                        Imdb = s.ImdbRatingScraped ?? s.imdbRating, Rt = s.RtTomatometer, Popcorn = s.RtPopcornmeter, Added = s.UploadedDate,
                    }).ToListAsync(ct));
                    rows = rows.Where(r => !string.IsNullOrWhiteSpace(r.GroupKey)).ToList();
                    break;
                }
                case "mpa":
                {
                    // The effective bucket is a COALESCE over three lookups; resolving it from the
                    // RatingMaps ONCE in memory (BrowseFilter.CountAsync's trick) beats three correlated
                    // subqueries per row.
                    var maps = (await db.RatingMaps.Where(rm => rm.MPARatingID >= 1 && rm.MPARatingID <= RatingGate.MaxRealBucket)
                            .Select(rm => new { rm.MovieRating, rm.MPARatingID }).ToListAsync(ct))
                        .GroupBy(x => x.MovieRating ?? "").ToDictionary(g => g.Key, g => g.First().MPARatingID, StringComparer.OrdinalIgnoreCase);
                    int? Bucket(string? a, string? b, string? c)
                    {
                        foreach (var v in new[] { a, b, c })
                            if (v != null && maps.TryGetValue(v, out var id)) return id == 6 ? 5 : id; // X reads as NC-17
                        return null;
                    }
                    var lightMovies = await mq.Select(m => new
                    {
                        m.id, m.SimpleTitle, Imdb = m.ImdbRatingScraped ?? m.imdbRating, m.RtTomatometer, m.RtPopcornmeter, m.UploadedDate,
                        m.MpaaRating, m.Rating, m.MpaaRatingInferred,
                    }).ToListAsync(ct);
                    foreach (var m in lightMovies)
                        if (Bucket(m.MpaaRating, m.Rating, m.MpaaRatingInferred) is int b)
                            rows.Add(new LightRow { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, GroupKey = b.ToString(), Imdb = m.Imdb, Rt = m.RtTomatometer, Popcorn = m.RtPopcornmeter, Added = m.UploadedDate });
                    var lightSeries = await sq.Select(s => new
                    {
                        s.Id, s.SimpleTitle, Imdb = s.ImdbRatingScraped ?? s.imdbRating, s.RtTomatometer, s.RtPopcornmeter, s.UploadedDate,
                        s.MpaaRating, s.Rating, s.MpaaRatingInferred,
                    }).ToListAsync(ct);
                    foreach (var s in lightSeries)
                        if (Bucket(s.MpaaRating, s.Rating, s.MpaaRatingInferred) is int b)
                            rows.Add(new LightRow { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, GroupKey = b.ToString(), Imdb = s.Imdb, Rt = s.RtTomatometer, Popcorn = s.RtPopcornmeter, Added = s.UploadedDate });
                    break;
                }
                case "my":
                {
                    // NEVER cached across users — the caller's own rows are the whole axis.
                    if (userId is not int uid) break;
                    var movieIds = mq.Select(m => m.id);
                    var seriesIds = sq.Select(s => s.Id);
                    var mine = await db.Viewings.Where(v => v.UserID == uid)
                        .Select(v => new { v.MovieID, v.SeriesId, v.ViewingType }).ToListAsync(ct);
                    var wantMovies = mine.Where(v => v.MovieID != null).Select(v => v.MovieID!.Value).Distinct().ToList();
                    var wantSeries = mine.Where(v => v.SeriesId != null).Select(v => v.SeriesId!.Value).Distinct().ToList();
                    var mById = (wantMovies.Count > 0 ? await MovieRows(mq.Where(m => wantMovies.Contains(m.id))).ToListAsync(ct) : new List<LightRow>()).ToDictionary(r => r.Id);
                    var sById = (wantSeries.Count > 0 ? await SeriesRows(sq.Where(s => wantSeries.Contains(s.Id))).ToListAsync(ct) : new List<LightRow>()).ToDictionary(r => r.Id);
                    var seen = new HashSet<string>();
                    foreach (var v in mine)
                    {
                        var list = MyLists.FirstOrDefault(l => l.ViewingType == v.ViewingType);
                        if (list.Key == null) continue;
                        if (v.MovieID is int mid && mById.TryGetValue(mid, out var mr) && seen.Add($"{list.Key}:m{mid}")) rows.Add(WithKey(mr, list.Key));
                        else if (v.SeriesId is int sid && sById.TryGetValue(sid, out var sr) && seen.Add($"{list.Key}:s{sid}")) rows.Add(WithKey(sr, list.Key));
                    }
                    break;
                }
                default:
                {
                    rows.AddRange(await mq.SelectMany(m => m.MovieGenres, (m, g) => new LightRow
                    {
                        Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, GroupKey = g.Genre.Name,
                        Imdb = m.ImdbRatingScraped ?? m.imdbRating, Rt = m.RtTomatometer, Popcorn = m.RtPopcornmeter, Added = m.UploadedDate,
                    }).ToListAsync(ct));
                    rows.AddRange(await sq.SelectMany(s => s.SeriesGenres, (s, g) => new LightRow
                    {
                        Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, GroupKey = g.Genre.Name,
                        Imdb = s.ImdbRatingScraped ?? s.imdbRating, Rt = s.RtTomatometer, Popcorn = s.RtPopcornmeter, Added = s.UploadedDate,
                    }).ToListAsync(ct));
                    rows = rows.Where(r => !string.IsNullOrWhiteSpace(r.GroupKey)).ToList();
                    break;
                }
            }

            // The certificate names come from the lookup table, not from a hard-coded ladder.
            var mpaNames = by == "mpa"
                ? await db.RatingMpas.Where(r => r.RatingID >= 1 && r.RatingID <= RatingGate.MaxRealBucket)
                    .ToDictionaryAsync(r => r.RatingID, r => string.IsNullOrWhiteSpace(r.MPAName) ? r.RatingID.ToString() : r.MPAName!, ct)
                : new Dictionary<int, string>();

            var byKey = new Dictionary<string, List<LightRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (!byKey.TryGetValue(r.GroupKey, out var list)) byKey[r.GroupKey] = list = new List<LightRow>();
                list.Add(r);
            }
            IEnumerable<Head> heads;
            if (BrowseFilter.TagTokens.ContainsKey(by))
                heads = byKey.Select(kv => new Head(kv.Key, BrowseFilter.Humanize(kv.Key), kv.Value.Count)).OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase);
            else
                heads = by switch
                {
                    "decade" => byKey.OrderByDescending(kv => int.TryParse(kv.Key, out var d) ? d : int.MinValue).Select(kv => new Head(kv.Key, DecadeLabel(kv.Key), kv.Value.Count)),
                    "franchise" => byKey.Select(kv => new Head(kv.Key, FranchiseLabel(kv.Key), kv.Value.Count)).OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase),
                    // The three fixed-order axes: a certificate ladder, the Type buckets and the viewer's
                    // lists all have ONE right order, and it is not alphabetical.
                    "type" => TypeKeys.Where(byKey.ContainsKey).Select(k => new Head(k, k, byKey[k].Count)),
                    "mpa" => MpaStops.Select(id => id.ToString()).Where(byKey.ContainsKey).Select(k => new Head(k, mpaNames.GetValueOrDefault(int.Parse(k), k), byKey[k].Count)),
                    "my" => MyLists.Where(l => byKey.ContainsKey(l.Key)).Select(l => new Head(l.Key, l.Label, byKey[l.Key].Count)),
                    _ => byKey.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => new Head(kv.Key, kv.Key, kv.Value.Count)),
                };
            return new GroupIndex { By = by, Rows = rows, Heads = heads.ToList(), ByKey = byKey };
        }

        /// <summary>The heads alone (an index build; the controller caches the whole index instead).</summary>
        public static async Task<List<Head>> HeadsAsync(MovieDb db, IQueryable<Movie> mq, IQueryable<Series> sq, IReadOnlyList<MiscLight> misc, string by, int? userId = null, CancellationToken ct = default)
            => (await BuildIndexAsync(db, mq, sq, misc, by, userId, ct)).Heads.ToList();

        /// <summary>
        /// Letter → first GROUP index over the heads' order (the grouped views' letter rail). Only the
        /// alphabetical axes have one: a rail over decades, the Type buckets, the certificate ladder or
        /// the viewer's lists would point at letters that are not in that order.
        /// </summary>
        public static List<(string Letter, int FirstIndex)> GroupLetters(IReadOnlyList<Head> heads, string by)
        {
            if (!IsAlphabetical(by)) return new List<(string, int)>();
            var seen = new HashSet<string>();
            var result = new List<(string, int)>();
            for (int i = 0; i < heads.Count; i++)
            {
                var letter = LetterBuckets.LetterOf(heads[i].Label);
                if (seen.Add(letter)) result.Add((letter, i));
            }
            return result;
        }

        // ── Bands ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The ordered members of each requested group, windowed, from an index. <paramref name="sort"/>
        /// is the normalized browse sort (alpha | added | imdb | rt | popcorn | random); every order
        /// ends in the <c>SimpleTitle, Kind, Id</c> tiebreak. Random is a seeded shuffle, stable for
        /// the same seed, so a band re-fetch lands on the same members.
        /// </summary>
        public static BandResult Band(GroupIndex index, IReadOnlyList<string> keys, string sort, int seed, int perGroupTop, int perGroupSkip)
        {
            var byKey = new Dictionary<string, List<Member>>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                var members = index.ByKey.TryGetValue(key, out var list) ? list : (IEnumerable<LightRow>)Array.Empty<LightRow>();
                byKey[key] = Order(members, sort, seed).Skip(perGroupSkip).Take(perGroupTop).Select(r => new Member(r.Kind, r.Id)).ToList();
            }
            return new BandResult(byKey);
        }

        /// <summary>Build the index and band it — the convenience the tests use.</summary>
        public static async Task<BandResult> BandAsync(
            MovieDb db, IQueryable<Movie> mq, IQueryable<Series> sq, IReadOnlyList<MiscLight> misc,
            string by, IReadOnlyList<string> keys, string sort, int seed, int perGroupTop, int perGroupSkip, int? userId = null, CancellationToken ct = default)
            => Band(await BuildIndexAsync(db, mq, sq, misc, by, userId, ct), keys, sort, seed, perGroupTop, perGroupSkip);

        /// <summary>The browse sorts, in memory, with the flat endpoints' tiebreak. Exposed for the tests.</summary>
        public static IEnumerable<LightRow> Order(IEnumerable<LightRow> rows, string sort, int seed)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            IOrderedEnumerable<LightRow> ordered = sort switch
            {
                "added" => rows.OrderByDescending(r => r.Added ?? DateTime.MinValue).ThenBy(r => r.SimpleTitle, cmp),
                "imdb" => rows.OrderByDescending(r => r.Imdb ?? -1m).ThenBy(r => r.SimpleTitle, cmp),
                "rt" => rows.OrderByDescending(r => r.Rt ?? -1).ThenBy(r => r.SimpleTitle, cmp),
                "popcorn" => rows.OrderByDescending(r => r.Popcorn ?? -1).ThenBy(r => r.SimpleTitle, cmp),
                "random" => rows.OrderBy(r => Shuffle(r, seed)).ThenBy(r => r.SimpleTitle, cmp),
                _ => rows.OrderBy(r => r.SimpleTitle, cmp),
            };
            return ordered.ThenBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Id);
        }

        /// <summary>Seeded, kind-salted shuffle key — the same title lands in the same place for the same seed.</summary>
        private static long Shuffle(LightRow r, int seed)
        {
            var salt = r.Kind == "series" ? 1_000_003L : r.Kind == "misc" ? 2_000_003L : 0L;
            unchecked { return ((r.Id + seed + salt) * 2654435761L) % 4294967311L; }
        }
    }
}
