using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// The controlled vocabulary for <see cref="TitleTag"/> values, plus the normalization the loader
    /// applies on write. Keeps model-emitted tags from fragmenting ("heist" vs "heists" vs "robbery")
    /// so the discovery facets stay queryable.
    ///
    /// <para>Policy is <b>allow + log</b>, not reject: a value outside the seed list is still written,
    /// but the loader reports it so genuinely useful new values can be promoted into the seed here
    /// (and typos/synonyms caught). The model isn't straitjacketed at v1.</para>
    /// </summary>
    public static class AiMetadataVocab
    {
        /// <summary>Synonyms collapsed to a canonical value, regardless of category. Lower-case keys.</summary>
        private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["heists"] = "heist",
            ["robbery"] = "heist",
            ["coming-of-age"] = "coming of age",
            ["sci fi"] = "sci-fi",
            ["scifi"] = "sci-fi",
            ["science fiction"] = "sci-fi",
            ["rom com"] = "romcom",
            ["rom-com"] = "romcom",
            ["feelgood"] = "feel-good",
            ["post apocalyptic"] = "post-apocalypse",
            ["post-apocalyptic"] = "post-apocalypse",
            ["stop motion"] = "stop-motion",
            ["found footage"] = "found-footage",
            ["neo noir"] = "neo-noir",
            ["b-movie"] = "b movie",
            ["bmovie"] = "b movie",
        };

        /// <summary>Seed values we already recognize, per category. Not exhaustive — grown over time.</summary>
        private static readonly Dictionary<TagCategory, HashSet<string>> Seed = new()
        {
            [TagCategory.Subgenre] = New(
                "heist", "slasher", "neo-noir", "mecha", "space opera", "spaghetti western",
                "body-horror", "psychological thriller", "creature feature", "courtroom drama",
                "buddy cop", "revenge thriller", "coming of age", "mockumentary", "romcom",
                "sword and sorcery", "kaiju", "giallo", "noir", "satire", "musical",
                "superhero", "spoof", "survival thriller", "art house", "buddy comedy",
                "crime saga", "prison drama", "rock opera", "mythological fantasy",
                "horror", "sci-fi", "documentary", "historical drama", "gothic horror", "anime",
                "sitcom", "fantasy adventure", "hard sci-fi", "urban fantasy", "sports drama",
                "political thriller", "sci-fi thriller", "fantasy comedy", "horror comedy",
                "paranormal mystery", "cyberpunk", "spy comedy", "supernatural",
                "action thriller", "comedy", "romance", "teen drama", "disaster", "vampire",
                "dark fantasy", "spy", "b movie", "stand-up comedy", "folk horror", "dark comedy"),
            [TagCategory.Mood] = New(
                "cozy", "bleak", "tense", "whimsical", "melancholic", "uplifting", "dread",
                "playful", "dreamlike", "gritty", "wholesome", "unsettling", "epic", "intimate"),
            [TagCategory.Tone] = New(
                "satirical", "earnest", "campy", "ironic", "deadpan", "operatic", "absurdist",
                "self-serious", "irreverent", "essayistic"),
            [TagCategory.Theme] = New(
                "redemption", "revenge", "coming of age", "identity", "isolation", "obsession",
                "sacrifice", "found family", "man vs nature", "loss of innocence", "rebellion",
                "curiosity", "loss", "ambition", "class", "innocence", "trauma"),
            [TagCategory.Setting] = New(
                "small-town", "space station", "post-apocalypse", "high school", "prison",
                "deep space", "suburbia", "wilderness", "dystopia", "haunted house", "the road",
                "boarding school", "ancient china", "hospital", "afterlife",
                "alien world", "desert planet", "desert", "ocean", "jungle", "los angeles",
                "paris", "liminal space", "summer camp", "one room", "the underworld",
                "new york", "san francisco", "las vegas", "london", "japan", "italy",
                "berlin", "hong kong", "cyberspace", "rural", "medieval", "taiwan",
                "college", "chicago", "germany", "india"),
            [TagCategory.Era] = New(
                "1920s", "1950s", "1960s", "1970s", "1980s", "1990s", "victorian", "medieval",
                "near-future", "far-future", "ancient", "wild west", "cold war",
                "1800s", "1860s", "1900s", "1910s", "1930s", "1940s", "2000s", "2010s",
                "renaissance", "1600s", "1700s", "2020s", "1500s", "1830s", "1840s", "1850s", "1880s", "1890s"),
            [TagCategory.VisualStyle] = New(
                "stop-motion", "rotoscope", "found-footage", "technicolor", "black and white",
                "cel animation", "claymation", "long takes", "practical effects", "neon-soaked",
                "hand-drawn", "cgi", "desaturated", "autumnal", "anime",
                "comic-book", "mixed-media", "painterly", "screenlife", "symmetrical", "spectacle"),
            [TagCategory.ContentDescriptor] = New(
                "gore", "body-horror", "feel-good", "jump-scares", "slow-burn", "tearjerker",
                "violence", "no dialogue", "non-linear", "one-take", "anthology",
                "explicit", "raunchy", "disturbing", "gross-out"),
            [TagCategory.Occasion] = New(
                "halloween", "christmas", "rainy-sunday", "background-noise", "date-night",
                "family-night", "comfort-watch", "late-night", "party", "summer", "feel-good",
                "thanksgiving"),
            // Theme-like open categories where a seed list is less useful are intentionally left
            // without one (Franchise, CompTitle, Keyword) — every value there is "novel" by nature.
        };

        private static HashSet<string> New(params string[] values) =>
            new(values, StringComparer.OrdinalIgnoreCase);

        /// <summary>Lower-case, trim, collapse internal whitespace, then apply the synonym map.</summary>
        public static string Normalize(string raw)
        {
            var v = string.Join(' ', (raw ?? "").Trim().ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return Synonyms.TryGetValue(v, out var canon) ? canon : v;
        }

        /// <summary>True when <paramref name="normalizedValue"/> is in the seed list for its category.
        /// Categories with no seed (Franchise/CompTitle/Keyword) always return true — they're open.</summary>
        public static bool IsKnown(TagCategory category, string normalizedValue) =>
            !Seed.TryGetValue(category, out var set) || set.Contains(normalizedValue);
    }
}
