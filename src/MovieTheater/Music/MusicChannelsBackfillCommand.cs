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

namespace MovieTheater.Music
{
    /// <summary>
    /// Fills <c>MusicTrack.Channels</c> for tracks ingested before that column existed, so the player
    /// can size its Web Audio destination to the source and stop the visualizer folding surround down
    /// to stereo (see MusicPlayerContext.applyOutputChannels).
    ///
    /// <para>This can't ride along on <c>music-ingest</c>: that command deliberately skips a file whose
    /// size and mtime are unchanged without re-opening it, which is exactly every already-ingested
    /// track. Backfilling through it would mean touching the whole library to defeat its own fast
    /// path. So this is a separate, narrower pass that only opens files whose Channels is still
    /// unknown.</para>
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run-first: prints what it found and writes nothing unless
    /// <c>--apply</c>. Bounded: at most <c>--limit</c> TRACKS per run. Resumable and idempotent: the
    /// work queue IS "Channels IS NULL" ordered by Id, so an --apply run shrinks it and a plain re-run
    /// picks up exactly where it stopped; <c>--after</c> pages a dry run, which writes nothing and so
    /// can't shrink anything. Terminates deterministically: a file that is missing, gone from disk, or
    /// unreadable is stamped with the 0 sentinel rather than left NULL, so it leaves the queue instead
    /// of being retried by every future run. Never destructive: only ever fills a NULL column.</para>
    /// </summary>
    [Command("music-backfill-channels", Description = "Fill MusicTrack.Channels from the audio files (dry-run unless --apply).")]
    public class MusicChannelsBackfillCommand : BasicDICommand, ICommand
    {
        [CommandOption("root", 'r', Description = "Music library root. Default: MusicLibraryDir from config.")]
        public string? Root { get; set; }

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max TRACKS to process this run (default 500).")]
        public int Limit { get; set; } = 500;

        [CommandOption("after", Description = "Resume cursor: skip tracks whose Id is ≤ this. Only needed to page a dry run.")]
        public int After { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public MusicChannelsBackfillCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var rootSetting = !string.IsNullOrWhiteSpace(Root) ? Root : config.MusicLibraryDir;
            if (string.IsNullOrWhiteSpace(rootSetting))
            {
                w.WriteLine("No music root: pass --root or set MusicLibraryDir in config.");
                return;
            }
            var root = Path.GetFullPath(rootSetting);
            if (!Directory.Exists(root)) { w.WriteLine($"Music root not found: {root}"); return; }

            await using var db = await dbFactory.CreateDbContextAsync();

            var pendingTotal = await db.MusicTracks.CountAsync(t => t.Channels == null);
            var batch = await db.MusicTracks
                .Where(t => t.Channels == null && t.Id > After)
                .OrderBy(t => t.Id)
                .Take(Math.Max(1, Limit))
                .ToListAsync();

            // Channel-count distribution across the batch, so a dry run answers the question that
            // actually matters — "is there ANY multichannel music in here?" — before writing a thing.
            var histogram = new SortedDictionary<int, int>();
            int read = 0, absent = 0, unreadable = 0;

            foreach (var track in batch)
            {
                int channels;
                if (track.MissingSinceUtc != null)
                {
                    // No file to open. Stamp the sentinel anyway: leaving it NULL would keep it in the
                    // queue forever and `remaining` would never reach 0. If the file comes back,
                    // music-ingest's changed-file path refreshes Channels along with the other
                    // machine fields.
                    channels = 0;
                    absent++;
                }
                else
                {
                    var path = Path.Combine(root, track.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path)) { channels = 0; absent++; }
                    else
                    {
                        try
                        {
                            channels = MusicIngestCommand.ReadChannels(new ATL.Track(path));
                            if (channels == 0) unreadable++; else read++;
                        }
                        catch
                        {
                            // A corrupt or locked file is counted, not fatal — same posture as ingest.
                            channels = 0;
                            unreadable++;
                        }
                    }
                }

                histogram[channels] = histogram.GetValueOrDefault(channels) + 1;
                if (Apply) track.Channels = channels;
            }

            if (Apply) await db.SaveChangesAsync();

            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            // In --apply mode the processed rows have left the queue, so `remaining` is the real
            // countdown. A dry run writes nothing, so it reports the queue it did NOT shrink — page it
            // with --after instead of expecting this number to move.
            var remaining = Apply ? Math.Max(0, pendingTotal - batch.Count) : pendingTotal;

            w.WriteLine();
            w.WriteLine($"{pendingTotal} track(s) with unknown Channels; this run read {read}, " +
                        $"{absent} with no file on disk, {unreadable} unreadable" +
                        (Apply ? "." : " (DRY RUN — nothing written)."));
            foreach (var (channels, count) in histogram)
                w.WriteLine($"  {(channels == 0 ? "unknown" : channels + "ch")}: {count}");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: {nextCursor} }}");
        }
    }
}
