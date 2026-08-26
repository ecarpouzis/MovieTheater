using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Web;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The grouped arcade browse (system / genre / decade) behind the catalog package's grouped views —
    /// the same two-phase heads/bands protocol as Movies (<c>Web.BrowseGroups</c>), on the lobby's
    /// CARD aggregates: one row per (System, CollapseKey) with the title, sort key, weighted rating,
    /// year, player count and the anchor's genre CSV. The controller materializes those rows for the
    /// caller's filter set once (the lobby's <c>groupedQ</c>, cached), and everything here is memory:
    /// heads, the letter rail, and each band's window, ordered by the lobby's own sorts with a unique
    /// tiebreak (<c>Sort, Title, System, CollapseKey</c>) — the lobby's order stopped at (Sort, Title),
    /// which is not unique across systems.
    /// </summary>
    public static class ArcadeGameGroups
    {
        public const int DefaultGroupsTop = 20;
        public const int MaxGroupsTop = 50;
        public const int DefaultPerGroupTop = 24;
        /// <summary>Each card hydrates its versions, cheats and profiles — a band of 20 × 30 is already 600 of them.</summary>
        public const int MaxPerGroupTop = 60;
        /// <summary>Genre groups beyond this many (by size) are dropped: the IGDB tail is noise.</summary>
        public const int MaxGenreGroups = 40;

        public sealed record CardLight(string System, string CollapseKey, string Title, string Sort, double? Rating, int? Year, int Players, string? Genres);
        public sealed record Head(string Key, string Label, int Count);
        public sealed record Member(string System, string CollapseKey, string Title);

        public sealed class GroupIndex
        {
            public string By { get; init; } = "system";
            public IReadOnlyList<Head> Heads { get; init; } = Array.Empty<Head>();
            public IReadOnlyDictionary<string, List<CardLight>> ByKey { get; init; } = new Dictionary<string, List<CardLight>>();
            public int Rows { get; init; }
            public long ApproxBytes => 256 + Rows * 160L + Heads.Count * 96L;
        }

        public static string NormalizeGroupBy(string? by) => (by ?? "").Trim().ToLowerInvariant() switch
        {
            "genre" => "genre",
            "decade" => "decade",
            _ => "system",
        };

        public static int CapGroupsTop(int requested) => requested <= 0 ? DefaultGroupsTop : Math.Min(requested, MaxGroupsTop);
        public static int CapPerGroupTop(int requested) => requested <= 0 ? DefaultPerGroupTop : Math.Min(requested, MaxPerGroupTop);

        /// <summary>The genre CSV split the way the lobby's genre filter reads it: comma/semicolon separated, trimmed, deduped.</summary>
        public static IEnumerable<string> SplitGenres(string? csv) =>
            (csv ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

        public static GroupIndex BuildIndex(IEnumerable<CardLight> cards, string by, Func<string, string>? systemLabel = null)
        {
            var byKey = new Dictionary<string, List<CardLight>>(StringComparer.OrdinalIgnoreCase);
            void Add(string key, CardLight c)
            {
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<CardLight>();
                list.Add(c);
            }
            var rows = 0;
            foreach (var c in cards)
            {
                rows += 1;
                switch (by)
                {
                    case "genre": foreach (var g in SplitGenres(c.Genres)) Add(g, c); break;
                    case "decade": if (c.Year is int y && y > 0) Add((y / 10 * 10).ToString(), c); break;
                    default: Add(c.System, c); break;
                }
            }
            IEnumerable<Head> heads = by switch
            {
                "genre" => byKey.OrderByDescending(kv => kv.Value.Count).Take(MaxGenreGroups)
                    .Select(kv => new Head(kv.Key, kv.Key, kv.Value.Count)).OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase),
                "decade" => byKey.OrderByDescending(kv => int.Parse(kv.Key)).Select(kv => new Head(kv.Key, kv.Key + "s", kv.Value.Count)),
                _ => byKey.Select(kv => new Head(kv.Key, systemLabel?.Invoke(kv.Key) ?? kv.Key, kv.Value.Count)).OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase),
            };
            var headList = heads.ToList();
            if (by == "genre")
            {
                // Keep only the surviving genres' members so a dropped tail genre cannot be banded.
                var keep = headList.Select(h => h.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var k in byKey.Keys.Where(k => !keep.Contains(k)).ToList()) byKey.Remove(k);
            }
            return new GroupIndex { By = by, Heads = headList, ByKey = byKey, Rows = rows };
        }

        /// <summary>Letter → first group index over the heads (none for decades).</summary>
        public static List<(string Letter, int FirstIndex)> GroupLetters(IReadOnlyList<Head> heads, string by)
        {
            if (by == "decade") return new List<(string, int)>();
            var seen = new HashSet<string>();
            var result = new List<(string, int)>();
            for (int i = 0; i < heads.Count; i++)
            {
                var l = LetterBuckets.LetterOf(heads[i].Label);
                if (seen.Add(l)) result.Add((l, i));
            }
            return result;
        }

        /// <summary>The lobby's sorts (rating | year | system | players | default alphabetical) with a unique tiebreak.</summary>
        public static IEnumerable<CardLight> Order(IEnumerable<CardLight> rows, string? sort)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;
            IOrderedEnumerable<CardLight> ordered = (sort ?? "").Trim().ToLowerInvariant() switch
            {
                "rating" => rows.OrderByDescending(x => x.Rating ?? -1).ThenBy(x => x.Sort, cmp),
                "year" => rows.OrderByDescending(x => x.Year ?? 0).ThenBy(x => x.Sort, cmp),
                "system" => rows.OrderBy(x => x.System, cmp).ThenBy(x => x.Sort, cmp),
                "players" => rows.OrderByDescending(x => x.Players).ThenBy(x => x.Sort, cmp),
                _ => rows.OrderBy(x => x.Sort, cmp),
            };
            return ordered.ThenBy(x => x.Title, cmp).ThenBy(x => x.System, StringComparer.Ordinal).ThenBy(x => x.CollapseKey, StringComparer.Ordinal);
        }

        public static List<Member> Band(GroupIndex index, string key, string? sort, int perGroupTop, int perGroupSkip)
        {
            var members = index.ByKey.TryGetValue(key, out var list) ? list : (IEnumerable<CardLight>)Array.Empty<CardLight>();
            return Order(members, sort).Skip(perGroupSkip).Take(perGroupTop).Select(c => new Member(c.System, c.CollapseKey, c.Title)).ToList();
        }
    }
}
