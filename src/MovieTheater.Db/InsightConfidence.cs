namespace MovieTheater.Db
{
    /// <summary>
    /// How much to trust a <see cref="TitleInsight"/>. Paired with <see cref="TitleInsight.Recognized"/>:
    /// an unrecognized title is inherently <see cref="Low"/>. Lets a future, more-knowledgeable model
    /// target the weak rows for re-generation rather than redoing the whole library.
    /// </summary>
    public enum InsightConfidence
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }
}
