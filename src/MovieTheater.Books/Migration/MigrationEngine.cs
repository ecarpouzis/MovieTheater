using System.Diagnostics;

namespace MovieTheater.Books.Migration
{
    public sealed record BatchResult(string Unit, int Processed, long Remaining, string NextCursor, bool UnitDone, UnitCounts Counts);

    public sealed record RunSummary(int Batches, int UnitsFinished, int UnitsRemaining, bool Stopped, string? StopReason);

    /// <summary>
    /// The chunked, resumable, caller-driven v1→v2 copy-transform. One batch = one page of one unit's driving
    /// table, written in one transaction per target file together with the unit's MigrationProgress row; the
    /// next batch reads the cursor back from that row, so a kill anywhere loses at most one page. The engine
    /// refuses to loop forever: a batch that moves no cursor breaks the run.
    /// </summary>
    public sealed class MigrationEngine
    {
        private readonly MigrationContext ctx;
        private readonly IReadOnlyList<StageUnit> units;

        public MigrationEngine(MigrationContext ctx, IReadOnlyList<StageUnit>? units = null)
        {
            this.ctx = ctx;
            this.units = units ?? MigrationUnits.All();
            Validate(ctx.Mapping, this.units);
        }

        public IReadOnlyList<StageUnit> Units => units;

        /// <summary>Every v1 table with targets is driven by exactly one unit in the contract's stage; every unit's stage exists.</summary>
        public static void Validate(MappingContract mapping, IReadOnlyList<StageUnit> units)
        {
            var stages = mapping.Stages.ToHashSet(StringComparer.Ordinal);
            foreach (var u in units)
                if (!stages.Contains(u.Stage)) throw new InvalidOperationException($"unit {u.Name}: stage '{u.Stage}' is not in the contract");
            var driven = units.Where(u => u.SourceTable != null && u.Suffix == "").ToLookup(u => u.SourceTable!);
            foreach (var t in mapping.V1.Values.Where(t => t.Targets.Count > 0 && t.Name != "ComicFts"))
            {
                var d = driven[t.Name].ToList();
                if (d.Count != 1) throw new InvalidOperationException($"v1 table {t.Name} is driven by {d.Count} units (expected 1)");
                if (d[0].Stage != t.Stage) throw new InvalidOperationException($"v1 table {t.Name}: unit stage {d[0].Stage} != contract stage {t.Stage}");
            }
            var order = mapping.Stages.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
            for (var i = 1; i < units.Count; i++)
                if (order[units[i].Stage] < order[units[i - 1].Stage])
                    throw new InvalidOperationException($"units out of stage order at {units[i].Name}");
        }

        public IEnumerable<StageUnit> Selected()
        {
            var sel = ctx.Options.Stage;
            if (string.IsNullOrEmpty(sel)) return units;
            return units.Where(u => u.Stage == sel || u.Name == sel);
        }

        /// <summary>Drive the selected units to completion (or MaxBatches), printing a progress line per batch.</summary>
        public RunSummary Run(CancellationToken ct = default)
        {
            var batches = 0; var finished = 0;
            var overrideApplied = false;
            var selected = Selected().ToList();
            if (selected.Count == 0) throw new InvalidOperationException($"no unit matches --stage {ctx.Options.Stage}");
            foreach (var unit in selected)
            {
                var progress = ReadProgress(unit.Name);
                if (progress.FinishedAt != null) { finished++; continue; }
                if (ctx.Options.DryRun && unit.SourceTable == null)
                {
                    ctx.Log($"[unit: {unit.Name}] skipped in dry run (derives from written rows)");
                    finished++;
                    continue;
                }
                // the cursor is carried in memory batch to batch (a dry run persists nothing); a fresh process reads it back from MigrationProgress
                long cursor = progress.Cursor;
                if (ctx.Options.After != null && !overrideApplied) { cursor = ctx.Options.After.Value; overrideApplied = true; }
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var before = cursor;
                    var result = RunBatch(unit, cursor);
                    batches++;
                    ctx.Log($"{{ processed: {result.Processed}, remaining: {result.Remaining}, nextCursor: \"{result.NextCursor}\" }}  [unit: {result.Unit}, {result.Counts}]");
                    if (result.UnitDone) { finished++; break; }
                    cursor = long.Parse(result.NextCursor);
                    if (cursor == before && result.Processed == 0)
                        return new RunSummary(batches, finished, selected.Count - finished, true, $"no progress in unit {unit.Name} at cursor {before}");
                    if (ctx.Options.MaxBatches > 0 && batches >= ctx.Options.MaxBatches)
                        return new RunSummary(batches, finished, selected.Count - finished, true, "max batches reached");
                }
            }
            return new RunSummary(batches, finished, selected.Count - finished, false, null);
        }

