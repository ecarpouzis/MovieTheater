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
    /// Group modes: <c>genre</c> (a title sits in every genre it carries), <c>decade</c> (release year,
    /// series' start year, misc's year; undated titles have no decade), <c>franchise</c> (the Franchise
    /// tags on each subject's NEWEST insight — a superseded generation's tag does not count, matching
    /// <c>GetFranchiseRail</c>; groups of one are dropped), <c>letter</c> (the A–Z bucket of the sort
    /// key, "#" for the rest).
    ///
    /// Pure and static: it takes the caller's ALREADY-GATED queries (quarantine, series exclusion and
    /// the age gate live on the base queries in the controller) and a light list of the misc videos in
    /// scope, so it runs against SQLite in the tests as written.
    /// </summary>
    public static class BrowseGroups
    {
        public const int DefaultGroupsTop = 20;
        public const int MaxGroupsTop = 50;
        /// <summary>Decade and letter groups are whole-library slices; a band of them is most of the catalog.</summary>
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

        public static string NormalizeGroupBy(string? by) => (by ?? "").Trim().ToLowerInvariant() switch
        {
            "decade" => "decade",
            "franchise" => "franchise",
            "letter" => "letter",
            _ => "genre",
        };

        public static bool IsWide(string by) => by is "decade" or "letter";

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

        /// <summary>Franchise tags on each subject's newest insight.</summary>
        private static IQueryable<TitleTag> NewestFranchiseTags(MovieDb db) =>
            db.TitleTags.Where(t => t.Category == TagCategory.Franchise && t.Value != ""
                && t.Insight.GeneratedUtc == db.TitleInsights
                    .Where(x => x.SubjectKind == t.Insight.SubjectKind && x.SubjectId == t.Insight.SubjectId)
                    .Max(x => x.GeneratedUtc));

        /// <summary>One pass over the scope: every (title, group) light row, then the heads from them.</summary>
        public static async Task<GroupIndex> BuildIndexAsync(MovieDb db, IQueryable<Movie> mq, IQueryable<Series> sq, IReadOnlyList<MiscLight> misc, string by, CancellationToken ct = default)
        {
            var rows = new List<LightRow>();
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
                    var movieIds = mq.Select(m => m.id);
                    var seriesIds = sq.Select(s => s.Id);
                    var tags = NewestFranchiseTags(db);
                    var movieTags = await tags.Where(t => t.Insight.SubjectKind == InsightSubjectKind.Movie && movieIds.Contains(t.Insight.SubjectId))
                        .Select(t => new { t.Value, t.Insight.SubjectId }).Distinct().ToListAsync(ct);
                    var seriesTags = await tags.Where(t => t.Insight.SubjectKind == InsightSubjectKind.Series && seriesIds.Contains(t.Insight.SubjectId))
                        .Select(t => new { t.Value, t.Insight.SubjectId }).Distinct().ToListAsync(ct);
                    var mIds = movieTags.Select(t => t.SubjectId).Distinct().ToList();
                    var sIds = seriesTags.Select(t => t.SubjectId).Distinct().ToList();
                    var mById = (mIds.Count > 0 ? await MovieRows(mq.Where(m => mIds.Contains(m.id))).ToListAsync(ct) : new List<LightRow>()).ToDictionary(r => r.Id);
                    var sById = (sIds.Count > 0 ? await SeriesRows(sq.Where(s => sIds.Contains(s.Id))).ToListAsync(ct) : new List<LightRow>()).ToDictionary(r => r.Id);
                    foreach (var t in movieTags) if (mById.TryGetValue(t.SubjectId, out var r)) rows.Add(WithKey(r, t.Value));
                    foreach (var t in seriesTags) if (sById.TryGetValue(t.SubjectId, out var r)) rows.Add(WithKey(r, t.Value));
                    // A franchise of one is not a shelf — the same floor GetFranchiseRail applies.
                    var keep = rows.GroupBy(r => r.GroupKey, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    rows = rows.Where(r => keep.Contains(r.GroupKey)).ToList();
                    break;
                }
                case "letter":
                {
                    var all = await MovieRows(mq).ToListAsync(ct);
                    all.AddRange(await SeriesRows(sq).ToListAsync(ct));
                    all.AddRange(misc.Select(v => MiscRow(v, "")));
                    foreach (var r in all) { r.GroupKey = LetterBuckets.LetterOf(r.SimpleTitle); rows.Add(r); }
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

            var byKey = new Dictionary<string, List<LightRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (!byKey.TryGetValue(r.GroupKey, out var list)) byKey[r.GroupKey] = list = new List<LightRow>();
                list.Add(r);
            }
            IEnumerable<Head> heads = by switch
            {
                "decade" => byKey.OrderByDescending(kv => int.TryParse(kv.Key, out var d) ? d : int.MinValue).Select(kv => new Head(kv.Key, DecadeLabel(kv.Key), kv.Value.Count)),
                "franchise" => byKey.Select(kv => new Head(kv.Key, FranchiseLabel(kv.Key), kv.Value.Count)).OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase),
                "letter" => byKey.OrderBy(kv => kv.Key == "#" ? 0 : 1).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new Head(kv.Key, kv.Key, kv.Value.Count)),
                _ => byKey.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => new Head(kv.Key, kv.Key, kv.Value.Count)),
            };
            return new GroupIndex { By = by, Rows = rows, Heads = heads.ToList(), ByKey = byKey };
        }

        /// <summary>The heads alone (an index build; the controller caches the whole index instead).</summary>
        public static async Task<List<Head>> HeadsAsync(MovieDb db, IQueryable<Movie> mq, IQueryable<Series> sq, IReadOnlyList<MiscLight> misc, string by, CancellationToken ct = default)
            => (await BuildIndexAsync(db, mq, sq, misc, by, ct)).Heads.ToList();

        /// <summary>Letter → first GROUP index over the heads' order (the grouped views' letter rail).</summary>
        public static List<(string Letter, int FirstIndex)> GroupLetters(IReadOnlyList<Head> heads, string by)
        {
            if (by == "decade") return new List<(string, int)>();
            var seen = new HashSet<string>();
            var result = new List<(string, int)>();
            for (int i = 0; i < heads.Count; i++)
            {
                var letter = by == "letter" ? heads[i].Key : LetterBuckets.LetterOf(heads[i].Label);
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
            string by, IReadOnlyList<string> keys, string sort, int seed, int perGroupTop, int perGroupSkip, CancellationToken ct = default)
            => Band(await BuildIndexAsync(db, mq, sq, misc, by, ct), keys, sort, seed, perGroupTop, perGroupSkip);

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
