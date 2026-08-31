using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        /// <summary>Which walk produced this state (<see cref="ThumbRecoder.Scope"/>). A different scope
        /// re-opens the job — that is how a root added later is picked up despite a DoneUtc.</summary>
        public string? Scope { get; set; }
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

            var roots = new List<ThumbRecoder.Root> { new("posters", dir) };
            var boardgames = config.BoardgameImagesDir;
            if (!string.IsNullOrWhiteSpace(boardgames) && Directory.Exists(boardgames))
                roots.Add(new ThumbRecoder.Root("boardgames", boardgames));
            else
                log.LogInformation("thumbs-recode: no boardgame images directory ({Dir}) — skipping that root", boardgames ?? "(unset)");

            var statePath = Path.Combine(dir, ThumbRecoder.StateFileName);
            var state = Read(statePath);
            if (state.DoneUtc != null && state.Scope != ThumbRecoder.Scope)
            {
                // A build that covers MORE than the one that finished: re-open and re-walk. Everything
                // already converted is a read and a sniff, so this costs minutes, not another pass.
                log.LogInformation("thumbs-recode: scope changed ({Old} → {New}) — re-walking from the start",
                    state.Scope ?? "(unrecorded)", ThumbRecoder.Scope);
                state = new ThumbsRecodeState();
            }
            state.Scope = ThumbRecoder.Scope;
            // Persist the re-opened state at once: until it lands, the file on disk still carries the old
            // walk's cursor, and the per-tick merge below would read it back.
            if (state.Cursor == null) Write(statePath, state);
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
                    // If the deployment ever runs MORE THAN ONE replica, both share this mount and this
                    // state file. That is safe but wasteful: each converts forward from its own cursor and
                    // the other simply finds the file already WebP and skips it. Taking the furthest cursor
                    // of the two each tick makes them converge instead of re-treading each other's ground —
                    // a cursor means "everything at or before this is done", so the maximum is always true.
                    var persisted = Read(statePath);
                    // Only merge a cursor written by the SAME scope. A stale file from a narrower walk
                    // holds a cursor in a different coordinate system (and, after a scope reset, one this
                    // pass has deliberately abandoned) — merging it silently undid the reset.
                    if (persisted.Scope == ThumbRecoder.Scope
                        && !string.IsNullOrEmpty(persisted.Cursor)
                        && string.CompareOrdinal(persisted.Cursor, state.Cursor ?? "") > 0)
                        state.Cursor = persisted.Cursor;

                    // Re-read the listing each tick rather than holding one: the set is small to enumerate
                    // and this way a thumbnail written WHILE the job runs is picked up if it sorts later.
                    var all = ThumbRecoder.Candidates(roots);
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
                    ThumbRecoder.Outcome outcome;
                    var (rootKey, relPath) = ThumbRecoder.Split(rel);
                    var rootDir = roots.FirstOrDefault(r => r.Key == rootKey)?.Dir ?? dir;
                    // A shutdown mid-file leaves the cursor where it was, so that file is picked up again
                    // on the next boot instead of being silently skipped forever.
                    try { outcome = await ThumbRecoder.RecodeAsync(rootDir, relPath, ImageShrinkQuality, apply: true, stoppingToken); }
                    catch (OperationCanceledException) { break; }
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
                // Starting over is SAFE — every already-WebP file is skipped, so a re-walk costs a listing
                // and a few thousand cheap reads, not another conversion. It is only slow, and losing the
                // cursor silently is worse than saying so loudly, which is what this warning is for.
                log.LogWarning(ex, "thumbs-recode: state file {Path} unreadable — re-walking from the start (already-converted files will skip)", path);
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
