using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Streaming
{
    /// <summary>
    /// Backfills Jellyfin's own keyframe repository — the server-side half of the exact-segmentation fix
    /// for the copy-mode freeze (docs/transcode-restart-freeze-plan.md). The patched Jellyfin cuts a
    /// stream-COPIED HLS session on a file's real keyframes ONLY for items whose keyframe list it already
    /// holds; everything else keeps the fixed-length guesses whose numbering drifts on a mid-session
    /// restart. This walks the library calling <c>POST /Videos/{id}/ExtractKeyframes</c> and stamps
    /// <see cref="MediaFile.JfKeyframesUtc"/> on success, which is what lets <c>StreamController</c> stop
    /// force-encoding a long-GOP title.
    ///
    /// <para><b>Extraction happens on the Jellyfin host, not here</b> — a full ffprobe packet walk of the
    /// file over SMB, tens of seconds to several minutes each. Unlike <c>probe-keyframes</c> this needs no
    /// local media mount (it never touches a file), only reachable Jellyfin config, but it is far slower
    /// per row, hence the small default <c>--limit</c>.</para>
    ///
    /// <para><b>Chunked + resumable</b> (global bulk-job rule): <c>--limit</c> items per run, already-stamped
    /// rows skipped unless <c>--force</c>, worst-GOP rows first so the titles that are being force-encoded
    /// today get fixed first. Prints one line per item and a <c>{processed, …, remaining}</c> summary; safe
    /// to re-run forever, and a 404/500 leaves the row unstamped (retried next run) without aborting the
    /// batch. <c>--skip</c> pages past rows that keep failing so a driver loop terminates.</para>
    /// </summary>
    [Command("extract-jellyfin-keyframes", Description = "Backfill Jellyfin's keyframe repository so copied HLS gets exact segmentation (MediaFile.JfKeyframesUtc).")]
    public class ExtractJellyfinKeyframesCommand : BasicDICommand, ICommand
    {
        [CommandOption("limit", Description = "Max items to extract this run (default 50 — each takes minutes).")]
        public int Limit { get; set; } = 50;

        [CommandOption("force", Description = "Re-extract items already stamped with JfKeyframesUtc.")]
        public bool Force { get; set; }

        [CommandOption("playable-id", Description = "Extract just this title's files, ignoring the worst-GOP order.")]
        public int? PlayableId { get; set; }

        [CommandOption("skip", Description = "Skip the first N rows of the queue — pages past items that keep failing.")]
        public int Skip { get; set; }

        [CommandOption("dry-run", Description = "List the items this run would extract and exit without calling Jellyfin.")]
        public bool DryRun { get; set; }

        // Only a stream-copyable source can hit the segment-renumbering bug at all: anything Jellyfin has
        // to re-encode is already cut on encoder-placed keyframes. Extracting for the rest would burn hours
        // of packet walks for no streaming benefit.
        private static readonly string[] CopyableCodecs = { "h264", "hevc" };

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly JellyfinApi jellyfin;

        public ExtractJellyfinKeyframesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            jellyfin = GetRequiredService<JellyfinApi>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var cancel = console.RegisterCancellationHandler();
            var w = console.Output;

            await using var db = await dbFactory.CreateDbContextAsync(cancel);

            var batch = await OrderedQueue(db).Skip(Math.Max(0, Skip)).Take(Math.Max(1, Limit)).ToListAsync(cancel);
            if (batch.Count == 0)
            {
                w.WriteLine("Nothing to extract.");
                return;
            }

            if (DryRun)
            {
                foreach (var file in batch)
                {
                    var gop = file.KeyframeIntervalSeconds is double s ? $"{s:F2}s" : "unprobed";
                    w.WriteLine($"  ? {file.Id} [{gop}] {file.VideoCodec} {file.Path}");
                }
                w.WriteLine("");
                w.WriteLine($"{{ dryRun: {batch.Count}, remaining: {await RemainingAsync(db, cancel)} }}");
                return;
            }

            int processed = 0, stamped = 0, failed = 0;
            foreach (var file in batch)
            {
                cancel.ThrowIfCancellationRequested();
                processed++;

                var started = Stopwatch.StartNew();
                var outcome = await jellyfin.ExtractKeyframesAsync(file.JellyfinItemId!, cancel);
                started.Stop();

                if (!outcome.Ok)
                {
                    failed++;
                    // Unstamped on purpose: a 404 (item/path gone) and a 500 (extraction failed) both mean
                    // Jellyfin holds no list, so the force-encode must stay in place for this file.
                    w.WriteLine($"  ! {file.Id} HTTP {outcome.StatusCode} after {started.Elapsed.TotalSeconds:F0}s " +
                                $"({outcome.Error}): {file.Path}");
                    continue;
                }

                file.JfKeyframesUtc = DateTime.UtcNow;
                // Saved per item rather than per batch: a run is minutes per row, so a Ctrl-C or a dropped
                // connection two hours in must not throw away every extraction Jellyfin already did.
                await db.SaveChangesAsync(cancel);
                stamped++;
                w.WriteLine($"  + {file.Id} {started.Elapsed.TotalSeconds:F0}s {System.IO.Path.GetFileName(file.Path)}");
            }

            w.WriteLine("");
            w.WriteLine($"{{ processed: {processed}, stamped: {stamped}, failed: {failed}, " +
                        $"remaining: {await RemainingAsync(db, cancel)} }}");
        }

        // The true work queue, regardless of this run's --force / --playable-id, so a driver loop can see
        // the job draining.
        private static Task<int> RemainingAsync(MovieDb db, System.Threading.CancellationToken cancel) =>
            db.MediaFiles.CountAsync(f => f.MissingSinceUtc == null && f.JellyfinItemId != null
                                          && f.JfKeyframesUtc == null
                                          && f.VideoCodec != null && CopyableCodecs.Contains(f.VideoCodec), cancel);

        // Present, synced, stream-copyable rows Jellyfin holds no keyframe list for. Selecting on "the
        // stamp is null" is what makes this a resumable QUEUE rather than a blunt re-run over everything.
        //
        // Worst measured GOP first: those are exactly the titles StreamController force-encodes today, so
        // each batch converts GPU-burning encodes back into copies. SQL Server sorts NULL lowest, so DESC
        // leaves unprobed rows at the end — right, because an unprobed row has no known problem. Ties break
        // on biggest bitrate (SizeBytes / DurationTicks — the remuxes), mirroring probe-keyframes.
        // --playable-id names one title's files instead, in play order.
        private IQueryable<MediaFile> OrderedQueue(MovieDb db)
        {
            var q = db.MediaFiles.Where(f => f.MissingSinceUtc == null && f.JellyfinItemId != null
                                             && f.VideoCodec != null && CopyableCodecs.Contains(f.VideoCodec));
            if (!Force)
                q = q.Where(f => f.JfKeyframesUtc == null);

            if (PlayableId is int pid)
                return q.Where(f => f.PlayableId == pid).OrderBy(f => f.Role).ThenBy(f => f.Id);

            return q
                .OrderByDescending(f => f.KeyframeIntervalSeconds)
                .ThenByDescending(f => f.SizeBytes == null || f.DurationTicks == null || f.DurationTicks == 0
                    ? (double?)null
                    : (double)f.SizeBytes.Value / (double)f.DurationTicks.Value)
                .ThenBy(f => f.Id);
        }
    }
}
