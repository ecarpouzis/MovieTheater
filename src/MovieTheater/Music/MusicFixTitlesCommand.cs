using System;
using System.Collections.Generic;
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
    /// Repairs TRUNCATED track titles in the database (2026-08-31) — the file is
    /// <c>07_10,000 Maniacs - What's The Matter Here.mp3</c> and its ID3 title frame says
    /// <c>What's The Matte</c>, so that is what ingest stored and that is what the site shows.
    /// </summary>
    /// <remarks>
    /// <para><b>DATABASE ONLY. This command never opens, writes or renames a file.</b> The standing
    /// project rule is that nothing automated modifies the media library, and repair is done in the
    /// rows instead. That is not a workaround here, it is the durable fix: ingest writes
    /// <c>Title</c>/<c>TrackNo</c> only on INSERT and refreshes nothing else, so a corrected title
    /// survives every future ingest of the same file.</para>
    ///
    /// <para><b>Two tests, and only the strict one may write.</b> The detector — "the filename still
    /// contains the stored title and carries more after it" — flags 83 rows; 59 carried proof and 24
    /// were left alone. A detector is never an action threshold, and here the proof is THREE things
    /// agreeing: the filename on disk, a cut at a width decided in advance
    /// (<see cref="MusicTitleRepair"/>), and the artist's Last.fm catalogue — read from the response
    /// cache the popularity pass already filled, so this command makes no network requests at all.
    /// Everything flagged that the proof does not carry is COUNTED AND LEFT ALONE.</para>
    ///
    /// <para><b>The catalogue alone was not enough, and that is worth knowing.</b> The first pass
    /// proposed 167 rewrites and reading them by eye found perhaps a quarter wrong — complete titles
    /// whose FILES append a composer or a year. Last.fm confirmed those too, because the same
    /// badly-named files are what people scrobbled: the "two independent sources" were not
    /// independent. The width gate, which is about the MECHANISM of truncation rather than about the
    /// title, is what actually separated them.</para>
    ///
    /// <para><b>The false positive that shaped the rules.</b> A file named
    /// <c>01 - Song Title (Live).mp3</c> whose tag correctly reads <c>Song Title</c> also matches the
    /// detector, and "fixing" it would move an edition marker into the song's name. So a candidate is
    /// refused when it differs from the stored title only by <see cref="MusicTrackTitles"/> noise —
    /// there has to be real title to recover, not an annotation. The title is also refused when it
    /// appears twice in the filename, because then which occurrence starts the song is a guess.</para>
    ///
    /// <para><b>Bulk-job rules.</b> Dry run unless <c>--apply</c>; bounded by <c>--take</c>; resumable
    /// by <c>--after</c>; idempotent because a repaired row stops matching the detector (the filename
    /// now ENDS with the title), so the work set shrinks monotonically and terminates.</para>
    /// </remarks>
    [Command("music-fix-titles", Description = "Repair truncated MusicTrack.Title values from the filename, proven against the cached Last.fm catalogue (dry-run unless --apply). Never touches a file.")]
    public class MusicFixTitlesCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("dry-run", Description = "Force a dry run. Redundant (dry is the default) but accepted, so the safe spelling is never a typo.")]
        public bool DryRun { get; set; }

        [CommandOption("take", Description = "Max TRACKS to examine this run (default 2000).")]
        public int Take { get; set; } = 2000;

        [CommandOption("after", Description = "Resume cursor: skip tracks whose Id is ≤ this (from a prior run's nextCursor).")]
        public int After { get; set; }

        [CommandOption("cache-dir", Description = "Raw response cache root. Default: data/music-cache (gitignored).")]
        public string? CacheDir { get; set; }

        [CommandOption("verbose", Description = "Print a line per repair, and per refusal with --show-refusals.")]
        public bool Verbose { get; set; }

        [CommandOption("show-refusals", Description = "With --verbose, also print the rows the proof refused — the list worth reading by eye.")]
        public bool ShowRefusals { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public MusicFixTitlesCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (DryRun) Apply = false;

            await using var db = await dbFactory.CreateDbContextAsync();
            var cache = new MusicResponseCache(CacheDir);

            // The whole work set is small enough to count, and the count is what the operator drives
            // the loop by. The detector cannot be expressed in SQL without repeating its subtleties
            // in a second language, so the batch is read by Id and judged in memory.
            var batch = await db.MusicTracks.AsNoTracking()
                .Where(t => t.Id > After && t.MissingSinceUtc == null)
                .OrderBy(t => t.Id)
                .Take(Math.Max(1, Take))
                .Select(t => new Row(t.Id, t.Title, t.FileName, t.TagArtist, t.Artist.Name))
                .ToListAsync();

            int flagged = 0, repaired = 0, refusedNoCatalogue = 0, refusedNotKnown = 0, refusedEditionOnly = 0, refusedAmbiguous = 0;
            var catalogues = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
            var writes = new List<(int Id, string From, string To)>();

            foreach (var row in batch)
            {
                var candidate = MusicTitleRepair.Recover(row.Title, row.FileName, out var ambiguous);
                if (ambiguous) { flagged++; refusedAmbiguous++; if (Verbose && ShowRefusals) w.WriteLine($"  ? {row.Id} ambiguous (title appears twice in the name): {row.FileName}"); continue; }
                if (candidate == null) continue;
                flagged++;

                // Proof 1 already holds (the filename says so). Proof 2: does the outside world know
                // a song by that full name? An edition-only difference never reaches here.
                if (MusicTrackTitles.Normalize(candidate) == MusicTrackTitles.Normalize(row.Title))
                {
                    refusedEditionOnly++;
                    if (Verbose && ShowRefusals) w.WriteLine($"  · {row.Id} edition marker only, not a truncation: \"{row.Title}\" vs \"{candidate}\"");
                    continue;
                }

                var known = await CatalogueAsync(cache, row, catalogues);
                if (known == null)
                {
                    refusedNoCatalogue++;
                    if (Verbose && ShowRefusals) w.WriteLine($"  ~ {row.Id} no cached catalogue for \"{row.TagArtist ?? row.ArtistName}\": \"{row.Title}\" -> \"{candidate}\"");
                    continue;
                }
                if (!known.Contains(MusicTrackTitles.Normalize(candidate)))
                {
                    refusedNotKnown++;
                    if (Verbose && ShowRefusals) w.WriteLine($"  ! {row.Id} unconfirmed, LEFT ALONE: \"{row.Title}\" -> \"{candidate}\"");
                    continue;
                }

                repaired++;
                writes.Add((row.Id, row.Title, candidate));
                if (Verbose) w.WriteLine($"  + {row.Id} \"{row.Title}\" -> \"{candidate}\"");
            }

            if (Apply && writes.Count > 0)
            {
                var ids = writes.Select(x => x.Id).ToList();
                var rows = await db.MusicTracks.Where(t => ids.Contains(t.Id)).ToListAsync();
                var byId = writes.ToDictionary(x => x.Id, x => x.To);
                foreach (var track in rows)
                    if (byId.TryGetValue(track.Id, out var title)) track.Title = title;
                await db.SaveChangesAsync();
            }

            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            var refused = refusedNoCatalogue + refusedNotKnown + refusedEditionOnly + refusedAmbiguous;

            w.WriteLine();
            w.WriteLine($"examined {batch.Count} track(s): {flagged} flagged by the detector, {repaired} PROVEN and " +
                        (Apply ? "repaired" : "repairable") + $", {refused} left alone" +
                        (Apply ? "." : " — DRY RUN, nothing written."));
            w.WriteLine($"  left alone: {refusedNotKnown} unconfirmed by the catalogue, {refusedEditionOnly} edition markers " +
                        $"(not truncations), {refusedNoCatalogue} no cached catalogue, {refusedAmbiguous} ambiguous.");
            w.WriteLine($"{{ processed: {batch.Count}, nextCursor: {nextCursor}, " +
                        $"counts: {{ flagged: {flagged}, repaired: {repaired}, refusedNotKnown: {refusedNotKnown}, " +
                        $"refusedEditionOnly: {refusedEditionOnly}, refusedNoCatalogue: {refusedNoCatalogue}, refusedAmbiguous: {refusedAmbiguous} }} }}");
            if (!Apply) w.WriteLine("DRY RUN — no rows written. Re-run with --apply.");
            w.WriteLine("No file was opened, written or renamed: this command repairs DATABASE rows only.");
        }

        private sealed record Row(int Id, string Title, string FileName, string? TagArtist, string ArtistName);

        /// <summary>
        /// The normalised names Last.fm knows for this row's artist, from the ON-DISK cache only.
        /// Null when nothing was ever cached for them — which is a refusal, not an error.
        /// </summary>
        /// <remarks>
        /// Deliberately offline. This is a repair pass over rows we already have, and a repair that
        /// silently made 7,000 network requests would be a different job than the one its name
        /// promises. The cache is already complete: the popularity drive asked about every artist.
        /// </remarks>
        private static async Task<HashSet<string>?> CatalogueAsync(
            MusicResponseCache cache, Row row, Dictionary<string, HashSet<string>?> memo)
        {
            foreach (var artist in Candidates(row))
            {
                if (memo.TryGetValue(artist, out var known))
                {
                    if (known != null) return known;
                    continue;
                }

                var body = await cache.TryReadAsync("lastfm", $"artist.gettoptracks|{artist}", maxAge: null);
                HashSet<string>? names = null;
                if (body != null)
                {
                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, _) in MusicLastFm.ParseTopTracks(body))
                    {
                        var normalized = MusicTrackTitles.Normalize(name);
                        if (normalized.Length > 0) names.Add(normalized);
                    }
                    if (names.Count == 0) names = null;
                }
                memo[artist] = names;
                if (names != null) return names;
            }
            return null;
        }

        /// <summary>The artist keys the popularity pass cached under, best guess first — the same
        /// order, so this reads exactly what that pass wrote.</summary>
        private static IEnumerable<string> Candidates(Row row)
        {
            var tag = string.IsNullOrWhiteSpace(row.TagArtist) ? null : row.TagArtist!.Trim();
            if (tag != null) yield return tag;
            var folder = (row.ArtistName ?? "").Trim();
            if (folder.Length > 0 && !string.Equals(tag, folder, StringComparison.OrdinalIgnoreCase))
                yield return folder;
        }
    }
}
