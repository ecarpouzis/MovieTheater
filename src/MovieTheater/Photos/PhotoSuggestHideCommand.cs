using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Photos
{
    /// <summary>
    /// <c>photos-suggest-hide</c> — proposes curation flags for the clutter §1 catalogued (screenshot
    /// piles, misc/reference folders, tiny images, saved graphics) as ONE reviewable batch
    /// (docs/photos-plan.md §2.9).
    ///
    /// <para><b>It cannot hide anything.</b> The pass writes a proposal artifact under
    /// <c>PhotosReportDir</c>; the flags are written only when a family member accepts the batch on the
    /// review surface. That split is the point: §2.9 says these flags are "human-confirmed batch-wise",
    /// and an automatic sweep over a family's photographs is not something to be clever about.</para>
    ///
    /// <para>Chunked and resumable like every other pass here — bounded rows per batch, per-chunk
    /// progress, and a cursor that is also stored in the proposal so a killed run resumes from the
    /// file. Reads no files: every heuristic runs off columns the ingest already persisted.</para>
    /// </summary>
    [Command("photos-suggest-hide", Description = "Propose (never apply) hide flags for screenshot/misc/tiny clutter, as one reviewable batch.")]
    public class PhotoSuggestHideCommand : BasicDICommand, ICommand
    {
        [CommandOption("batch-id", Description = "Proposal batch id (default: hide-<timestamp>). Re-using one appends to it.")]
        public string? BatchId { get; set; }

        [CommandOption("batch-size", Description = "Rows examined per batch (default 500).")]
        public int BatchSize { get; set; } = 500;

        [CommandOption("max-batches", Description = "Batches this invocation runs; 0 drains (default 0).")]
        public int MaxBatches { get; set; }

        [CommandOption("after", Description = "Resume cursor (an asset id) from a prior run's nextCursor.")]
        public string? After { get; set; }

        [CommandOption("rules", Description = "Comma-separated rule subset: screenshot-folder, screenshot-filename, misc-folder, tiny-image, non-photo-format.")]
        public string? Rules { get; set; }

        [CommandOption("min-edge", Description = "Longest-edge pixels below which a still is proposed as too small (default 320).")]
        public int MinEdge { get; set; } = 320;

        [CommandOption("min-bytes", Description = "File size below which a dimensionless still is proposed as clutter (default 20480).")]
        public long MinBytes { get; set; } = 20 * 1024;

        [CommandOption("sqlite", Description = "Run against this SQLite file instead of the configured database (local exercise only).")]
        public string? Sqlite { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public PhotoSuggestHideCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            var rules = new PhotoHideSuggestions.Options { MinEdge = MinEdge, MinBytes = MinBytes };
            if (!string.IsNullOrWhiteSpace(Rules))
            {
                var wanted = Rules!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
                var unknown = wanted.Where(r => !PhotoHideSuggestions.AllRules.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
                if (unknown.Count > 0)
                {
                    w.WriteLine($"Unknown rule(s): {string.Join(", ", unknown)}");
                    w.WriteLine($"Known rules: {string.Join(", ", PhotoHideSuggestions.AllRules)}");
                    return;
                }
                rules.Rules = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            }

            var batchId = !string.IsNullOrWhiteSpace(BatchId)
                ? BatchId!
                : "hide-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

            w.WriteLine($"proposal: {batchId}");
            w.WriteLine($"rules: {string.Join(", ", rules.Rules.OrderBy(r => r, StringComparer.Ordinal))}");
            w.WriteLine("Nothing is hidden by this command — the batch is applied only when a family member accepts it.");
            w.WriteLine();

            var pass = new PhotoSuggestHidePass(BuildDbFactory(w), rules, BatchSize, line => w.WriteLine(line));
            var total = await pass.RunAsync(batchId, After, MaxBatches);

            var counts = total.CountsText();
            w.WriteLine();
            w.WriteLine($"examined {total.Processed}, {total.Remaining} remaining" + (counts.Length > 0 ? $"  [{counts}]" : ""));
            if (total.Remaining > 0)
                w.WriteLine($"More to do: re-run --batch-id \"{batchId}\" --after {total.NextCursor}");
            else
                w.WriteLine("Review it at /photos → Review, or reject it. The proposal is a PhotoCurationBatch row, "
                            + "so the site reads it wherever the database is (Phase 3).");
        }

        /// <summary>Same explicit local lane as the ingest command: the configured connection string is
        /// the live shared database, so exercising a pass end to end has to be possible without
        /// pointing it there.</summary>
        private Func<MovieDb> BuildDbFactory(ConsoleWriter w)
        {
            if (string.IsNullOrWhiteSpace(Sqlite)) return () => dbFactory.CreateDbContext();

            var file = Path.GetFullPath(Sqlite!);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var sqliteOptions = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + file).Options;
            using (var seed = new MovieDb(sqliteOptions)) seed.Database.EnsureCreated();
            w.WriteLine($"sqlite: {file}");
            return () => new MovieDb(sqliteOptions);
        }
    }
}
