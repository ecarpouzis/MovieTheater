using MovieTheater.Books.Db;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Migration.Units
{
    /// <summary>The <c>resolve</c> stage: the same resolver the runtime jobs use, run over the freshly copied rows.</summary>
    public sealed class ResolveUnit : StageUnit
    {
        public override string Stage => "resolve";
        public override string? SourceTable => null;
        public override void Transform(V1Row row, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts counts) => throw new NotSupportedException();

        public override bool RunSpecial(MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts counts, long cursor, int batchSize, out long nextCursor)
        {
            // one step (or one item chunk) per batch so a kill mid-resolve resumes where it stopped
            var done = ResolvePipeline.RunStep(hot, cursor, batchSize, ctx.Log, counts, out nextCursor);
            if (done) counts.Inserted++;
            return done;
        }
    }

    /// <summary>The <c>fts</c> stage: rebuild ItemFts from the resolved scalars, chunked by Item.Id.</summary>
    public sealed class FtsUnit : StageUnit
    {
        public override string Stage => "fts";
        public override string? SourceTable => null;
        public override void Transform(V1Row row, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts counts) => throw new NotSupportedException();

        public override bool RunSpecial(MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts counts, long cursor, int batchSize, out long nextCursor)
        {
            if (cursor == 0) hot.Exec(ItemFts.ClearSql);
            var last = FtsBuilder.IndexBatch(hot, cursor, batchSize, out var n);
            counts.Inserted += n;
            nextCursor = last;
            var done = n < batchSize;
            if (done) hot.Exec(ItemFts.OptimizeSql);
            return done;
        }
    }

    /// <summary>The <c>analyze</c> stage: planner statistics for both files.</summary>
    public sealed class AnalyzeUnit : StageUnit
    {
        public override string Stage => "analyze";
        public override string? SourceTable => null;
        public override void Transform(V1Row row, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts counts) => throw new NotSupportedException();

        public override bool RunSpecial(MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts counts, long cursor, int batchSize, out long nextCursor)
        {
            hot.Exec("ANALYZE;");
            legs.Exec("ANALYZE;");
            counts.Inserted = 2;
            nextCursor = 1;
            return true;
        }
    }
}
