using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Controllers;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Streaming
{
    /// <summary>
    /// Fills <see cref="MediaFile.KeyframeIntervalSeconds"/> by sampling each file with ffprobe
    /// (docs/transcode-restart-freeze-plan.md §Part 1). Jellyfin's API doesn't expose keyframe spacing
    /// and nothing else in the repo probes files, so this is the only source: a copy-mode HLS session on
    /// a source whose keyframes fall further apart than the segment length gets its segments renumbered
    /// on every mid-session restart, which freezes the picture. <c>StreamController</c> reads the column
    /// and force-encodes those titles instead.
    ///
    /// <para><b>Must run on a machine with the library drives mapped.</b> <see cref="MediaFile.Path"/> is
    /// a Windows path and prod is a Linux pod with no media mount — the gateway design deliberately keeps
    /// video bytes off the API server, so this can never be an at-request-time probe.</para>
    ///
    /// <para><b>Chunked + resumable</b> (global bulk-job rule): <c>--limit</c> rows per run, already-probed
    /// rows skipped unless <c>--force</c>, biggest-bitrate rows (remuxes — the likely offenders) first, so
    /// the fix is effective after the first batch. Prints per-file results and a
    /// <c>{processed, remaining, …}</c> summary; safe to re-run forever, and a dead path or a failed probe
    /// leaves the row null and never aborts the run. Reads only the exact per-row paths — it never scans
    /// a directory, and per window only ~30 seconds of packets come off the NAS.</para>
    /// </summary>
    [Command("probe-keyframes", Description = "ffprobe media files for keyframe spacing (MediaFile.KeyframeIntervalSeconds).")]
    public class ProbeKeyframesCommand : BasicDICommand, ICommand
    {
        [CommandOption("ffprobe", Description = "ffprobe executable to run.")]
        public string FfprobePath { get; set; } = @"C:\Program Files\Jellyfin\Server\ffprobe.exe";

        [CommandOption("limit", Description = "Max files to probe this run (default 25).")]
        public int Limit { get; set; } = 25;

        [CommandOption("force", Description = "Re-probe files that already carry a keyframe interval.")]
        public bool Force { get; set; }

        [CommandOption("playable-id", Description = "Probe just this title's files, ignoring the bitrate order.")]
        public int? PlayableId { get; set; }

        [CommandOption("skip", Description = "Skip the first N rows of the queue — pages past files that keep failing to probe.")]
        public int Skip { get; set; }

        private const long TicksPerSecond = 10_000_000;

        // One ffprobe read window. Also the recorded floor when a window holds fewer than two keyframes:
        // that only happens when spacing exceeds the window, and any value > the copy path's segment
        // length (6) tells the controller everything it needs.
        private const double WindowSeconds = 30;

        // Sample mid-file, never the head: the opening scene cuts carry extra keyframes and would
        // underestimate the steady-state GOP. Neither the tail — end credits are near-static and their
        // long gaps are real but unrepresentative of the body.
        //
        // SIX windows, doubled from the original three (2026-07-29). The estimator is a max over samples,
        // so it can only ever UNDER-report the file's true worst gap: three 30 s windows covered ~1.5% of
        // a feature, which made a just-under-threshold reading ("copy is safe") the least trustworthy
        // value the probe produced — precisely the wrong place to be optimistic, since this measurement
        // decides whether a title can be stream-copied at all. Six windows double the coverage and roughly
        // double the probe's wall-clock (~65 -> ~33 files/min); the file read is bounded either way,
        // ~30 s of packets per window and nothing decoded.
        private static readonly double[] SampleFractions = { 0.20, 0.32, 0.44, 0.56, 0.68, 0.80 };

        // Fallback offsets for a row with no usable DurationTicks — a window past the end simply reads
        // nothing and is dropped rather than counted as a spacing floor.
        private static readonly double[] BlindOffsets = { 300, 900, 1800, 2700, 3600, 5400 };

        // One dead NAS path must not hang the run, so every invocation is bounded and a timeout is
        // recorded as a probe failure for that file only.
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ProbeKeyframesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var cancel = console.RegisterCancellationHandler();
            var w = console.Output;

            if (!System.IO.File.Exists(FfprobePath))
            {
                console.Error.WriteLine($"ffprobe not found at {FfprobePath} — pass --ffprobe <path>. Aborting.");
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancel);

            var batch = await OrderedQueue(db).Skip(Math.Max(0, Skip)).Take(Math.Max(1, Limit)).ToListAsync(cancel);
            if (batch.Count == 0)
            {
                w.WriteLine("Nothing to probe.");
                return;
            }

            int processed = 0, skippedMissingFile = 0, probeFailed = 0, forceCandidates = 0;
            foreach (var file in batch)
            {
                cancel.ThrowIfCancellationRequested();

                if (!System.IO.File.Exists(file.Path))
                {
                    skippedMissingFile++;
                    w.WriteLine($"  ! {file.Id} no file on disk: {file.Path}");
                    continue;
                }

                var probe = await ProbeAsync(file.Path, file.DurationTicks, cancel);
                var (spacing, detail) = (probe.Worst, probe.Detail);
                if (spacing == null)
                {
                    probeFailed++;
                    w.WriteLine($"  ! {file.Id} probe produced nothing ({detail}): {file.Path}");
                    continue;
                }

                file.KeyframeIntervalSeconds = spacing;
                // Everything the probe measured, kept: the first pass stored only the worst gap and threw
                // the rest away, which made re-analysis impossible without re-reading every file.
                file.KeyframeMinSeconds = probe.Best;
                file.KeyframeSampleDetail = detail.Length <= 256 ? detail : detail[..256];
                file.KeyframeSpacingCensored = probe.Censored;
                file.KeyframeProbedUtc = DateTime.UtcNow;
                processed++;
                if (spacing > StreamController.CopyHlsSegmentSeconds) forceCandidates++;
                w.WriteLine($"  {file.Id} {spacing.Value:F2}s [{detail}] {Path.GetFileName(file.Path)}");
            }

            if (processed > 0)
                await db.SaveChangesAsync(cancel);

            // The true work queue, regardless of this run's --force / --playable-id. Split so a driver can
            // see the two jobs: rows with NO measurement at all (the streaming fix can't act on those), and
            // rows measured by the first pass, which kept only the worst-case number and discarded the
            // per-window detail — those need re-reading to recover it.
            var unprobed = await db.MediaFiles
                .CountAsync(f => f.MissingSinceUtc == null && f.JellyfinItemId != null
                                 && f.KeyframeIntervalSeconds == null, cancel);
            var detailless = await db.MediaFiles
                .CountAsync(f => f.MissingSinceUtc == null && f.JellyfinItemId != null
                                 && f.KeyframeIntervalSeconds != null && f.KeyframeSampleDetail == null, cancel);

            w.WriteLine("");
            w.WriteLine($"{{ processed: {processed}, remaining: {unprobed + detailless}, unprobed: {unprobed}, " +
                        $"detailBackfill: {detailless}, skippedMissingFile: {skippedMissingFile}, " +
                        $"probeFailed: {probeFailed}, forceCandidates: {forceCandidates} }}");
        }

        // Rows worth probing: present, synced files that are either unmeasured, or measured by the first
        // pass that discarded the per-window detail. Selecting on "the new field is null" is what makes the
        // detail backfill a resumable QUEUE rather than a blunt --force over everything: re-running after
        // an interruption continues, and a row is never re-read once it carries detail.
        //
        // Unprobed rows go FIRST — they have no measurement at all, so StreamController cannot act on them,
        // whereas a detail-less row still has a working copy/encode decision. Within each group, biggest
        // bitrate (SizeBytes / DurationTicks) first — the remuxes, where long-GOP copy sessions actually
        // hurt — with unknown-bitrate rows last (SQL Server sorts NULL lowest, so DESC leaves them at the
        // end). --playable-id names one title's files instead, in play order.
        private IQueryable<MediaFile> OrderedQueue(MovieDb db)
        {
            var q = db.MediaFiles.Where(f => f.MissingSinceUtc == null && f.JellyfinItemId != null);
            if (!Force)
                q = q.Where(f => f.KeyframeIntervalSeconds == null || f.KeyframeSampleDetail == null);

            if (PlayableId is int pid)
                return q.Where(f => f.PlayableId == pid).OrderBy(f => f.Role).ThenBy(f => f.Id);

            return q
                .OrderBy(f => f.KeyframeIntervalSeconds == null ? 0 : 1)
                .ThenByDescending(f => f.SizeBytes == null || f.DurationTicks == null || f.DurationTicks == 0
                    ? (double?)null
                    : (double)f.SizeBytes.Value / (double)f.DurationTicks.Value)
                .ThenBy(f => f.Id);
        }

        /// <summary>
        /// Everything one file's sampling produced. <see cref="Worst"/> drives the copy/encode decision;
        /// the rest is persisted alongside it so the sampling can be re-analysed without re-reading the
        /// file (see [[persist-hard-won-measurements]] — the first pass kept only Worst and the rest had
        /// to be re-measured off the NAS). <see cref="Worst"/> null = no window yielded anything.
        /// </summary>
        private sealed record ProbeResult(double? Worst, double? Best, bool Censored, string Detail);

        // Worst and best keyframe gap across the sampled windows, whether any window hit the censoring
        // floor, and a per-window readout. Null Worst = no window yielded anything (dead path, unreadable
        // file, ffprobe error).
        private async Task<ProbeResult> ProbeAsync(string path, long? durationTicks, CancellationToken cancel)
        {
            double seconds = (durationTicks ?? 0) / (double)TicksPerSecond;
            var offsets = seconds > 0
                ? SampleFractions.Select(f => seconds * f).ToArray()
                : BlindOffsets;

            double? worst = null, best = null;
            bool censored = false;
            var parts = new List<string>();
            foreach (var offset in offsets)
            {
                var window = await ReadWindowAsync(path, offset, cancel);
                if (window.Error.Length > 0)
                    return new ProbeResult(null, null, false, window.Error);
                if (window.Packets == 0)
                {
                    parts.Add($"{offset:F0}s:-");   // past the end (blind offsets) or an empty window
                    continue;
                }

                double candidate;
                if (window.KeyframeTimes.Count < 2)
                {
                    candidate = WindowSeconds;      // spacing exceeds the window: record the floor
                    censored = true;                // ...so this is a ">=", not a measurement
                    parts.Add($"{offset:F0}s:>{WindowSeconds:F0}");
                }
                else
                {
                    candidate = 0;
                    for (int i = 1; i < window.KeyframeTimes.Count; i++)
                        candidate = Math.Max(candidate, window.KeyframeTimes[i] - window.KeyframeTimes[i - 1]);
                    parts.Add($"{offset:F0}s:{candidate:F2}");
                }

                if (worst == null || candidate > worst) worst = candidate;
                if (best == null || candidate < best) best = candidate;
            }

            return new ProbeResult(worst, best, censored, string.Join(" ", parts));
        }

        // Empty Error = the window read cleanly (it may still hold no packets, e.g. a blind offset past
        // the end of the file).
        private sealed record ProbeWindow(List<double> KeyframeTimes, int Packets, string Error);

        // Packets, not frames: nothing is decoded and only this window is read. csv rows come back as
        // "packet,<pts_time>,<dts_time>,<flags>"; flags lead with K on a keyframe.
        private async Task<ProbeWindow> ReadWindowAsync(string path, double offsetSeconds, CancellationToken cancel)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "packet=pts_time,dts_time,flags",
                "-of", "csv",
                "-read_intervals", $"{offsetSeconds.ToString("F3", CultureInfo.InvariantCulture)}%+{WindowSeconds:F0}",
                path,
            }) psi.ArgumentList.Add(arg);

            string stdout;
            using (var p = Process.Start(psi)!)
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(ProbeTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
                try
                {
                    await p.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    cancel.ThrowIfCancellationRequested();   // the operator pressed Ctrl-C; anything else is this file's problem
                    return new ProbeWindow(new List<double>(), 0, $"timed out after {ProbeTimeout.TotalSeconds:F0}s");
                }
                if (p.ExitCode != 0)
                    return new ProbeWindow(new List<double>(), 0, Trunc((await errTask).Trim()) is { Length: > 0 } e ? e : $"ffprobe exit {p.ExitCode}");
                stdout = await outTask;
            }

            var times = new List<double>();
            int packets = 0;
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var fields = line.Split(',');
                if (fields.Length < 4 || !fields[0].Equals("packet", StringComparison.OrdinalIgnoreCase)) continue;
                packets++;
                if (fields[^1].Length == 0 || fields[^1][0] != 'K') continue;
                // Containers that carry no presentation stamps report pts_time as N/A; dts_time is the
                // same value for a keyframe, so it's a lossless fallback here.
                var stamp = fields[1] == "N/A" ? fields[2] : fields[1];
                if (double.TryParse(stamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
                    times.Add(at);
            }
            times.Sort();
            return new ProbeWindow(times, packets, "");
        }

        private static string Trunc(string s) => s.Length <= 200 ? s : s[..200];
    }
}
