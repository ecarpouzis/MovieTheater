using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Loads the resolved on-disk titles (the Phase-2 reconcile output,
    /// <c>data/_ingest_phase2_dryrun.csv</c>) into the Movie table as new rows, each tagged
    /// with its <see cref="Movie.ReviewBatch"/> / provenance / confidence / source folder so
    /// the whole batch is quarantined from browse and reviewable on the site before it's
    /// trusted. Idempotent by <c>imdbID</c> — re-running skips titles already in the DB.
    /// Legacy and enrichment columns are left null so the bulk IMDB/TMDB/RT enrichment can
    /// fill them later. See the library-ingest effort and docs/metadata-enrichment-plan.md.
    /// </summary>
    [Command("ingest-library", Description = "Insert resolved on-disk titles as review-tagged Movie rows (idempotent by imdbID).")]
    public class IngestLibraryCommand : BasicDICommand, ICommand
    {
        [CommandOption("csv", Description = "Path to the Phase-2 worklist CSV (default: data/_ingest_phase2_dryrun.csv).")]
        public string CsvPath { get; set; } = Path.Combine("data", "_ingest_phase2_dryrun.csv");

        [CommandOption("dry-run", Description = "Parse and report what would be inserted, without writing.")]
        public bool DryRun { get; set; }

        [CommandOption("limit", Description = "Max rows to insert this run.")]
        public int? Limit { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly ILogger<IngestLibraryCommand> logger;

        public IngestLibraryCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            logger = GetRequiredService<ILogger<IngestLibraryCommand>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var path = Path.GetFullPath(CsvPath);
            if (!File.Exists(path))
            {
                console.Error.WriteLine($"CSV not found: {path}");
                return;
            }

            var rows = ParseCsv(path);
            console.Output.WriteLine($"Read {rows.Count} row(s) from {path}");

            await using var db = await dbFactory.CreateDbContextAsync();

            // tt already in the DB — the idempotency guard so re-runs never duplicate.
            var existing = new HashSet<string>(
                await db.Movies.Where(m => m.imdbID != null && m.imdbID != "").Select(m => m.imdbID!).ToListAsync(),
                StringComparer.OrdinalIgnoreCase);

            int skippedExisting = 0, skippedNoTt = 0, badType = 0;
            var seenThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toAdd = new List<Movie>();

            foreach (var r in rows)
            {
                var tt = (r.Get("imdbID") ?? "").Trim();
                if (tt.Length == 0) { skippedNoTt++; continue; }
                if (existing.Contains(tt) || !seenThisRun.Add(tt)) { skippedExisting++; continue; }

                if (!Enum.TryParse<TitleType>((r.Get("titletype") ?? "").Trim(), ignoreCase: true, out var titleType))
                { titleType = TitleType.Unknown; badType++; }

                toAdd.Add(new Movie
                {
                    Title = NullIfEmpty(r.Get("title")),
                    imdbID = tt,
                    TitleType = titleType,
                    ReviewBatch = NullIfEmpty(r.Get("ReviewBatch")) ?? "library-ingest",
                    ReviewProvenance = NullIfEmpty(r.Get("provenance")),
                    ReviewConfidence = NullIfEmpty(r.Get("confidence")),
                    ReviewSourcePath = NullIfEmpty(r.Get("path")),
                    UploadedDate = DateTime.Now,
                    // Every movie gets a Playable (Phase-4 cutover); existing rows got theirs from the backfill.
                    Playable = new Playable { Kind = PlayableKind.Movie },
                });
                if (Limit.HasValue && toAdd.Count >= Limit.Value) break;
            }

            console.Output.WriteLine(
                $"Prepared {toAdd.Count} new row(s); skipped {skippedExisting} already-present, {skippedNoTt} without an id"
                + (badType > 0 ? $", {badType} fell back to TitleType.Unknown" : ""));
            foreach (var g in toAdd.GroupBy(m => m.TitleType).OrderByDescending(g => g.Count()))
                console.Output.WriteLine($"  {g.Key}: {g.Count()}");

            if (DryRun)
            {
                console.Output.WriteLine("Dry run — no rows written. First rows that would insert:");
                foreach (var m in toAdd.Take(15))
                    console.Output.WriteLine($"  [{m.ReviewConfidence,-6} {m.ReviewProvenance}] {m.TitleType,-12} {Trunc(m.Title, 48),-50} {m.imdbID}");
                return;
            }

            db.Movies.AddRange(toAdd);
            await db.SaveChangesAsync();
            console.Output.WriteLine($"Inserted {toAdd.Count} new Movie row(s), all tagged for on-site review (ReviewBatch). "
                + "They are quarantined from browse until approved.");
            logger.LogInformation("ingest-library inserted {Count} rows from {Csv}", toAdd.Count, path);
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static string Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));

        private sealed class Row
        {
            private readonly Dictionary<string, string> fields;
            public Row(Dictionary<string, string> fields) => this.fields = fields;
            public string? Get(string col) => fields.TryGetValue(col, out var v) ? v : null;
        }

        private static List<Row> ParseCsv(string path)
        {
            var records = ParseRecords(path);
            var result = new List<Row>();
            if (records.Count == 0) return result;
            var header = records[0];
            for (int i = 1; i < records.Count; i++)
            {
                var f = records[i];
                if (f.Count == 1 && f[0].Length == 0) continue; // blank trailing line
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < header.Count && c < f.Count; c++)
                    dict[header[c]] = f[c];
                result.Add(new Row(dict));
            }
            return result;
        }

        // Minimal RFC-4180 reader: double-quoted fields, "" escapes, embedded commas/newlines.
        private static List<List<string>> ParseRecords(string path)
        {
            var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'); // ReadAllText usually strips the BOM; this is belt-and-suspenders
            var records = new List<List<string>>();
            var field = new StringBuilder();
            var record = new List<string>();
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(ch);
                }
                else
                {
                    switch (ch)
                    {
                        case '"': inQuotes = true; break;
                        case ',': record.Add(field.ToString()); field.Clear(); break;
                        case '\r': break;
                        case '\n':
                            record.Add(field.ToString()); field.Clear();
                            records.Add(record); record = new List<string>();
                            break;
                        default: field.Append(ch); break;
                    }
                }
            }
            if (field.Length > 0 || record.Count > 0)
            {
                record.Add(field.ToString());
                records.Add(record);
            }
            return records;
        }
    }
}
