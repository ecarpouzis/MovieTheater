using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieTheater.Services;

namespace MovieTheater.Web
{
    /// <summary>The service's own state, kept beside the images so it survives a pod restart.</summary>
    public sealed class ThumbsRecodeState
    {
        /// <summary>Last relative path completed. The walk is ordered, so this means "resume after it".</summary>
        public string? Cursor { get; set; }
        public int Processed { get; set; }
        public int Rewritten { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public long BytesBefore { get; set; }
        public long BytesAfter { get; set; }
        /// <summary>Set once the walk has run out of candidates. Present = the job never runs again.</summary>
        public DateTime? DoneUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
    }

    public sealed class ThumbsRecodeOptions
    {
        /// <summary>Off in Development: a developer's Posters folder is not the mount, and converting it
        /// achieves nothing while looking like it worked.</summary>
        public bool Enabled { get; init; } = true;
        /// <summary>Files per tick. Small enough that a restart loses almost nothing.</summary>
        public int ChunkSize { get; init; } = 40;
        /// <summary>Pause between ticks — this must never compete with a reader for the pod.</summary>
        public TimeSpan Pause { get; init; } = TimeSpan.FromSeconds(10);
        /// <summary>Let the app finish booting before touching the disk.</summary>
        public TimeSpan StartDelay { get; init; } = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Converts the thumbnails already on the images mount from PNG to WebP, in the background, on the
    /// pod — because the mount is the pod's and nothing outside the cluster can write it.
    ///
    /// <para><b>Why it exists at all.</b> Changing the recipe only changes thumbnails written from now on,
    /// and nothing regenerates an existing one (a poster thumb is written once and <c>HasImage</c> is
    /// authoritative), so the library would serve the old PNGs forever. Measured: 125 KB PNG against
    /// 12.9 KB WebP for the same cover, and the music grid asks for 22 at once.</para>
    ///
    /// <para><b>It is safe to deploy the moment the sniffing serve path is live, which is the same
    /// image.</b> These routes send <c>X-Content-Type-Options: nosniff</c>, so a WebP body labelled
    /// <c>image/png</c> would not render — the conversion and the content-typed serve path
    /// (<see cref="ImageBytes"/>) must ship together, and they do.</para>
    ///
    /// <para><b>The house rules for a long job, all of them.</b> Bounded per tick
    /// (<see cref="ThumbsRecodeOptions.ChunkSize"/> files, then a pause — never "convert everything in one
    /// loop"); observable (every tick logs what it did and what remains); resumable and idempotent (the
    /// cursor is a file beside the images, an already-WebP file is skipped, so a restart continues rather
    /// than restarting and a re-run costs nothing); and it TERMINATES — when the walk runs out it stamps
    /// <c>DoneUtc</c> and never runs again. Failures are counted and stepped over, never retried forever.
    /// The per-file guards live in <see cref="ThumbRecoder"/>.</para>
    /// </summary>
    public sealed class ThumbsRecodeService : BackgroundService
    {
        private readonly MovieTheaterConfiguration config;
        private readonly ILogger<ThumbsRecodeService> log;
        private readonly ThumbsRecodeOptions options;

        public ThumbsRecodeService(MovieTheaterConfiguration config, ILogger<ThumbsRecodeService> log,
            ThumbsRecodeOptions? options = null)
        {
            this.config = config;
            this.log = log;
            this.options = options ?? new ThumbsRecodeOptions();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled) { log.LogInformation("thumbs-recode: disabled"); return; }

            var dir = config.MoviePostersDir;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                log.LogInformation("thumbs-recode: no images directory ({Dir}) — nothing to do", dir ?? "(unset)");
                return;
            }

            try { await Task.Delay(options.StartDelay, stoppingToken); } catch (OperationCanceledException) { return; }

            var statePath = Path.Combine(dir, ThumbRecoder.StateFileName);
            var state = Read(statePath);
            if (state.DoneUtc != null)
            {
                log.LogInformation("thumbs-recode: already complete at {When} ({Rewritten} rewritten, {Saved} bytes saved)",
                    state.DoneUtc, state.Rewritten, state.BytesBefore - state.BytesAfter);
                return;
            }
            state.StartedUtc ??= DateTime.UtcNow;

