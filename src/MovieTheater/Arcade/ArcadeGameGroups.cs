using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Web;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// The grouped arcade browse behind the catalog package's grouped views — the same two-phase
    /// heads/bands protocol as Movies (<c>Web.BrowseGroups</c>), on the lobby's CARD aggregates: one
    /// row per (System, CollapseKey) with the title, sort key, weighted rating, year, player count,
    /// developer/publisher, the RA flags and the anchor's genre CSV. The controller materializes those
    /// rows for the caller's filter set once (the lobby's <c>groupedQ</c>, cached), and everything here
    /// is memory: heads, the letter rail, and each band's window, ordered by the lobby's own sorts with
    /// a unique tiebreak (<c>Sort, Title, System, CollapseKey</c>) — the lobby's order stopped at
    /// (Sort, Title), which is not unique across systems.
    ///
    /// <para>Axes (R9 S8): <c>system</c> · <c>genre</c> · <c>decade</c> · <c>players</c> (the card's
    /// <c>MaxPlayers</c>, 5+ folded) · <c>region</c> and <c>variant</c> (per VERSION — a card stands
    /// under every region and every variant it has a surviving dump for, which is exactly how the
    /// lobby's region deselect reads a card) · <c>developer</c> · <c>publisher</c> · <c>ra</c>
    /// (achievements / high scores / speedruns, and the cards with none).</para>
    ///
    /// <para>Region and variant are the only MULTI-VALUED axes, so they need one extra light query —
    /// the distinct (System, CollapseKey, Region, Variant) tuples, which is a couple of rows per card
    /// rather than every ROM row. The controller passes them in; this stays pure.</para>
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

        public sealed record CardLight(string System, string CollapseKey, string Title, string Sort, double? Rating, int? Year, int Players, string? Genres,
            string? Developer = null, string? Publisher = null, int RaAchievements = 0, bool RaScore = false, bool RaTime = false);
        /// <summary>One (card, region, variant) tuple — the per-VERSION facts a card can hold several of.</summary>
        public sealed record CardTag(string System, string CollapseKey, string? Region, string? Variant);
        public sealed record Head(string Key, string Label, int Count);
        public sealed record Member(string System, string CollapseKey, string Title);

        /// <summary>The RA shelves, in the order the rail lists them; the first three keys ARE the `ra=` facet's values.</summary>
        public static readonly IReadOnlyList<(string Key, string Label)> RaShelves = new[]
        {
            ("achievements", "Achievements"),
            ("highscores", "High-score leaderboards"),
            ("speedruns", "Speedrun leaderboards"),
            ("none", "No RetroAchievements"),
        };

        /// <summary>Where an unmarked dump files: the lobby spells a null Variant "Release" everywhere else too.</summary>
        public const string DefaultVariant = "Release";
        /// <summary>Where an unmarked dump's REGION files. Never a deselect option, so never hidden.</summary>
        public const string UnknownRegion = "Unknown";

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
            "players" => "players",
            "region" => "region",
            "variant" => "variant",
            "developer" => "developer",
            "publisher" => "publisher",
            "ra" => "ra",
            _ => "system",
        };

        /// <summary>True when the axis needs the per-VERSION tuples rather than the card aggregates alone.</summary>
        public static bool NeedsTags(string by) => by is "region" or "variant";

        /// <summary>
        /// Axes whose heads are in LABEL order, so an A–Z rail over them means something. Decades run
        /// newest-first, players is a numeric ladder and the RA shelves are a fixed order.
        /// </summary>
        public static bool IsAlphabetical(string by) => by is not ("decade" or "players" or "ra");

        /// <summary>How many can play at once: exact up to four, then one 5+ shelf.</summary>
        public static (string Key, string Label) PlayersBucket(int players)
        {
            var n = players <= 1 ? 1 : players;
            if (n >= 5) return ("5", "5+ players");
            return (n.ToString(), n == 1 ? "1 player" : $"{n} players");
        }

        public static int CapGroupsTop(int requested) => requested <= 0 ? DefaultGroupsTop : Math.Min(requested, MaxGroupsTop);
        public static int CapPerGroupTop(int requested) => requested <= 0 ? DefaultPerGroupTop : Math.Min(requested, MaxPerGroupTop);

        /// <summary>The genre CSV split the way the lobby's genre filter reads it: comma/semicolon separated, trimmed, deduped.</summary>
        public static IEnumerable<string> SplitGenres(string? csv) =>
            (csv ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

        public static GroupIndex BuildIndex(IEnumerable<CardLight> cards, string by, Func<string, string>? systemLabel = null, IEnumerable<CardTag>? tags = null)
        {
            var byKey = new Dictionary<string, List<CardLight>>(StringComparer.OrdinalIgnoreCase);
            void Add(string key, CardLight c)
            {
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<CardLight>();
                list.Add(c);
            }
            // The multi-valued axes read the per-VERSION tuples; a card lands under every value it has a
            // surviving dump for, deduped so two dumps of one region are still one shelf entry.
            var tagKeys = NeedsTags(by)
                ? (tags ?? Array.Empty<CardTag>())
                    .GroupBy(t => (t.System, t.CollapseKey))
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(t => by == "region" ? (string.IsNullOrWhiteSpace(t.Region) ? UnknownRegion : t.Region!) : (string.IsNullOrWhiteSpace(t.Variant) ? DefaultVariant : t.Variant!))
                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                : null;
            var rows = 0;
            foreach (var c in cards)
            {
                rows += 1;
                switch (by)
                {
                    case "genre": foreach (var g in SplitGenres(c.Genres)) Add(g, c); break;
                    case "decade": if (c.Year is int y && y > 0) Add((y / 10 * 10).ToString(), c); break;
                    case "players": Add(PlayersBucket(c.Players).Key, c); break;
                    case "developer": if (!string.IsNullOrWhiteSpace(c.Developer)) Add(c.Developer!.Trim(), c); break;
                    case "publisher": if (!string.IsNullOrWhiteSpace(c.Publisher)) Add(c.Publisher!.Trim(), c); break;
                    case "ra":
                    {
                        var any = false;
                        if (c.RaAchievements > 0) { Add("achievements", c); any = true; }
                        if (c.RaScore) { Add("highscores", c); any = true; }
                        if (c.RaTime) { Add("speedruns", c); any = true; }
                        if (!any) Add("none", c);
                        break;
                    }
                    case "region":
                    case "variant":
                    {
                        if (tagKeys != null && tagKeys.TryGetValue((c.System, c.CollapseKey), out var keys))
                            foreach (var k in keys) Add(k, c);
                        else if (by == "variant") Add(DefaultVariant, c);
                        break;
                    }
                    default: Add(c.System, c); break;
                }
            }
            IEnumerable<Head> heads = by switch
            {
                "genre" => byKey.OrderByDescending(kv => kv.Value.Count).Take(MaxGenreGroups)
                    .Select(kv => new Head(kv.Key, kv.Key, kv.Value.Count)).OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase),
                "decade" => byKey.OrderByDescending(kv => int.Parse(kv.Key)).Select(kv => new Head(kv.Key, kv.Key + "s", kv.Value.Count)),
                "players" => byKey.OrderBy(kv => int.Parse(kv.Key)).Select(kv => new Head(kv.Key, PlayersBucket(int.Parse(kv.Key)).Label, kv.Value.Count)),
                "ra" => RaShelves.Where(s => byKey.ContainsKey(s.Key)).Select(s => new Head(s.Key, s.Label, byKey[s.Key].Count)),
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

        /// <summary>Letter → first group index over the heads. Only the alphabetical axes have one.</summary>
        public static List<(string Letter, int FirstIndex)> GroupLetters(IReadOnlyList<Head> heads, string by)
        {
            if (!IsAlphabetical(by)) return new List<(string, int)>();
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
