using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Jellyfin
{
    /// <summary>
    /// Matches Jellyfin's library against the movies' stored file paths and records the
    /// result in <see cref="MediaFile"/> (docs/streaming-plan.md §6): Jellyfin item id,
    /// real duration, container/codec/size. Re-runnable any time; prints a two-way diff
    /// (DB files Jellyfin doesn't have → MissingSinceUtc; Jellyfin items the DB doesn't
    /// track). IMDB-id fallback candidates are reported for review, never written.
    /// </summary>
    [Command("sync-jellyfin", Description = "Match Jellyfin items to movie file paths and store ids + media details.")]
    public class SyncJellyfinCommand : BasicDICommand, ICommand
    {
        [CommandOption("dry-run", Description = "Match and report without writing to the database.")]
        public bool DryRun { get; set; }

        [CommandOption("samples", Description = "How many examples to print per report section.")]
        public int Samples { get; set; } = 15;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly JellyfinApi jellyfin;
        private readonly ILogger<SyncJellyfinCommand> logger;
        private readonly MovieTheaterConfiguration config;

        public SyncJellyfinCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            jellyfin = GetRequiredService<JellyfinApi>();
            logger = GetRequiredService<ILogger<SyncJellyfinCommand>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var cancel = console.RegisterCancellationHandler();
            var o = console.Output;

            if (config.JellyfinPathMappings.Count == 0)
            {
                console.Error.WriteLine("No JellyfinPathMappings configured — nothing can match. Aborting.");
                return;
            }

            var info = await jellyfin.GetSystemInfoAsync(cancel);
            o.WriteLine($"Jellyfin: {info.ServerName} {info.Version}{(DryRun ? "   (dry-run)" : "")}");

            var items = await jellyfin.GetAllMovieItemsAsync(cancel);
            o.WriteLine($"Jellyfin movie items: {items.Count}");

            using var db = await dbFactory.CreateDbContextAsync(cancel);
            var movies = await db.Movies
                .Where(m => m.FilePath != null && m.FilePath != "")
                .Select(m => new { m.id, m.Title, m.FilePath, m.imdbID, m.PlayableId })
                .ToListAsync(cancel);
            // Files now hang off the movie's Playable (Phase-4 cutover). Dry-run works before any
            // MediaFile rows exist; existing rows only matter when writing.
            var existingFiles = DryRun ? new List<MediaFile>() : await db.MediaFiles.ToListAsync(cancel);
            var filesByPlayable = existingFiles.ToLookup(f => f.PlayableId);
            o.WriteLine($"DB movies with a file path: {movies.Count}" +
                        (DryRun ? "" : $"   existing MediaFile rows: {existingFiles.Count}"));

            // DB path → movie. Duplicate paths (DB duplicate rows) are matched to the first
            // movie and reported rather than guessed at.
            var byPath = new Dictionary<string, (int Id, string Title, string FilePath)>();
            var duplicatePaths = new List<string>();
            foreach (var m in movies)
            {
                var key = JellyfinPathMapper.NormalizeForCompare(m.FilePath!);
                if (!byPath.TryAdd(key, (m.id, m.Title ?? "?", m.FilePath!)))
                    duplicatePaths.Add($"{m.FilePath} (movie {m.id} '{m.Title}' collides with movie {byPath[key].Id} '{byPath[key].Title}')");
            }
            var byImdb = movies.Where(m => !string.IsNullOrEmpty(m.imdbID))
                .GroupBy(m => m.imdbID!).ToDictionary(g => g.Key, g => g.First());
            var movieById = movies.ToDictionary(m => m.id);

            var untranslatable = new List<string>();
            var untracked = new List<string>();
            var imdbFallbackCandidates = new List<(int MovieId, string Line)>();
            var duplicateItems = new List<string>();
            int created = 0, updated = 0;
            var now = DateTime.UtcNow;

            // Pass 1: resolve each Jellyfin item to a movie, keeping one item per movie.
            // Jellyfin can hold duplicate items for one file (e.g. a leftover drive-letter
            // library folder beside the UNC one); the earlier-listed mapping wins.
            var chosen = new Dictionary<int, (JellyfinItem Item, int MappingIndex)>();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Path)) { untracked.Add($"(no path) {item.Name} [{item.Id}]"); continue; }

                if (!JellyfinPathMapper.TryTranslateToDb(item.Path, config.JellyfinPathMappings, out var dbPath, out var mappingIndex))
                {
                    untranslatable.Add(item.Path);
                    continue;
                }

                if (!byPath.TryGetValue(JellyfinPathMapper.NormalizeForCompare(dbPath), out var movie))
                {
                    // Path didn't match; an IMDB-id agreement is reported for review, not trusted.
                    if (item.ImdbId != null && byImdb.TryGetValue(item.ImdbId, out var byId))
                        imdbFallbackCandidates.Add((byId.id, $"{item.Path} ↔ movie {byId.id} '{byId.Title}' (shared {item.ImdbId})"));
                    else
                        untracked.Add(item.Path);
                    continue;
                }

                if (chosen.TryGetValue(movie.Id, out var existing))
                {
                    var loser = mappingIndex < existing.MappingIndex ? existing.Item : item;
                    if (mappingIndex < existing.MappingIndex)
                        chosen[movie.Id] = (item, mappingIndex);
                    duplicateItems.Add($"movie {movie.Id} '{movie.Title}': kept [{chosen[movie.Id].Item.Path}], ignored [{loser.Path}]");
                }
                else
                {
                    chosen[movie.Id] = (item, mappingIndex);
                }
            }

            // Pass 2: write the chosen item per movie.
            if (!DryRun)
            {
                foreach (var (movieId, (item, _)) in chosen)
                {
                    var moviePath = movieById[movieId].FilePath!;
                    var playableId = movieById[movieId].PlayableId;
                    if (playableId == null) continue;   // a movie with no Playable can't hold a MediaFile
                    var row = filesByPlayable[playableId.Value].FirstOrDefault(f =>
                        JellyfinPathMapper.NormalizeForCompare(f.Path) == JellyfinPathMapper.NormalizeForCompare(moviePath));
                    if (row == null)
                    {
                        row = new MediaFile { PlayableId = playableId.Value, Path = moviePath };
                        db.MediaFiles.Add(row);
                        created++;
                    }
                    else
                    {
                        updated++;
                    }

                    var source = item.MediaSources?.FirstOrDefault();
                    var video = source?.MediaStreams?.FirstOrDefault(s => s.Type == "Video");
                    var audio = source?.MediaStreams?.Where(s => s.Type == "Audio").OrderByDescending(s => s.IsDefault).FirstOrDefault();

                    row.JellyfinItemId = item.Id;
                    row.DurationTicks = item.RunTimeTicks;
                    row.Container = Truncate(source?.Container, 32);
                    row.VideoCodec = Truncate(video?.Codec, 32);
                    row.AudioCodec = Truncate(audio?.Codec, 32);
                    row.Width = video?.Width;
                    row.Height = video?.Height;
                    row.SizeBytes = source?.Size;
                    row.LastSyncedUtc = now;
                    row.MissingSinceUtc = null;
                }
            }

            int matched = chosen.Count;

            // Reverse diff: movies whose file Jellyfin no longer (or never) reported.
            var missing = movies.Where(m => !chosen.ContainsKey(m.id)).ToList();
            if (!DryRun)
            {
                foreach (var m in missing)
                {
                    if (m.PlayableId == null) continue;
                    foreach (var row in filesByPlayable[m.PlayableId.Value])
                        row.MissingSinceUtc ??= now;
                }
                await db.SaveChangesAsync(cancel);
            }

            o.WriteLine("");
            o.WriteLine($"Matched by path: {matched}/{movies.Count} movies ({100.0 * matched / Math.Max(1, movies.Count):F1}%)" +
                        (DryRun ? "" : $" — rows created {created}, updated {updated}"));
            PrintSection(o, $"DB movies with no Jellyfin item ({missing.Count}){(DryRun ? "" : " — MissingSinceUtc stamped on existing rows")}",
                missing.Select(m => $"{m.id} '{m.Title}' → {m.FilePath}"));
            // An imdb-id agreement only matters for movies that found no path match at all;
            // extra files of already-matched movies (e.g. a stray .IFO) are just untracked.
            var realFallbacks = imdbFallbackCandidates.Where(c => !chosen.ContainsKey(c.MovieId)).Select(c => c.Line).ToList();
            var extraFiles = imdbFallbackCandidates.Count - realFallbacks.Count;

            PrintSection(o, $"Jellyfin items the DB doesn't track ({untracked.Count + extraFiles})", untracked);
            PrintSection(o, $"IMDB-id fallback candidates — review, not written ({realFallbacks.Count})", realFallbacks);
            PrintSection(o, $"Jellyfin paths no mapping covers ({untranslatable.Count})", untranslatable);
            PrintSection(o, $"Duplicate Jellyfin items for one movie — earlier-listed mapping kept ({duplicateItems.Count})", duplicateItems);
            PrintSection(o, $"Duplicate DB file paths ({duplicatePaths.Count})", duplicatePaths);

            // ── Episodes, misc videos + extra movie files (Parts/Variants/Extras) ─────
            // These hang off Episode / MiscVideo Playables (no Movie.FilePath) OR are a movie's
            // non-Primary files — the movie pass above keys off the single Movie.FilePath, so it only
            // ever touches a movie's Primary. Match Jellyfin items to all those MediaFile rows by path
            // and stamp the same id + media details, so approved series AND multi-part movies (part 2+)
            // become streamable.
            // We match against Episode + Video items PLUS the movie items: series/misc content filed
            // UNDER 1 - Movies lives in the Movies Jellyfin library, so it surfaces as Movie items rather
            // than Episode/Video. Folding the movie items in lets those interleaved files match by path
            // and get a JellyfinItemId — the item TYPE is irrelevant to playback (PlaybackInfo plays any
            // item id), so no re-foldering or extra library is needed.
            var epVidItems = (await jellyfin.GetAllEpisodeAndVideoItemsAsync(cancel)).Concat(items).ToList();
            o.WriteLine("");
            o.WriteLine($"Jellyfin episode/video/movie candidate items: {epVidItems.Count}");

            // Everything the movie pass didn't already stamp: all episode/misc files, a movie's non-Primary
            // files (split parts, alternate cuts, extras), AND any movie Primary the movie pass couldn't
            // match. That last case is a movie filed under the series/episode tree (e.g. 2 - Video\Series):
            // Jellyfin surfaces its file as a Video item, invisible to the movie pass (which only scans
            // Movie items) — but visible here, since the movie items are folded into epVidItems above.
            var pass1MatchedPlayables = chosen.Keys
                .Select(id => movieById[id].PlayableId)
                .Where(pid => pid != null).Select(pid => pid!.Value)
                .ToHashSet();
            var nonMovieFiles = (await (
                    from f in db.MediaFiles
                    join p in db.Playables on f.PlayableId equals p.Id
                    select new { File = f, p.Kind }).ToListAsync(cancel))
                .Where(x => x.Kind != PlayableKind.Movie
                            || x.File.Role != MovieFileRole.Primary
                            || !pass1MatchedPlayables.Contains(x.File.PlayableId))
                .Select(x => x.File)
                .ToList();
            var nonMovieByPath = new Dictionary<string, MediaFile>();
            foreach (var f in nonMovieFiles)
                nonMovieByPath[JellyfinPathMapper.NormalizeForCompare(f.Path)] = f;   // last wins on a dup path (rare)

            var epUntranslatable = new List<string>();
            var epUntracked = new List<string>();
            var matchedEpFileIds = new HashSet<int>();
            foreach (var item in epVidItems)
            {
                if (string.IsNullOrEmpty(item.Path)) { epUntracked.Add($"(no path) {item.Name} [{item.Id}]"); continue; }
                if (!JellyfinPathMapper.TryTranslateToDb(item.Path, config.JellyfinPathMappings, out var dbPath, out _))
                { epUntranslatable.Add(item.Path); continue; }
                if (!nonMovieByPath.TryGetValue(JellyfinPathMapper.NormalizeForCompare(dbPath), out var row))
                { epUntracked.Add(item.Path); continue; }
                if (!matchedEpFileIds.Add(row.Id)) continue;   // first Jellyfin item wins per file

                if (!DryRun)
                {
                    var src = item.MediaSources?.FirstOrDefault();
                    var vid = src?.MediaStreams?.FirstOrDefault(s => s.Type == "Video");
                    var aud = src?.MediaStreams?.Where(s => s.Type == "Audio").OrderByDescending(s => s.IsDefault).FirstOrDefault();
                    row.JellyfinItemId = item.Id;
                    row.DurationTicks = item.RunTimeTicks;
                    row.Container = Truncate(src?.Container, 32);
                    row.VideoCodec = Truncate(vid?.Codec, 32);
                    row.AudioCodec = Truncate(aud?.Codec, 32);
                    row.Width = vid?.Width;
                    row.Height = vid?.Height;
                    row.SizeBytes = src?.Size;
                    row.LastSyncedUtc = now;
                    row.MissingSinceUtc = null;
                }
            }
            if (!DryRun)
            {
                foreach (var f in nonMovieFiles)
                    if (!matchedEpFileIds.Contains(f.Id)) f.MissingSinceUtc ??= now;
                await db.SaveChangesAsync(cancel);
            }

            o.WriteLine($"Episode/movie-part/misc files matched by path: {matchedEpFileIds.Count}/{nonMovieFiles.Count}" +
                        (nonMovieFiles.Count == 0 ? "" : $" ({100.0 * matchedEpFileIds.Count / nonMovieFiles.Count:F1}%)"));
            PrintSection(o, $"Episode/movie-part/misc Jellyfin items the DB doesn't track ({epUntracked.Count})", epUntracked);
            PrintSection(o, $"Episode/movie-part/misc Jellyfin paths no mapping covers ({epUntranslatable.Count})", epUntranslatable);
        }

        private void PrintSection(ConsoleWriter o, string heading, IEnumerable<string> lines)
        {
            o.WriteLine("");
            o.WriteLine(heading);
            int shown = 0;
            foreach (var line in lines)
            {
                if (shown++ >= Samples) { o.WriteLine($"  … ({heading.Split('(')[0].Trim()}: more omitted, raise --samples to see)"); break; }
                o.WriteLine($"  {line}");
            }
            if (shown == 0) o.WriteLine("  (none)");
        }

        private static string? Truncate(string? s, int max) =>
            s != null && s.Length > max ? s.Substring(0, max) : s;
    }
}