        public BatchResult RunBatch(StageUnit unit, long cursor)
        {
            var sw = Stopwatch.StartNew();
            using var hot = new TargetWriter(ctx.Options.TargetPath, ctx.Mapping, ctx.Options.DryRun);
            using var legs = new TargetWriter(ctx.Options.LegsPath, ctx.Mapping, ctx.Options.DryRun);
            var counts = new UnitCounts();
            var batch = ctx.Options.BatchSize;

            if (unit.SourceTable == null)
            {
                hot.Begin(); legs.Begin();
                var done = unit.RunSpecial(ctx, hot, legs, counts, cursor, batch, out var next);
                if (done) unit.Finalize(ctx, hot, legs, counts);
                WriteProgress(hot, unit.Name, next, counts.Inserted, 1, done);
                legs.Commit(); hot.Commit();
                return new BatchResult(unit.Name, counts.Inserted, done ? 0 : 1, next.ToString(), done, counts);
            }

            var rows = ctx.Source.Page(unit.SourceTable, cursor, batch, unit.SourceWhere);
            hot.Begin(); legs.Begin();
            long last = cursor;
            foreach (var row in rows)
            {
                unit.Transform(row, ctx, hot, legs, counts);
                last = row.Rowid;
            }
            var unitDone = rows.Count < batch;
            if (unitDone) unit.Finalize(ctx, hot, legs, counts);
            WriteProgress(hot, unit.Name, last, rows.Count, ctx.Source.Count(unit.SourceTable, unit.SourceWhere), unitDone);
            // legs first: if the hot commit (which carries the cursor) then fails, the legs rows are simply re-upserted
            legs.Commit();
            hot.Commit();
            var remaining = ctx.Source.Remaining(unit.SourceTable, last, unit.SourceWhere);
            counts.Bump("ms", (int)sw.ElapsedMilliseconds);
            return new BatchResult(unit.Name, rows.Count, remaining, last.ToString(), unitDone, counts);
        }

        public sealed record Progress(string Stage, long Cursor, long Processed, DateTime? FinishedAt);

        public Progress ReadProgress(string unitName)
        {
            using var hot = new TargetWriter(ctx.Options.TargetPath, ctx.Mapping, dryRun: true);
            var rows = hot.Pairs($"SELECT Cursor, FinishedAt FROM MigrationProgress WHERE Stage = '{unitName.Replace("'", "''")}'");
            if (rows.Count == 0) return new Progress(unitName, 0, 0, null);
            var processed = hot.Scalar<long>($"SELECT coalesce(Processed,0) FROM MigrationProgress WHERE Stage = '{unitName.Replace("'", "''")}'");
            return new Progress(unitName, rows[0].Item1, processed, Transforms.ParseDate(rows[0].Item2));
        }

        private void WriteProgress(TargetWriter hot, string unitName, long cursor, int processedThisBatch, long total, bool done)
        {
            var prev = hot.Scalar<long>("SELECT coalesce(Processed,0) FROM MigrationProgress WHERE Stage=$s", ("$s", unitName));
            hot.Upsert("MigrationProgress", new { Stage = unitName, Cursor = cursor.ToString(), Processed = (int)(prev + processedThisBatch), Total = (int)total, FinishedAt = done ? DateTime.UtcNow : (DateTime?)null });
        }

        /// <summary>Progress of every unit, for status displays and the verifier.</summary>
        public List<Progress> AllProgress() => units.Select(u => ReadProgress(u.Name)).ToList();

        /// <summary>Forget the progress of a unit (or all) so it re-runs; rows are upserts, so this is safe.</summary>
        public void ResetProgress(string? unitOrStage)
        {
            using var hot = new TargetWriter(ctx.Options.TargetPath, ctx.Mapping, ctx.Options.DryRun);
            hot.Begin();
            if (unitOrStage == null) hot.Exec("DELETE FROM MigrationProgress");
            else hot.Exec("DELETE FROM MigrationProgress WHERE Stage = $s OR Stage LIKE $p", ("$s", unitOrStage), ("$p", unitOrStage + "/%"));
            hot.Commit();
        }
    }
}
