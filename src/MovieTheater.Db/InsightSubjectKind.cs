namespace MovieTheater.Db
{
    /// <summary>
    /// What a <see cref="TitleInsight"/> describes. Mirrors the existing shared-id-space pattern
    /// (a numeric id is a Movie OR a Series, disambiguated by a kind discriminator). Episode and
    /// Person are designed-for but not generated yet — see docs / the AI-insight plan.
    /// </summary>
    public enum InsightSubjectKind
    {
        Movie = 0,
        Series = 1,

        /// <summary>Reserved — episode-level insights are a later phase.</summary>
        Episode = 2,

        /// <summary>Reserved — person-level insights are a later phase.</summary>
        Person = 3,
    }
}
