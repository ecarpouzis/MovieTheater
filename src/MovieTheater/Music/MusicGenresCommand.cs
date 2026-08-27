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
    /// Fills <c>MusicTrack.Genre</c> from the files' own genre frames and rolls the result up into
    /// <c>MusicAlbumGenre</c> / <c>MusicArtistGenre</c> with <c>Source = "tags"</c> (R9 S10, the
    /// cheapest of the three metadata legs — the answer is already on the disk).
    ///
    /// <para>This cannot ride along on <c>music-ingest</c> for the same reason
    /// <c>music-backfill-channels</c> could not: ingest deliberately skips a file whose size and
    /// mtime are unchanged without re-opening it, which is every one of the 42k already-ingested
    /// tracks. (Ingest DOES read the genre now, so nothing ingested from here on joins this backlog.)</para>
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run-first: prints what it found and writes nothing unless
    /// <c>--apply</c>. Bounded: at most <c>--take</c> TRACKS per run. Resumable and idempotent: the
    /// work queue IS "GenreCheckedUtc IS NULL" ordered by Id, so an --apply run shrinks it and a
    /// plain re-run continues where it stopped; the roll-up recomputes an album from ALL of its
    /// tracks and REPLACES only its own <c>Source='tags'</c> rows, so an album whose tracks straddle
    /// two chunks ends up correct either way and a re-run cannot double-count. Terminates
    /// deterministically: a file that is missing, gone from disk, or unreadable is stamped as checked
    /// with a NULL genre rather than left for every future run to retry. Never destructive: it writes
    /// two new columns and two new tables and touches nothing else, and it NEVER writes under the
    /// music root — every file is opened read-only.</para>
    /// </summary>
    [Command("music-genres", Description = "Read genre tags into MusicTrack.Genre and roll them up per album/artist (dry-run unless --apply).")]
    public class MusicGenresCommand : BasicDICommand, ICommand
    {
        [CommandOption("root", 'r', Description = "Music library root. Default: MusicLibraryDir from config.")]
        public string? Root { get; set; }

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("dry-run", Description = "Force a dry run. Redundant (dry is the default) but accepted, so the safe spelling is never a typo.")]
        public bool DryRun { get; set; }

        [CommandOption("take", Description = "Max TRACKS to read this run (default 200).")]
        public int Take { get; set; } = 200;

        [CommandOption("after", Description = "Resume cursor: skip tracks whose Id is ≤ this. Only needed to page a dry run (an --apply run shrinks its own queue).")]
        public int After { get; set; }

        [CommandOption("verbose", Description = "Print a line per track read, not just the summary.")]
        public bool Verbose { get; set; }

        [CommandOption("rollup-only", Description = "Skip the file pass; only recompute album/artist roll-ups from the genres already in the database.")]
        public bool RollupOnly { get; set; }

        [CommandOption("rollup-after", Description = "With --rollup-only: resume cursor over ALBUM ids.")]
        public int RollupAfter { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public MusicGenresCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (DryRun) Apply = false;
            await using var db = await dbFactory.CreateDbContextAsync();

            if (RollupOnly)
            {
                await RollupOnlyPassAsync(db, w);
                return;
            }

            var rootSetting = !string.IsNullOrWhiteSpace(Root) ? Root : config.MusicLibraryDir;
            if (string.IsNullOrWhiteSpace(rootSetting))
            {
                w.WriteLine("No music root: pass --root or set MusicLibraryDir in config.");
                return;
            }
            var root = Path.GetFullPath(rootSetting);
            if (!Directory.Exists(root)) { w.WriteLine($"Music root not found: {root}"); return; }

            var pendingTotal = await db.MusicTracks.CountAsync(t => t.GenreCheckedUtc == null && t.Id > After);
            var batch = await db.MusicTracks
                .Where(t => t.GenreCheckedUtc == null && t.Id > After)
                .OrderBy(t => t.Id)
                .Take(Math.Max(1, Take))
                .ToListAsync();

            int tagged = 0, untagged = 0, absent = 0, unreadable = 0;
            var histogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var track in batch)
            {
                string? genre = null;
                if (track.MissingSinceUtc != null) { absent++; }
                else
                {
                    var path = Path.Combine(root, track.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path)) absent++;
                    else
                    {
                        try
                        {
                            // Read-only: ATL opens the file for reading and this command never calls
                            // Save(). Nothing under the music root is ever written.
                            genre = MusicIngestCommand.JoinGenres(new ATL.Track(path).Genre);
                            if (genre == null) untagged++; else tagged++;
                        }
                        catch
                        {
                            // A corrupt or locked file is counted, not fatal — the same posture as
                            // ingest and the channels backfill. It still leaves the queue.
                            unreadable++;
                        }
                    }
                }

                if (genre != null)
                    foreach (var g in MusicGenres.Split(genre))
                        histogram[g] = histogram.GetValueOrDefault(g) + 1;

                if (Verbose) w.WriteLine($"  {(genre == null ? "·" : "+")} {track.Id} {track.RelativePath} → {genre ?? "(none)"}");

                if (Apply)
                {
                    track.Genre = genre;
                    // Stamped even on a miss — the negative cache. This is the queue's stop condition.
                    track.GenreCheckedUtc = DateTime.UtcNow;
                }
            }

            if (Apply) await db.SaveChangesAsync();

            // Roll up every album this chunk touched, from ALL of its tracks (not just this chunk's):
            // an album whose tracks straddle a chunk boundary is recomputed correctly by whichever
            // chunk sees it last, and a recompute is a replace, so it can happen any number of times.
            var albumIds = batch.Select(t => t.AlbumId).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
            var artistIds = batch.Select(t => t.ArtistId).Distinct().ToList();
            var (albumRows, artistRows) = await RollUpAsync(db, albumIds, artistIds, w);

            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            // --apply removes the processed rows from the queue, so `remaining` is a real countdown.
            // A dry run writes nothing and so cannot shrink anything: it reports the queue it did NOT
            // shrink, and is paged with --after.
            var remaining = Apply ? Math.Max(0, pendingTotal - batch.Count) : pendingTotal;
            var errorRate = batch.Count == 0 ? 0 : 100.0 * unreadable / batch.Count;

            w.WriteLine();
            w.WriteLine($"read {batch.Count} track(s): {tagged} tagged, {untagged} no genre tag, " +
                        $"{absent} no file on disk, {unreadable} unreadable ({errorRate:F1}% error)" +
                        (Apply ? "." : " — DRY RUN, nothing written."));
            foreach (var (g, n) in histogram.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(12))
                w.WriteLine($"  {g}: {n}");
            w.WriteLine($"rolled up {albumRows} album genre row(s) over {albumIds.Count} album(s), " +
                        $"{artistRows} artist genre row(s) over {artistIds.Count} artist(s).");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: {nextCursor}, " +
                        $"counts: {{ tagged: {tagged}, untagged: {untagged}, absent: {absent}, unreadable: {unreadable}, " +
                        $"albumGenres: {albumRows}, artistGenres: {artistRows} }} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run (optionally --after {nextCursor}).");
        }

        /// <summary>
        /// Recomputes roll-ups from the genres already in the database, without opening a single file
        /// — for after a threshold change, or to finish a library whose track pass ran before this
        /// command grew its roll-up. Bounded and resumable over ALBUM ids.
        /// </summary>
        private async Task RollupOnlyPassAsync(MovieDb db, ConsoleWriter w)
        {
            var pendingTotal = await db.MusicAlbums.CountAsync(a => a.Id > RollupAfter);
            var albumIds = await db.MusicAlbums
                .Where(a => a.Id > RollupAfter)
                .OrderBy(a => a.Id)
                .Select(a => a.Id)
                .Take(Math.Max(1, Take))
                .ToListAsync();
            var artistIds = await db.MusicAlbums
                .Where(a => albumIds.Contains(a.Id))
                .Select(a => a.ArtistId)
                .Distinct()
                .ToListAsync();

            var (albumRows, artistRows) = await RollUpAsync(db, albumIds, artistIds, w);
            var nextCursor = albumIds.Count > 0 ? albumIds[^1] : RollupAfter;
            var remaining = Math.Max(0, pendingTotal - albumIds.Count);

            w.WriteLine();
            w.WriteLine($"rolled up {albumRows} album genre row(s) over {albumIds.Count} album(s), " +
                        $"{artistRows} artist genre row(s) over {artistIds.Count} artist(s)" +
                        (Apply ? "." : " — DRY RUN, nothing written."));
            w.WriteLine($"{{ processed: {albumIds.Count}, remaining: {remaining}, nextCursor: {nextCursor}, " +
                        $"counts: {{ albumGenres: {albumRows}, artistGenres: {artistRows} }} }}");
        }

        /// <summary>
        /// Recomputes the <c>Source='tags'</c> genre rows for the given albums and artists.
        /// </summary>
        /// <remarks>
        /// REPLACE, not merge: the album's genres are a function of its tracks, so the only way a
        /// re-run can be idempotent is to derive the whole set and swap it. The delete is fenced to
        /// this pass's own <c>Source</c> and to the ids being recomputed — the external legs' rows for
        /// the same album are never in scope, which is the whole reason Source is part of the key.
        /// </remarks>
        private async Task<(int albumRows, int artistRows)> RollUpAsync(MovieDb db, List<int> albumIds, List<int> artistIds, ConsoleWriter w)
        {
            const string source = MusicGenreSources.Tags;
            if (albumIds.Count == 0 && artistIds.Count == 0) return (0, 0);

            // ── Albums ────────────────────────────────────────────────────────────────────────────
            var trackGenres = await db.MusicTracks
                .Where(t => t.AlbumId != null && albumIds.Contains(t.AlbumId.Value) && t.Genre != null)
                .Select(t => new { AlbumId = t.AlbumId!.Value, t.Genre })
                .ToListAsync();
            var byAlbum = trackGenres.GroupBy(t => t.AlbumId).ToDictionary(g => g.Key, g => g.Select(x => x.Genre).ToList());

            var existingAlbumRows = await db.MusicAlbumGenres
                .Where(g => g.Source == source && albumIds.Contains(g.AlbumId))
                .ToListAsync();
            var wantedAlbum = new List<MusicAlbumGenre>();
            foreach (var albumId in albumIds)
            {
                if (!byAlbum.TryGetValue(albumId, out var genres)) continue;
                foreach (var (genre, count) in MusicGenres.RollUpAlbum(genres))
                    wantedAlbum.Add(new MusicAlbumGenre { AlbumId = albumId, Genre = genre, Source = source, Weight = count, CreatedUtc = DateTime.UtcNow });
            }

            if (Apply)
            {
                db.MusicAlbumGenres.RemoveRange(existingAlbumRows);
                db.MusicAlbumGenres.AddRange(wantedAlbum);
                await db.SaveChangesAsync();
            }

            // ── Artists ───────────────────────────────────────────────────────────────────────────
            // The album roll-up IS the artist roll-up's input, and it is read back from the database
            // rather than reused from `wantedAlbum`: an artist's other albums (untouched by this
            // chunk) count too, and a dry run must reason over what is actually there.
            var albumOwner = await db.MusicAlbums
                .Where(a => artistIds.Contains(a.ArtistId))
                .Select(a => new { a.Id, a.ArtistId })
                .ToListAsync();
            var ownerOf = albumOwner.ToDictionary(a => a.Id, a => a.ArtistId);
            var ownedAlbumIds = albumOwner.Select(a => a.Id).ToList();
            var albumGenreRows = await db.MusicAlbumGenres
                .Where(g => g.Source == source && ownedAlbumIds.Contains(g.AlbumId))
                .Select(g => new { g.AlbumId, g.Genre })
                .ToListAsync();

            var existingArtistRows = await db.MusicArtistGenres
                .Where(g => g.Source == source && artistIds.Contains(g.ArtistId))
                .ToListAsync();
            var wantedArtist = new List<MusicArtistGenre>();
            foreach (var group in albumGenreRows.GroupBy(r => ownerOf[r.AlbumId]))
            {
                var perAlbum = group.GroupBy(r => r.AlbumId).Select(g => g.Select(x => x.Genre));
                foreach (var (genre, count) in MusicGenres.RollUpArtist(perAlbum))
                    wantedArtist.Add(new MusicArtistGenre { ArtistId = group.Key, Genre = genre, Source = source, Weight = count, CreatedUtc = DateTime.UtcNow });
            }

            if (Apply)
            {
                db.MusicArtistGenres.RemoveRange(existingArtistRows);
                db.MusicArtistGenres.AddRange(wantedArtist);
                await db.SaveChangesAsync();
            }

            return (wantedAlbum.Count, wantedArtist.Count);
        }
    }
}
