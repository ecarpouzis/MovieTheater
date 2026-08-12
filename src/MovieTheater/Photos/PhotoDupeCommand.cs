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
    /// <c>photos-dupes</c> — the §2.6 grouping passes (docs/photos-plan.md): exact copies, near
    /// duplicates, and the variant pairs that are one capture written as several files.
    ///
    /// <para><b>It cannot change a file.</b> Every outcome is a <see cref="PhotoDupeGroup"/> /
    /// <see cref="PhotoDupeMember"/> row. "Merging" the merge-needed phone-backup folders means the
    /// master wins the timeline; the folders on disk are not touched, now or ever (§6).</para>
    ///
    /// <para><b>It cannot resolve a near duplicate either.</b> Near groups are PROPOSED — Pending, with
    /// a master merely suggested — and a pair a human has marked "not the same photo" is never proposed
    /// again. Exact groups arrive auto-mastered but still listed for review; Variant groups are settled
    /// outright, because "the JPEG beside the RAW is the one to show" is not a judgement call.</para>
    ///
    /// <para>Chunked and resumable like every pass here: bounded work per batch,
    /// <c>{processed, remaining, nextCursor}</c> per chunk, <c>--after</c> to resume, <c>--max-batches</c>
    /// to bound one invocation. No file is opened — every lane reads columns the ingest already
    /// persisted, so this runs anywhere the database is reachable.</para>
    /// </summary>
    [Command("photos-dupes", Description = "Group exact copies, near duplicates and RAW/motion/Live-Photo variants — rows only, never a file.")]
    public class PhotoDupeCommand : BasicDICommand, ICommand
    {
        [CommandOption("pass", 'p', Description = "exact | near | variant | all (default all).")]
        public string Pass { get; set; } = "all";

        [CommandOption("batch-size", Description = "Units per batch: SHA keys for exact, rows for near/variant (default 500).")]
        public int BatchSize { get; set; } = 500;

        [CommandOption("max-batches", Description = "Batches this invocation runs per pass; 0 drains (default 0).")]
        public int MaxBatches { get; set; }

        [CommandOption("after", Description = "Resume cursor from a prior run's nextCursor (applies to the FIRST pass of a chained run).")]
        public string? After { get; set; }

        [CommandOption("near-distance", Description = "Near-dupe pHash Hamming threshold in bits, 0-32 (default 8).")]
        public int NearDistance { get; set; } = 8;

        [CommandOption("max-pairs", Description = "Candidate pairs one near batch may emit before handing back a cursor (default 500).")]
        public int MaxPairs { get; set; } = 500;

        [CommandOption("variant-minutes", Description = "How far apart two halves of one capture may be stamped (default 5).")]
        public double VariantMinutes { get; set; } = 5;

        [CommandOption("motion-seconds", Description = "Longest a motion-photo/Live-Photo video half may run (default 10).")]
        public double MotionSeconds { get; set; } = 10;

        [CommandOption("variant-rules", Description = "Comma-separated variant rule subset: raw+jpeg, live-photo, motion-photo.")]
        public string? VariantRules { get; set; }

        [CommandOption("sqlite", Description = "Run against this SQLite file instead of the configured database (local exercise only).")]
        public string? Sqlite { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public PhotoDupeCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            var passes = ParsePasses(Pass);
            if (passes.Count == 0)
            {
                w.WriteLine($"Unknown --pass '{Pass}'. Use exact, near, variant or all.");
                return;
            }

            var options = new PhotoDupeOptions
            {
                BatchSize = BatchSize,
                NearDistance = Math.Clamp(NearDistance, 0, 32),
                MaxPairsPerBatch = Math.Max(1, MaxPairs),
                Variant = new PhotoVariantPairs.Options
                {
                    TimeTolerance = TimeSpan.FromMinutes(Math.Max(0, VariantMinutes)),
                    MaxMotionSeconds = Math.Max(0, MotionSeconds),
                },
            };

            if (!string.IsNullOrWhiteSpace(VariantRules))
            {
                var wanted = VariantRules!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
                var unknown = wanted.Where(r => !PhotoVariantPairs.AllRules.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
                if (unknown.Count > 0)
                {
                    w.WriteLine($"Unknown variant rule(s): {string.Join(", ", unknown)}");
                    w.WriteLine($"Known rules: {string.Join(", ", PhotoVariantPairs.AllRules)}");
                    return;
                }
                options.Variant.Rules = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            }

            w.WriteLine("Nothing on disk is touched by this command — every outcome is a row (§6).");
            w.WriteLine("Near duplicates are PROPOSED only; a family member picks the master at /photos → Dupes.");

            var factory = BuildDbFactory(w);
            var pass0 = passes[0];
            foreach (var pass in passes)
            {
                w.WriteLine();
                w.WriteLine($"── {pass} ──");
                // A cursor belongs to the FIRST pass of a chained run: a SHA means nothing to the near
                // queue, and an id means nothing to the variant walk.
                var cursor = pass == pass0 ? After : null;
                // A fresh engine per pass so the per-run near index is built (and dropped) around the
                // lane that actually needs it.
                var engine = new PhotoDupePass(factory, options, line => w.WriteLine(line));
                var total = await engine.RunAsync(pass, cursor, MaxBatches);

                var counts = total.CountsText();
                w.WriteLine($"{pass}: {total.Processed} examined, {total.Remaining} remaining"
                            + (counts.Length > 0 ? $"  [{counts}]" : ""));
                if (total.Remaining > 0)
                    w.WriteLine($"More to do: re-run --pass {pass.ToString().ToLowerInvariant()} --after \"{total.NextCursor}\"");
            }

            await ReportAsync(factory, w);
        }

        /// <summary>The state of the review queue after the run — the number a human acts on, counted
        /// from the database rather than accumulated by the passes.</summary>
        private static async Task ReportAsync(Func<MovieDb> factory, ConsoleWriter w)
        {
            using var db = factory();
            var byKind = await db.PhotoDupeGroups
                .GroupBy(g => new { g.Kind, g.Status })
                .Select(g => new { g.Key.Kind, g.Key.Status, count = g.Count() })
                .ToListAsync();

            w.WriteLine();
            foreach (var row in byKind.OrderBy(r => r.Kind).ThenBy(r => r.Status))
                w.WriteLine($"  {row.Kind} / {row.Status}: {row.count}");

            var pending = byKind.Where(r => r.Status == PhotoDupeGroupStatus.Pending && r.Kind == PhotoDupeGroupKind.Near)
                .Sum(r => r.count);
            w.WriteLine(pending > 0
                ? $"{pending} near-duplicate group(s) waiting for a master pick at /photos → Dupes."
                : "No near-duplicate groups are waiting for review.");
        }

        private static List<PhotoDupePassKind> ParsePasses(string value)
        {
            var all = new[] { PhotoDupePassKind.Exact, PhotoDupePassKind.Variant, PhotoDupePassKind.Near };
            // Variant before Near on purpose: a motion photo's two halves should already be one settled
            // item before the near lane starts proposing anything about them.
            if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return all.ToList();

            var result = new List<PhotoDupePassKind>();
            foreach (var part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse<PhotoDupePassKind>(part.Trim(), ignoreCase: true, out var pass)
                    || !Enum.IsDefined(typeof(PhotoDupePassKind), pass))
                    return new List<PhotoDupePassKind>();
                result.Add(pass);
            }
            return result;
        }

        /// <summary>Same explicit local lane as the other photo commands: the configured connection
        /// string is the live shared database, so exercising a pass end to end has to be possible
        /// without pointing it there.</summary>
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
