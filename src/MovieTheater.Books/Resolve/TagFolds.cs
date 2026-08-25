using System.Text.Json;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// The four tag folds of the standalone site's DataNormalizationService, ported verbatim: each maps a
    /// provider's vocabulary onto ONE canonical Tags-facet vocabulary and drops everything else, so chips from
    /// different legs merge instead of duplicating. In v2 the folded values are rows — SeriesTag/ItemTag with
    /// Category "tag" and the provider as Source — rather than five CSV rollups. Sources are never mutated.
    /// </summary>
    public static class TagFolds
    {
        public const string FoldedCategory = "tag";

        // ── AI (insight) fold ────────────────────────────────────────────────────────────────

        public static readonly string[] FoldCategories = { "genre", "theme", "tone", "audience", "setting", "character-focus", "award" };

        public static readonly HashSet<string> FoldDropTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "various", "superhero fans", "horror fans", "batman", "spider-man", "tank-girl",
            "zot", "suicide-squad", "gotham-city", "gotham city", "dc universe", "earth",
            "contemporary america",
        };

        public static readonly Dictionary<string, string> FoldCanonical = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sci-fi"] = "Science Fiction", ["science-fiction"] = "Science Fiction", ["space-opera"] = "Space Opera",
            ["slice-of-life"] = "Slice of Life", ["coming-of-age"] = "Coming of Age", ["martial-arts"] = "Martial Arts",
            ["non-fiction"] = "Nonfiction", ["memoir"] = "Autobiography", ["historical"] = "Historical Fiction",
            ["dark fantasy"] = "Dark Fantasy", ["dark-fantasy"] = "Dark Fantasy", ["dark-comedy"] = "Dark Comedy",
            ["mature-readers"] = "Mature", ["all-ages"] = "All Ages",
            ["action-heavy"] = "Action", ["adventurous"] = "Adventure", ["dramatic"] = "Drama",
            ["post-apocalyptic"] = "Post-Apocalyptic", ["dystopia"] = "Dystopian",
        };

        public static readonly HashSet<string> CharacterFocusVocab = new(StringComparer.OrdinalIgnoreCase)
        {
            "ensemble", "anthology", "solo-hero", "team", "anti-hero", "protagonist", "non-fiction",
            "villain", "duo", "animal", "female-lead", "villain-protagonist", "romance-leads",
        };

        /// <summary>Keep a canonical tag only if MORE than this many High-confidence series carry it.</summary>
        public const int FoldMinSeries = 5;

        /// <summary>One insight tag → its canonical display form, or null when the fold drops it.</summary>
        public static string? CanonicalizeInsightTag(string category, string tag, IReadOnlyDictionary<(string, string), string> aliases)
        {
            var t = tag.Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            if (aliases.TryGetValue((category.ToLowerInvariant(), t), out var aliased)) t = aliased;
            if (category.Equals("character-focus", StringComparison.OrdinalIgnoreCase) && !CharacterFocusVocab.Contains(t)) return null;
            if (FoldDropTags.Contains(t)) return null;
            if (FoldCanonical.TryGetValue(t, out var display)) return display;
            return TitleCase(t.Replace('-', ' ').Replace('_', ' '));
        }

        public static string TitleCase(string s) =>
            string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length <= 1 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));

        // ── External (Open Library / Google Books subjects) fold: substring whitelist ─────────

        public static readonly (string Keyword, string Canonical)[] SubjectMap =
        {
            ("science fiction", "Science Fiction"), ("sci-fi", "Science Fiction"),
            ("fantasy", "Fantasy"), ("horror", "Horror"), ("romance", "Romance"),
            ("detective", "Mystery"), ("mystery", "Mystery"),
            ("thriller", "Thriller"), ("suspense", "Thriller"),
            ("crime", "Crime"),
            ("espionage", "Spy"), ("spy", "Spy"), ("spies", "Spy"),
            ("adventure", "Adventure"), ("western", "Western"),
            ("world war", "War"), (" war ", "War"), ("wartime", "War"),
            ("humor", "Humor"), ("humour", "Humor"), ("comedy", "Humor"),
            ("autobiograph", "Autobiography"), ("memoir", "Autobiography"),
            ("biography", "Biography"), ("historical fiction", "Historical Fiction"),
            ("superhero", "Superhero"), ("satire", "Satire"), ("noir", "Noir"),
            ("supernatural", "Supernatural"), ("occult", "Supernatural"), ("ghost", "Supernatural"), ("vampire", "Supernatural"),
            ("martial arts", "Martial Arts"), ("slice of life", "Slice of Life"),
            ("coming of age", "Coming of Age"), ("coming-of-age", "Coming of Age"),
            ("dystopia", "Dystopian"),
            ("post-apocalyptic", "Post-Apocalyptic"), ("apocalyptic", "Post-Apocalyptic"),
            ("young adult", "Teen"),
        };

        public static SortedSet<string> FoldSubjects(string? subjectsJson)
        {
            var canon = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var subjects = ParseStringArray(subjectsJson);
            if (subjects.Count == 0) return canon;
            var blob = string.Join(" | ", subjects).ToLowerInvariant();
            foreach (var (keyword, canonical) in SubjectMap)
                if (blob.Contains(keyword, StringComparison.Ordinal)) canon.Add(canonical);
            return canon;
        }

        // ── MangaUpdates fold: exact-match closed maps ────────────────────────────────────────

        public static readonly Dictionary<string, string> MuGenreMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Action"] = "Action", ["Adventure"] = "Adventure", ["Comedy"] = "Humor", ["Drama"] = "Drama", ["Fantasy"] = "Fantasy", ["Horror"] = "Horror",
            ["Mystery"] = "Mystery", ["Psychological"] = "Psychological", ["Romance"] = "Romance", ["Sci-fi"] = "Science Fiction", ["Slice of Life"] = "Slice of Life",
            ["Sports"] = "Sports", ["Supernatural"] = "Supernatural", ["Thriller"] = "Thriller", ["Tragedy"] = "Tragedy", ["Historical"] = "Historical Fiction", ["Mecha"] = "Mecha",
            ["Martial Arts"] = "Martial Arts", ["School Life"] = "School Life", ["Seinen"] = "Seinen", ["Shounen"] = "Shonen", ["Shoujo"] = "Shojo", ["Josei"] = "Josei",
            ["Mature"] = "Mature", ["Gender Bender"] = "Gender Bender", ["Harem"] = "Harem", ["Yaoi"] = "LGBTQ+", ["Yuri"] = "LGBTQ+", ["Shounen Ai"] = "LGBTQ+", ["Shoujo Ai"] = "LGBTQ+",
        };

        public static readonly Dictionary<string, string> MuCategoryMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Award-Winning Work"] = "Award Winner", ["Adapted to Anime"] = "Anime Adaptation", ["Adapted to Live Action"] = "Live-Action Adaptation",
            ["Adapted to Movie"] = "Film Adaptation", ["Classic Manga"] = "Classic", ["Post-Apocalyptic"] = "Post-Apocalyptic", ["Time Travel"] = "Time Travel",
        };

        public static SortedSet<string> FoldMu(string? genresJson, string? categoriesJson)
        {
            var canon = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (json, map) in new[] { (genresJson, MuGenreMap), (categoriesJson, MuCategoryMap) })
                foreach (var v in ParseStringArray(json))
                    if (map.TryGetValue(v.Trim(), out var c)) canon.Add(c);
            return canon;
        }

        // ── GCD story-genre fold: ';'-separated closed vocabulary ────────────────────────────

        public static readonly Dictionary<string, string> GcdGenreMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["superhero"] = "Superhero", ["humor"] = "Humor", ["science fiction"] = "Science Fiction", ["horror-suspense"] = "Horror", ["adventure"] = "Adventure",
            ["fantasy"] = "Fantasy", ["war"] = "War", ["detective-mystery"] = "Mystery", ["teen"] = "Teen", ["sword and sorcery"] = "Sword & Sorcery", ["children"] = "Children's",
            ["anthropomorphic-funny animals"] = "Funny Animals", ["crime"] = "Crime", ["western-frontier"] = "Western", ["romance"] = "Romance", ["sports"] = "Sports",
            ["biography"] = "Biography", ["history"] = "History", ["drama"] = "Drama", ["satire-parody"] = "Satire", ["spy"] = "Spy", ["non-fiction"] = "Nonfiction",
            ["military"] = "War", ["medical"] = "Medical", ["nature"] = "Nature", ["aviation"] = "Aviation", ["car"] = "Cars", ["domestic"] = "Slice of Life",
            ["erotica"] = "Mature", ["jungle"] = "Jungle",
        };

        public static SortedSet<string> FoldGcd(string? storyGenres)
        {
            var canon = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(storyGenres)) return canon;
            foreach (var tok in storyGenres.Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (GcdGenreMap.TryGetValue(tok.Trim(), out var c)) canon.Add(c);
            return canon;
        }

        public static List<string> ParseStringArray(string? json)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is string s && s.Length > 0) list.Add(s);
            }
            catch (JsonException) { }
            return list;
        }

        // ── The AI fold job over the hot file ────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds SeriesTag/ItemTag(Source=AI) from the CURRENT insight rows: every current row's tags are kept
        /// raw under their own category (the kids gate reads <c>audience</c>), and High-confidence SERIES rows also
        /// get the folded canonical set under "tag" — kept only above the >5-series threshold, exactly as the
        /// standalone fold did for ClaudeTagsCsv.
        /// </summary>
        public static (int raw, int folded, int kept) RebuildAiFold(TargetWriter hot)
        {
            hot.Exec("DELETE FROM SeriesTag WHERE Source = $s", ("$s", (int)TagSource.AI));
            hot.Exec("DELETE FROM ItemTag WHERE Source = $s", ("$s", (int)TagSource.AI));
            var aliases = new Dictionary<(string, string), string>();
            foreach (var (_, row) in hot.Pairs("SELECT rowid, Category || char(31) || AliasTag || char(31) || CanonicalTag FROM TagAlias"))
            {
                var p = row!.Split(TargetWriter.Sep);
                aliases.TryAdd((p[0].ToLowerInvariant(), p[1].ToLowerInvariant()), p[2].ToLowerInvariant());
            }
            var raw = 0;
            // raw rows for every current insight
            var rows = hot.Pairs(
                "SELECT i.Id, i.SubjectKind || char(31) || i.SubjectId || char(31) || i.Confidence || char(31) || t.Category || char(31) || t.Value" +
                " FROM Insight i JOIN InsightTag t ON t.InsightId = i.Id WHERE i.IsCurrent = 1");
            var perSeries = new Dictionary<long, HashSet<string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, payload) in rows)
            {
                var p = payload!.Split(TargetWriter.Sep);
                var kind = (SubjectKind)int.Parse(p[0]); var subjectId = long.Parse(p[1]); var conf = (Confidence)int.Parse(p[2]);
                var category = p[3]; var value = p[4];
                var key = $"{p[0]}|{p[1]}|{category.ToLowerInvariant()}|{value.ToLowerInvariant()}";
                if (seen.Add(key))
                {
                    if (kind == SubjectKind.Series) hot.Upsert("SeriesTag", new { SeriesId = (int)subjectId, Category = category, Value = value, Source = TagSource.AI });
                    else hot.Upsert("ItemTag", new { ItemId = (int)subjectId, Category = category, Value = value, Source = TagSource.AI });
                    raw++;
                }
                if (kind != SubjectKind.Series || conf != Confidence.High || !FoldCategories.Contains(category, StringComparer.OrdinalIgnoreCase)) continue;
                var canon = CanonicalizeInsightTag(category, value, aliases);
                if (canon == null) continue;
                if (!perSeries.TryGetValue(subjectId, out var set)) perSeries[subjectId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(canon);
            }
            var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in perSeries.Values) foreach (var t in set) count[t] = count.GetValueOrDefault(t) + 1;
            var kept = count.Where(kv => kv.Value > FoldMinSeries).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var folded = 0;
            foreach (var (seriesId, set) in perSeries)
                foreach (var t in set.Where(kept.Contains))
                { hot.Upsert("SeriesTag", new { SeriesId = (int)seriesId, Category = FoldedCategory, Value = t, Source = TagSource.AI }); folded++; }
            return (raw, folded, kept.Count);
        }
    }
}