            log.LogInformation("thumbs-recode: starting from cursor {Cursor}", state.Cursor ?? "(the beginning)");

            while (!stoppingToken.IsCancellationRequested)
            {
                List<string> batch;
                int remaining;
                try
                {
                    // Re-read the listing each tick rather than holding one: the set is small to enumerate
                    // and this way a thumbnail written WHILE the job runs is picked up if it sorts later.
                    var all = ThumbRecoder.Candidates(dir);
                    var pending = string.IsNullOrEmpty(state.Cursor)
                        ? all
                        : all.FindAll(r => string.CompareOrdinal(r, state.Cursor) > 0);
                    batch = pending.GetRange(0, Math.Min(options.ChunkSize, pending.Count));
                    remaining = pending.Count - batch.Count;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "thumbs-recode: could not list {Dir} — retrying next tick", dir);
                    try { await Task.Delay(options.Pause, stoppingToken); } catch (OperationCanceledException) { return; }
                    continue;
                }

                if (batch.Count == 0)
                {
                    state.DoneUtc = DateTime.UtcNow;
                    Write(statePath, state);
                    log.LogInformation(
                        "thumbs-recode: COMPLETE — {Processed} files, {Rewritten} rewritten, {Skipped} skipped, {Failed} failed; {Before} → {After} bytes ({Pct:F1}% smaller), started {Started}",
                        state.Processed, state.Rewritten, state.Skipped, state.Failed, state.BytesBefore, state.BytesAfter,
                        state.BytesBefore > 0 ? 100.0 * (state.BytesBefore - state.BytesAfter) / state.BytesBefore : 0,
                        state.StartedUtc);
                    return;
                }

                var sw = Stopwatch.StartNew();
                int rewrote = 0, skipped = 0, failed = 0;
                long before = 0, after = 0;
                foreach (var rel in batch)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    var outcome = await ThumbRecoder.RecodeAsync(dir, rel, ImageShrinkQuality, apply: true, stoppingToken);
                    state.Processed++;
                    state.Cursor = rel;   // advanced per FILE, so a kill mid-chunk loses at most one
                    if (outcome.Rewritten)
                    {
                        rewrote++; before += outcome.Before; after += outcome.After;
                        state.Rewritten++; state.BytesBefore += outcome.Before; state.BytesAfter += outcome.After;
                    }
                    else if (outcome.Reason.StartsWith("already", StringComparison.Ordinal)
                             || outcome.Reason.StartsWith("webp not smaller", StringComparison.Ordinal))
                    {
                        skipped++; state.Skipped++;
                    }
                    else
                    {
                        failed++; state.Failed++;
                        log.LogWarning("thumbs-recode: {File} — {Reason}", rel, outcome.Reason);
                    }
                }

                Write(statePath, state);
                log.LogInformation(
                    "thumbs-recode: +{Rewrote} rewritten (+{Skipped} skipped, +{Failed} failed) in {Ms} ms; {Before} → {After} B this chunk; {Remaining} remaining; cursor {Cursor}",
                    rewrote, skipped, failed, sw.ElapsedMilliseconds, before, after, remaining, state.Cursor);

                try { await Task.Delay(options.Pause, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>The site-wide thumbnail knee, so a recoded file matches a freshly-written one.</summary>
        private const int ImageShrinkQuality = MovieTheater.Services.Poster.ImageShrinkService.ThumbnailQuality;

        private ThumbsRecodeState Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return new ThumbsRecodeState();
                return JsonSerializer.Deserialize<ThumbsRecodeState>(File.ReadAllText(path)) ?? new ThumbsRecodeState();
            }
            catch (Exception ex)
            {
                // An unreadable state file must not mean "start over and redo 30k files": stop instead and
                // let a human look. Re-running from the beginning would be harmless (already-WebP files
                // skip) but slow, and silently losing the cursor is the kind of thing nobody notices.
                log.LogWarning(ex, "thumbs-recode: state file {Path} unreadable — treating as a fresh start", path);
                return new ThumbsRecodeState();
            }
        }

        private void Write(string path, ThumbsRecodeState state)
        {
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Losing the cursor costs time, not data — the next pass re-walks and skips what is done.
                log.LogWarning(ex, "thumbs-recode: could not persist state to {Path}", path);
            }
        }
    }
}
