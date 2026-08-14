using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Core;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Streaming
{
    /// <summary>
    /// Stamps <see cref="MediaFile.ContentFingerprint"/> across the library — the content-identity
    /// half of keyframe custody (see <see cref="MediaKeyframes"/>). Sampled hashing
    /// (<see cref="MediaFingerprint"/>, ~3.5 MB read per file) keeps the whole 18k-file backfill to
    /// an evening of NAS reads instead of a second 25 TB marathon.
    ///
    /// <para><b>Runs on the NAS-attached host only.</b> The prod pods cannot read the collection;
    /// this command reads the files at <see cref="MediaFile.Path"/> directly, which is exactly why the
    /// fingerprint lives in the database — the sync, running where the files are unreachable, can then
    /// use it as a pure lookup key.</para>
    ///
    /// <para><b>Chunked + resumable</b> (global bulk-job rule): <c>--limit</c> rows per run, the queue
    /// is "fingerprint is null", progress prints per chunk, and every stamp saves immediately so a
    /// Ctrl-C loses at most one file's work. An unreadable file is reported and left null (retried
    /// next run; <c>--skip</c> pages past a persistent failure so a driver loop terminates).</para>
    /// </summary>
    [Command("fingerprint-media-files", Description = "Stamp MediaFile.ContentFingerprint (sampled content hash) so keyframe custody survives renames.")]
    public class FingerprintMediaFilesCommand : BasicDICommand, ICommand
    {
        [CommandOption("limit", Description = "Max files to fingerprint this run (default 500 — each is ~3.5 MB of reads).")]
        public int Limit { get; set; } = 500;

        [CommandOption("skip", Description = "Skip the first N rows of the queue — pages past files that keep failing.")]
        public int Skip { get; set; }

        [CommandOption("dry-run", Description = "List the files this run would read and exit without touching anything.")]
        public bool DryRun { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public FingerprintMediaFilesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var cancel = console.RegisterCancellationHandler();
            var w = console.Output;

            await using var db = await dbFactory.CreateDbContextAsync(cancel);

            var batch = await Queue(db).Skip(Math.Max(0, Skip)).Take(Math.Max(1, Limit)).ToListAsync(cancel);
            if (batch.Count == 0)
            {
                w.WriteLine("Nothing to fingerprint.");
                return;
            }

            if (DryRun)
            {
                foreach (var file in batch)
                    w.WriteLine($"  ? {file.Id} {file.Path}");
                w.WriteLine("");
                w.WriteLine($"{{ dryRun: {batch.Count}, remaining: {await RemainingAsync(db, cancel)} }}");
                return;
            }

            int processed = 0, stamped = 0, missing = 0, failed = 0;
            foreach (var file in batch)
            {
                cancel.ThrowIfCancellationRequested();
                processed++;

                if (!File.Exists(file.Path))
                {
                    // Not stamped and not flagged missing — MissingSinceUtc is the SYNC's verdict to
                    // give (it can tell a moved file from a gone one); this pass just reports and moves on.
                    missing++;
                    w.WriteLine($"  ! {file.Id} not on disk: {file.Path}");
                    continue;
                }

                try
                {
                    var started = Stopwatch.StartNew();
                    file.ContentFingerprint = await MediaFingerprint.ComputeFileAsync(file.Path, cancel);
                    started.Stop();
                    // Saved per file: cheap, and a killed run keeps everything it already measured.
                    await db.SaveChangesAsync(cancel);
                    stamped++;
                    w.WriteLine($"  + {file.Id} {started.ElapsedMilliseconds}ms {System.IO.Path.GetFileName(file.Path)}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    w.WriteLine($"  ! {file.Id} {e.GetType().Name}: {e.Message}");
                }
            }

            w.WriteLine("");
            w.WriteLine($"{{ processed: {processed}, stamped: {stamped}, missing: {missing}, failed: {failed}, " +
                        $"remaining: {await RemainingAsync(db, cancel)} }}");
        }

        // Present files without a fingerprint. Keyframe-stamped rows first: they are the ones whose
        // banked lists a rename would otherwise cost, so they get rename-proofed soonest.
        private static IQueryable<MediaFile> Queue(MovieDb db) =>
            db.MediaFiles.Where(f => f.MissingSinceUtc == null && f.ContentFingerprint == null)
                .OrderByDescending(f => f.JfKeyframesUtc != null).ThenByDescending(f => f.Id);

        private static Task<int> RemainingAsync(MovieDb db, System.Threading.CancellationToken cancel) =>
            db.MediaFiles.CountAsync(f => f.MissingSinceUtc == null && f.ContentFingerprint == null, cancel);
    }
}
