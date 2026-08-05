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
    /// Ingests the music library folder tree into the <c>MusicArtist</c>/<c>MusicAlbum</c>/
    /// <c>MusicTrack</c> catalog (music-plan.md §2.3). Identity comes from the curated folder grammar
    /// (§1): artist = top-level folder, album = first-level subfolder, track = audio file; tags
    /// (read via ATL) fill Title/TrackNo/duration/bitrate and are kept raw in TagArtist/TagAlbum.
    /// Embedded/sidecar lyrics are captured into <c>MusicTrackLyrics</c> while the file is open (§2.7).
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run-first: prints <c>{inserted, updated, skipped, remaining,
    /// nextCursor}</c> and writes nothing unless <c>--apply</c>. Bounded + resumable: processes at most
    /// <c>--limit</c> ARTIST FOLDERS per run, ordered by folder name; the caller loops passing
    /// <c>--after &lt;nextCursor&gt;</c> until <c>remaining</c> is 0. Idempotent: upserts keyed on the
    /// unique FolderName/FolderPath/RelativePath indexes, and an unchanged file (same size + mtime) is
    /// skipped without re-reading its tags. Never deletes: <c>--reconcile</c> stamps vanished tracks'
    /// <c>MissingSinceUtc</c> (scoped to this run's artists), it does not remove rows.</para>
    /// </summary>
    [Command("music-ingest", Description = "Scan the music library into the Music catalog (dry-run unless --apply).")]
    public class MusicIngestCommand : BasicDICommand, ICommand
    {
        [CommandOption("root", 'r', Description = "Music library root (holds one folder per artist). Default: MusicLibraryDir from config.")]
        public string? Root { get; set; }

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max ARTIST FOLDERS to process this run (default 10).")]
        public int Limit { get; set; } = 10;

        [CommandOption("after", Description = "Resume cursor: skip artist folders whose name is ≤ this (from a prior run's nextCursor).")]
        public string After { get; set; } = "";

        [CommandOption("reconcile", Description = "Also stamp MissingSinceUtc on this run's tracks whose file has vanished (never deletes).")]
        public bool Reconcile { get; set; }

        /// <summary>Formats every modern browser decodes natively via &lt;audio&gt; + Range (§2.1).</summary>
        private static readonly Dictionary<string, string> NativeCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            [".mp3"] = "mp3",
            [".flac"] = "flac",
            [".m4a"] = "aac",
            [".aac"] = "aac",
            [".ogg"] = "vorbis",
            [".oga"] = "vorbis",
            [".opus"] = "opus",
            [".wav"] = "pcm",
        };

        /// <summary>Ingested but flagged RequiresTranscode until the ffmpeg lane exists (§Phase 7).</summary>
        private static readonly Dictionary<string, string> TranscodeCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            [".wma"] = "wma",
            [".ape"] = "ape",
            [".wv"] = "wavpack",
            [".mpc"] = "musepack",
            [".aiff"] = "pcm",
            [".aif"] = "pcm",
        };

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public MusicIngestCommand(MovieTheaterConfiguration config) : base(config)
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

            // Chunking is per ARTIST FOLDER: enumerating the root's one level is cheap; the bounded
            // work (walking a subtree + reading tags over the network share) is sliced by --limit.
            var allArtistDirs = Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            var pending = allArtistDirs.Where(n => string.CompareOrdinal(n, After) > 0).ToList();
            var batch = pending.Take(Math.Max(1, Limit)).ToList();

            // Artist + album catalogs are small; load whole. Tracks are loaded per artist below.
            var artistsByFolder = (await db.MusicArtists.ToListAsync())
                .ToDictionary(a => a.FolderName, a => a, StringComparer.OrdinalIgnoreCase);
            var albumsByPath = (await db.MusicAlbums.ToListAsync())
                .ToDictionary(a => a.FolderPath, a => a, StringComparer.OrdinalIgnoreCase);

            int inserted = 0, updated = 0, skipped = 0, tagErrors = 0, flaggedMissing = 0;

            foreach (var artistFolder in batch)
            {
                var counts = await IngestArtistAsync(db, root, artistFolder, artistsByFolder, albumsByPath, w);
                inserted += counts.inserted; updated += counts.updated; skipped += counts.skipped;
                tagErrors += counts.tagErrors; flaggedMissing += counts.flaggedMissing;

                // Save per artist folder: bounded transactions, and an interrupted run resumes at the
                // artist granularity the cursor already works in.
                if (Apply) await db.SaveChangesAsync();
            }

            var remaining = pending.Count - batch.Count;
            var nextCursor = batch.Count > 0 ? batch[^1] : After;

            w.WriteLine();
            w.WriteLine($"{allArtistDirs.Count} artist folder(s) total; this run: {inserted} inserted, {updated} updated, " +
                        $"{skipped} unchanged, {tagErrors} tag-read error(s)" +
                        (Reconcile ? $", {flaggedMissing} flagged missing" : "") + ".");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: \"{nextCursor}\" }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after \"{nextCursor}\".");
        }

        private async Task<(int inserted, int updated, int skipped, int tagErrors, int flaggedMissing)> IngestArtistAsync(
            MovieDb db, string root, string artistFolder,
            Dictionary<string, MusicArtist> artistsByFolder, Dictionary<string, MusicAlbum> albumsByPath,
            ConsoleWriter w)
        {
            int inserted = 0, updated = 0, skipped = 0, tagErrors = 0, flaggedMissing = 0;

            var parsed = MusicNaming.ParseArtistFolder(artistFolder);
            if (!artistsByFolder.TryGetValue(artistFolder, out var artist))
            {
                artist = new MusicArtist
                {
                    Name = parsed.Display,
                    SortName = parsed.Sort,
                    FolderName = artistFolder,
                    YearRange = parsed.YearRange,
                };
                if (Apply) db.MusicArtists.Add(artist);
                artistsByFolder[artistFolder] = artist;
                w.WriteLine($"+ artist  {parsed.Display}" + (parsed.YearRange != null ? $"  ({parsed.YearRange})" : ""));
            }
            else if (artist.YearRange != parsed.YearRange)
            {
                // The year range is curation that lives in the folder name; keep it current.
                if (Apply) artist.YearRange = parsed.YearRange;
            }

            // This artist's existing tracks, keyed by relative path — one query, no per-file lookups.
            var prefix = artistFolder + "/";
            var existingTracks = await db.MusicTracks
                .Where(t => t.RelativePath.StartsWith(prefix))
                .ToListAsync();
            var tracksByPath = existingTracks.ToDictionary(t => t.RelativePath, t => t, StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var artistDir = Path.Combine(root, artistFolder);
            foreach (var path in Directory.EnumerateFiles(artistDir, "*", SearchOption.AllDirectories)
                                          .OrderBy(p => p, StringComparer.Ordinal))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                bool native = NativeCodecs.TryGetValue(ext, out var codec);
                if (!native && !TranscodeCodecs.TryGetValue(ext, out codec)) continue;

                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                seenPaths.Add(rel);
                var fi = new FileInfo(path);

                if (tracksByPath.TryGetValue(rel, out var existing))
                {
                    // Unchanged file: skip without re-opening it — this is what keeps re-runs fast and
                    // the job resumable over the network share.
                    bool changed = existing.SizeBytes != fi.Length ||
                                   existing.ModifiedUtc != fi.LastWriteTimeUtc;
                    if (!changed)
                    {
                        if (existing.MissingSinceUtc != null)
                        {
                            if (Apply) existing.MissingSinceUtc = null;
                            updated++;
                        }
                        else skipped++;
                        continue;
                    }

                    // Re-ripped/retagged file: refresh the machine fields; Title/TrackNo are
                    // hand-editable and only refreshed from the new tags when they were tag-sourced
                    // anyway (heuristic: leave them alone).
                    var tags2 = ReadTags(path, ref tagErrors);
                    if (Apply)
                    {
                        existing.SizeBytes = fi.Length;
                        existing.ModifiedUtc = fi.LastWriteTimeUtc;
                        existing.DurationSec = tags2.DurationSec;
                        existing.BitrateKbps = tags2.BitrateKbps;
                        existing.SampleRateHz = tags2.SampleRateHz;
                        existing.HasEmbeddedArt = tags2.HasEmbeddedArt;
                        existing.MissingSinceUtc = null;
                    }
                    updated++;
                    continue;
                }

                // New track.
                var fileName = Path.GetFileName(path);
                var (fallbackNo, fallbackTitle) = MusicNaming.ParseTrackFileName(Path.GetFileNameWithoutExtension(fileName));
                var tags = ReadTags(path, ref tagErrors);

                var album = ResolveAlbum(db, albumsByPath, artist, artistFolder, parsed.Sort, rel, w);

                var track = new MusicTrack
                {
                    Artist = artist,
                    Album = album,
                    RelativePath = rel,
                    FileName = fileName,
                    Extension = ext,
                    SizeBytes = fi.Length,
                    ModifiedUtc = fi.LastWriteTimeUtc,
                    Title = !string.IsNullOrWhiteSpace(tags.Title) ? tags.Title!.Trim() : fallbackTitle,
                    TrackNo = tags.TrackNo ?? fallbackNo,
                    DiscNo = tags.DiscNo,
                    DurationSec = tags.DurationSec,
                    Codec = codec,
                    BitrateKbps = tags.BitrateKbps,
                    SampleRateHz = tags.SampleRateHz,
                    TagArtist = Truncate(tags.Artist, 400),
                    TagAlbum = Truncate(tags.Album, 400),
                    HasEmbeddedArt = tags.HasEmbeddedArt,
                    RequiresTranscode = !native,
                };
                if (Apply)
                {
                    db.MusicTracks.Add(track);
                    AttachLyrics(db, track, tags, path);
                }
                inserted++;
            }

            if (Reconcile)
            {
                foreach (var t in existingTracks)
                {
                    if (seenPaths.Contains(t.RelativePath) || t.MissingSinceUtc != null) continue;
                    if (Apply) t.MissingSinceUtc = DateTime.UtcNow;
                    flaggedMissing++;
                    w.WriteLine($"  - missing: {t.RelativePath}");
                }
            }

            if (inserted > 0 || updated > 0 || flaggedMissing > 0)
                w.WriteLine($"  [{artistFolder}] +{inserted} ~{updated} ={skipped}" +
                            (flaggedMissing > 0 ? $" missing:{flaggedMissing}" : ""));
            return (inserted, updated, skipped, tagErrors, flaggedMissing);
        }

        /// <summary>Album = first path segment under the artist folder; a file directly in the artist
        /// folder is a loose track (null album). Deeper nesting (CD1/CD2) still belongs to the
        /// first-level album folder (§2.2).</summary>
        private MusicAlbum? ResolveAlbum(MovieDb db, Dictionary<string, MusicAlbum> albumsByPath,
            MusicArtist artist, string artistFolder, string artistBase, string relPath, ConsoleWriter w)
        {
            var segments = relPath.Split('/');
            if (segments.Length <= 2) return null; // artistFolder/file.mp3 — loose track

            var albumPath = segments[0] + "/" + segments[1];
            if (albumsByPath.TryGetValue(albumPath, out var album)) return album;

            var parsed = MusicNaming.ParseAlbumFolder(segments[1], artistBase);
            album = new MusicAlbum
            {
                Artist = artist,
                Title = parsed.Title,
                Year = parsed.Year,
                FolderPath = albumPath,
                Tag = parsed.Tag,
            };
            if (Apply) db.MusicAlbums.Add(album);
            albumsByPath[albumPath] = album;
            w.WriteLine($"  + album  {parsed.Title}" + (parsed.Year != null ? $" ({parsed.Year})" : ""));
            return album;
        }

        private sealed record TagData(
            string? Title, string? Artist, string? Album, int? TrackNo, int? DiscNo,
            double? DurationSec, int? BitrateKbps, int? SampleRateHz, bool HasEmbeddedArt,
            string? UnsyncedLyrics, string? SyncedLrc);

        /// <summary>Reads a file's tags via ATL. A corrupt/unreadable file is counted, not fatal —
        /// the track still ingests from its filename.</summary>
        private static TagData ReadTags(string path, ref int tagErrors)
        {
            try
            {
                var t = new ATL.Track(path);
                string? unsynced = null, lrc = null;
                foreach (var ly in t.Lyrics)
                {
                    if (unsynced == null && !string.IsNullOrWhiteSpace(ly.UnsynchronizedLyrics))
                        unsynced = ly.UnsynchronizedLyrics;
                    if (lrc == null && ly.SynchronizedLyrics != null && ly.SynchronizedLyrics.Count > 0)
                        lrc = string.Join("\n", ly.SynchronizedLyrics.Select(p =>
                            $"[{TimeSpan.FromMilliseconds(p.TimestampStart):mm\\:ss\\.ff}]{p.Text}"));
                }
                return new TagData(
                    Title: t.Title,
                    Artist: t.Artist,
                    Album: t.Album,
                    TrackNo: t.TrackNumber > 0 ? t.TrackNumber : null,
                    DiscNo: t.DiscNumber > 0 ? t.DiscNumber : null,
                    DurationSec: t.DurationMs > 0 ? t.DurationMs / 1000.0 : (double?)null,
                    BitrateKbps: t.Bitrate > 0 ? (int?)t.Bitrate : null,
                    SampleRateHz: t.SampleRate > 0 ? (int?)Math.Round((double)t.SampleRate) : null,
                    HasEmbeddedArt: t.EmbeddedPictures != null && t.EmbeddedPictures.Count > 0,
                    UnsyncedLyrics: unsynced,
                    SyncedLrc: lrc);
            }
            catch
            {
                tagErrors++;
                return new TagData(null, null, null, null, null, null, null, null, false, null, null);
            }
        }

        /// <summary>Captures lyrics while the file is at hand (§2.7): embedded tag first, else a
        /// sidecar .lrc sharing the track's basename. LRCLIB enrichment is a later, separate CLI.</summary>
        private static void AttachLyrics(MovieDb db, MusicTrack track, TagData tags, string filePath)
        {
            if (tags.UnsyncedLyrics != null || tags.SyncedLrc != null)
            {
                db.MusicTrackLyrics.Add(new MusicTrackLyrics
                {
                    Track = track,
                    PlainText = tags.UnsyncedLyrics,
                    SyncedLrc = tags.SyncedLrc,
                    Source = "embedded",
                    FetchedUtc = DateTime.UtcNow,
                });
                return;
            }

            var lrcPath = Path.ChangeExtension(filePath, ".lrc");
            if (!File.Exists(lrcPath)) return;
            try
            {
                var text = File.ReadAllText(lrcPath);
                if (string.IsNullOrWhiteSpace(text)) return;
                db.MusicTrackLyrics.Add(new MusicTrackLyrics
                {
                    Track = track,
                    SyncedLrc = text,
                    Source = "sidecar",
                    FetchedUtc = DateTime.UtcNow,
                });
            }
            catch
            {
                // A broken sidecar never blocks the track itself.
            }
        }

        private static string? Truncate(string? s, int max) =>
            string.IsNullOrEmpty(s) ? null : (s!.Length <= max ? s : s.Substring(0, max));
    }
}
