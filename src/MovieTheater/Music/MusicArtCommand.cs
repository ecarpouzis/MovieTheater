using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
    /// Fills in album art (music-plan.md §2.5). Two passes, deliberately separate:
    ///
    /// <para><b>Local (default)</b> — free and offline: prefer a folder image in the album folder
    /// (cover/folder/front, else the largest image there), else the embedded picture on the album's
    /// first track that has one. Writes <c>music_{albumId}.png</c> + <c>_s.png</c> to
    /// <c>MusicImagesDir ?? MoviePostersDir</c>, computes the dominant color from the thumbnail, and
    /// flips <c>MusicAlbum.HasArt</c>.</para>
    ///
    /// <para><b>Remote (<c>--remote</c>)</b> — only albums still artless after the local pass:
    /// MusicBrainz release search → Cover Art Archive front image, falling back to the iTunes Search
    /// API. Rate-limited to one MusicBrainz call per second (their published limit) with the
    /// User-Agent they require. Every attempt stamps <c>ArtCheckedUtc</c>, hit or miss, so a re-run
    /// skips albums the internet has already declined — the negative cache.</para>
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run by default; <c>--apply</c> writes. Bounded by
    /// <c>--limit</c> ALBUMS per run and resumable via <c>--after &lt;albumId&gt;</c>; prints
    /// <c>{ processed, remaining, nextCursor }</c> so the caller can drive it to completion.
    /// Idempotent, and <b>never overwrites an existing art file</b> (project rule: art on the mount
    /// is not regenerated) — an album whose file is already there is just re-flagged HasArt.</para>
    /// </summary>
    [Command("music-art", Description = "Extract/fetch album art into the images mount (dry-run unless --apply).")]
    public class MusicArtCommand : BasicDICommand, ICommand
    {
        [CommandOption("root", 'r', Description = "Music library root. Default: MusicLibraryDir from config.")]
        public string? Root { get; set; }

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max ALBUMS to process this run (default 100).")]
        public int Limit { get; set; } = 100;

        [CommandOption("after", Description = "Resume cursor: skip albums whose id is ≤ this (from a prior run's nextCursor).")]
        public int After { get; set; }

        [CommandOption("remote", Description = "Second pass: fetch art for still-artless albums from Cover Art Archive / iTunes.")]
        public bool Remote { get; set; }

        [CommandOption("verbose", Description = "Print a line per album, not just the ones that changed.")]
        public bool Verbose { get; set; }

        /// <summary>See <see cref="MusicRemoteArt.MusicBrainzThrottleMs"/> — the remote lookup itself
        /// now lives there so this command and the admin backfill endpoint can't drift apart.</summary>
        private const int MusicBrainzThrottleMs = MusicRemoteArt.MusicBrainzThrottleMs;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public MusicArtCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            var imagesDir = MusicArtStore.ResolveDir(config);
            if (imagesDir == null)
            {
                w.WriteLine("No images directory: set MusicImagesDir (or MoviePostersDir) in config.");
                return;
            }
            var rootSetting = !string.IsNullOrWhiteSpace(Root) ? Root : config.MusicLibraryDir;
            var root = string.IsNullOrWhiteSpace(rootSetting) ? null : Path.GetFullPath(rootSetting);
            if (!Remote && (root == null || !Directory.Exists(root)))
            {
                w.WriteLine($"Music root not found: {root ?? "(unset — pass --root or set MusicLibraryDir)"}");
                return;
            }
            if (Apply) Directory.CreateDirectory(imagesDir);

            await using var db = await dbFactory.CreateDbContextAsync();

            // The work set: albums that still have no art. Remote mode additionally skips anything the
            // negative cache already answered for.
            var pendingQuery = db.MusicAlbums.Where(a => !a.HasArt);
            if (Remote) pendingQuery = pendingQuery.Where(a => a.ArtCheckedUtc == null);

            // "remaining" counts what's left AFTER this run's cursor, so a driver loop reaches 0 and stops
            // (artless albums BELOW the cursor were already offered art and declined it — counting them
            // would make the number never reach zero).
            var totalPending = await pendingQuery.Where(a => a.Id > After).CountAsync();
            var batch = await pendingQuery
                .Where(a => a.Id > After)
                .OrderBy(a => a.Id)
                .Include(a => a.Artist)
                .Take(Math.Max(1, Limit))
                .ToListAsync();

            int fromFolder = 0, fromEmbedded = 0, fromRemote = 0, alreadyOnDisk = 0, noArt = 0, failed = 0;
            using var http = Remote ? MusicRemoteArt.CreateHttp() : null;
            DateTime lastMusicBrainz = DateTime.MinValue;

            foreach (var album in batch)
            {
                var mainPath = Path.Combine(imagesDir, MusicArtStore.FileName(album.Id, thumbnail: false));

                // Never re-generate art that already exists on the mount — just make the DB agree.
                if (File.Exists(mainPath))
                {
                    if (Apply)
                    {
                        album.HasArt = true;
                        if (album.DominantColor == null)
                        {
                            var thumbPath = Path.Combine(imagesDir, MusicArtStore.FileName(album.Id, thumbnail: true));
                            if (File.Exists(thumbPath))
                                album.DominantColor = MusicArtStore.ComputeAverageColor(await File.ReadAllBytesAsync(thumbPath));
                        }
                    }
                    alreadyOnDisk++;
                    if (Verbose) w.WriteLine($"  = {album.Id} {album.Title} — art already on disk");
                    continue;
                }

                byte[]? source = null;
                string origin = "";

                if (!Remote)
                {
                    var albumDir = Path.Combine(root!, album.FolderPath.Replace('/', Path.DirectorySeparatorChar));
                    var folderImage = MusicArtStore.FindFolderImage(albumDir);
                    if (folderImage != null)
                    {
                        try { source = await File.ReadAllBytesAsync(folderImage); origin = "folder"; }
                        catch { source = null; }
                    }

                    if (source == null)
                    {
                        source = await FirstEmbeddedPictureAsync(db, root!, album.Id);
                        if (source != null) origin = "embedded";
                    }
                }
                else
                {
                    // Throttle applies to the MusicBrainz search regardless of outcome. The same spacer
                    // goes in, so the second search the lookup may run stays inside the 1 req/s limit.
                    async Task SpaceAsync()
                    {
                        var wait = MusicBrainzThrottleMs - (int)(DateTime.UtcNow - lastMusicBrainz).TotalMilliseconds;
                        if (wait > 0) await Task.Delay(wait);
                        lastMusicBrainz = DateTime.UtcNow;
                    }
                    await SpaceAsync();

                    source = await MusicRemoteArt.FetchAsync(http!, album.Artist.Name, album.Title, SpaceAsync);
                    if (source != null) origin = "remote";
                    if (Apply) album.ArtCheckedUtc = DateTime.UtcNow;
                }

                if (source == null)
                {
                    noArt++;
                    if (Verbose) w.WriteLine($"  · {album.Id} {album.Artist.Name} — {album.Title}: no art found");
                    continue;
                }

                var main = MusicArtStore.Downscale(source, MusicArtStore.MainMaxPx);
                var thumb = MusicArtStore.Downscale(source, MusicArtStore.ThumbMaxPx);
                if (main == null || thumb == null)
                {
                    failed++;
                    w.WriteLine($"  ! {album.Id} {album.Title}: art found but could not be decoded ({origin})");
                    continue;
                }

                if (Apply)
                {
                    await File.WriteAllBytesAsync(mainPath, main);
                    await File.WriteAllBytesAsync(Path.Combine(imagesDir, MusicArtStore.FileName(album.Id, thumbnail: true)), thumb);
                    album.HasArt = true;
                    album.DominantColor = MusicArtStore.ComputeAverageColor(thumb);
                }

                if (origin == "folder") fromFolder++;
                else if (origin == "embedded") fromEmbedded++;
                else fromRemote++;
                if (Verbose) w.WriteLine($"  + {album.Id} {album.Artist.Name} — {album.Title} ({origin})");
            }

            if (Apply) await db.SaveChangesAsync();

            var remaining = Math.Max(0, totalPending - batch.Count);
            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            var withArt = await db.MusicAlbums.CountAsync(a => a.HasArt);
            var totalAlbums = await db.MusicAlbums.CountAsync();

            w.WriteLine();
            w.WriteLine($"{(Remote ? "remote" : "local")} pass: {fromFolder} from folder image, {fromEmbedded} from embedded tag, " +
                        $"{fromRemote} from remote, {alreadyOnDisk} already on disk, {noArt} no art, {failed} undecodable.");
            w.WriteLine($"coverage: {withArt}/{totalAlbums} albums have art ({(totalAlbums == 0 ? 0 : 100.0 * withArt / totalAlbums):F1}%).");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: {nextCursor} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
        }

        /// <summary>Reads the embedded picture off the first track of the album that claims to have one
        /// (HasEmbeddedArt was recorded at ingest, so this opens at most one file per album).</summary>
        private static async Task<byte[]?> FirstEmbeddedPictureAsync(MovieDb db, string root, int albumId)
        {
            var rel = await db.MusicTracks.AsNoTracking()
                .Where(t => t.AlbumId == albumId && t.HasEmbeddedArt && t.MissingSinceUtc == null)
                .OrderBy(t => t.DiscNo).ThenBy(t => t.TrackNo).ThenBy(t => t.Id)
                .Select(t => t.RelativePath)
                .FirstOrDefaultAsync();
            if (rel == null) return null;

            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return null;
            try
            {
                var track = new ATL.Track(full);
                var picture = track.EmbeddedPictures?.FirstOrDefault();
                return picture?.PictureData;
            }
            catch
            {
                return null;
            }
        }

    }
}
