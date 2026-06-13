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
    /// result in <see cref="MovieFile"/> (docs/streaming-plan.md §6): Jellyfin item id,
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
                .Select(m => new { m.id, m.Title, m.FilePath, m.imdbID })
                .ToListAsync(cancel);
            // Dry-run works before the MovieFile migration has been applied; existing rows
            // only matter when writing.
            var existingFiles = DryRun ? new List<MovieFile>() : await db.MovieFiles.ToListAsync(cancel);
            var filesByMovie = existingFiles.ToLookup(f => f.MovieID);
            o.WriteLine($"DB movies with a file path: {movies.Count}" +
                        (DryRun ? "" : $"   existing MovieFile rows: {existingFiles.Count}"));

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
                    var row = filesByMovie[movieId].FirstOrDefault(f =>
                        JellyfinPathMapper.NormalizeForCompare(f.Path) == JellyfinPathMapper.NormalizeForCompare(moviePath));
                    if (row == null)
                    {
                        row = new MovieFile { MovieID = movieId, Path = moviePath };
                        db.MovieFiles.Add(row);
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
                    foreach (var row in filesByMovie[m.id])
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
