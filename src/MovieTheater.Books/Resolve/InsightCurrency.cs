using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// GATE-1: Insight rows are append-only; the CURRENT row per subject is SELECTED, never overwritten —
    /// highest model rank, then confidence, then newest, then highest id (the standalone MODEL_RANK upsert
    /// rule, expressed as a choice instead of a write). One SQL pass; safe to re-run.
    /// </summary>
    public static class InsightCurrency
    {
        public const string Sql = @"
UPDATE Insight SET IsCurrent = CASE WHEN Id IN (
    SELECT Id FROM (
        SELECT Id, row_number() OVER (PARTITION BY SubjectKind, SubjectId ORDER BY Rank DESC, Confidence DESC, GeneratedAt DESC, Id DESC) AS rn
        FROM Insight
    ) WHERE rn = 1
) THEN 1 ELSE 0 END;";

        public static (long current, long total) Rebuild(TargetWriter hot)
        {
            hot.Exec(Sql);
            return (hot.Scalar<long>("SELECT count(*) FROM Insight WHERE IsCurrent = 1"), hot.Scalar<long>("SELECT count(*) FROM Insight"));
        }
    }
}
