namespace MovieTheater.Db
{
    /// <summary>
    /// The facet a <see cref="TitleTag"/> belongs to. The category is what makes the discovery layer
    /// queryable — "all <c>Subgenre=heist</c>", "all <c>Franchise=alien</c>", "all <c>Mood=cozy</c>" —
    /// so a tag's <see cref="TitleTag.Value"/> is only meaningful alongside its category.
    /// </summary>
    public enum TagCategory
    {
        /// <summary>What the title is about — "redemption", "coming of age", "revenge".</summary>
        Theme = 0,

        /// <summary>How it feels — "cozy", "bleak", "tense", "whimsical".</summary>
        Mood = 1,

        /// <summary>Register / attitude — "satirical", "earnest", "campy".</summary>
        Tone = 2,

        /// <summary>Finer genre than the IMDb genre list — "heist", "slasher", "mecha", "neo-noir".</summary>
        Subgenre = 3,

        /// <summary>Where it takes place — "small-town", "space station", "post-apocalypse".</summary>
        Setting = 4,

        /// <summary>When it is set — "1920s", "near-future", "medieval".</summary>
        Era = 5,

        /// <summary>Look / craft — "stop-motion", "rotoscope", "found-footage", "technicolor".</summary>
        VisualStyle = 6,

        /// <summary>Franchise / shared universe / loose grouping — "alien", "studio-ghibli", "mcu".</summary>
        Franchise = 7,

        /// <summary>Content the viewer may want to seek out or avoid — "gore", "body-horror", "feel-good".</summary>
        ContentDescriptor = 8,

        /// <summary>A "watch if you liked …" comparison title (free text, may not be in the library).</summary>
        CompTitle = 9,

        /// <summary>When/why to watch — "halloween", "rainy-sunday", "background-noise", "date-night".</summary>
        Occasion = 10,

        /// <summary>Anything else salient that doesn't fit a category above.</summary>
        Keyword = 11,

        /// <summary>Curated channel membership. Value is a ChannelCatalog key ("family-night");
        /// written by the load-channel-tags command as a direct per-title judgment, consumed by
        /// Channel filters. Deliberately NOT a similarity feature (see RecommendationRefresher) —
        /// curation labels must not reshape recommendations.</summary>
        Channel = 12,
    }
}
