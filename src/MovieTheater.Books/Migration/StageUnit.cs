namespace MovieTheater.Books.Migration
{
    /// <summary>Per-batch tallies. <c>Unmapped</c> counts rows (or values) the contract could not place — reported, never dropped silently.</summary>
    public sealed class UnitCounts
    {
        public int Inserted, Skipped, Unmapped;
        public readonly SortedDictionary<string, int> Detail = new(StringComparer.Ordinal);
        public void Bump(string key, int n = 1) { Detail.TryGetValue(key, out var c); Detail[key] = c + n; }
        public override string ToString() =>
            $"inserted: {Inserted}, skipped: {Skipped}, unmapped: {Unmapped}" + (Detail.Count == 0 ? "" : ", " + string.Join(", ", Detail.Select(kv => $"{kv.Key}: {kv.Value}")));
    }

    /// <summary>
    /// One unit of migration work: rows of ONE v1 table (paged by rowid) → writes into the hot and/or legs file.
    /// Units are grouped into the contract's stages; the engine runs them in stage order and keeps a
    /// MigrationProgress row per unit, so a run killed anywhere resumes at the unit and rowid it stopped on.
    /// </summary>
    public abstract class StageUnit
    {
        /// <summary>The contract stage this unit belongs to (validated against the mapping's stage list).</summary>
        public abstract string Stage { get; }

        /// <summary>The v1 table driving the unit; null for the row-less units (fts, analyze, resolve).</summary>
        public abstract string? SourceTable { get; }

        /// <summary>Optional filter on the source table (SQL fragment).</summary>
        public virtual string? SourceWhere => null;

        /// <summary>A unit name distinct from the table when a table feeds several units (folders two-pass).</summary>
        public virtual string Suffix => "";

        public string Name => SourceTable == null ? Stage : $"{Stage}/{SourceTable}{Suffix}";

        public abstract void Transform(V1Row row, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts counts);

        /// <summary>Runs once when the unit completes (reports, exports).</summary>
        public virtual void Finalize(MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts counts) { }

        /// <summary>Row-less units implement the whole job here (chunked internally); return false when more remains.</summary>
        public virtual bool RunSpecial(MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts counts, long cursor, int batchSize, out long nextCursor)
        {
            nextCursor = cursor;
            return true;
        }
    }
}
