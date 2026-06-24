using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;

namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// The body of the <c>sync-jellyfin</c> operation, factored out of the CLI command so the web app
    /// can run it too (the admin "Sync from Jellyfin" button). Matches Jellyfin's library items against
    /// the DB's stored file paths and records the result on <see cref="MediaFile"/> (Jellyfin item id,
    /// duration, container/codec/size); two-way diffs are collected into the returned
    /// <see cref="JellyfinSyncReport"/> (DB files Jellyfin doesn't have → <c>MissingSinceUtc</c>; Jellyfin
    /// items the DB doesn't track). IMDB-id agreements are reported for review, never written.
    /// Re-runnable any time; <paramref name="dryRun"/> matches and reports without writing.
    /// </summary>
    public class JellyfinSyncService
    {
        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly JellyfinApi jellyfin;
        private readonly MovieTheaterConfiguration config;
        private readonly ILogger<JellyfinSyncService> logger;

        public JellyfinSyncService(IDbContextFactory<MovieDb> dbFactory, JellyfinApi jellyfin,
            MovieTheaterConfiguration config, ILogger<JellyfinSyncService> logger)
        {
            this.dbFactory = dbFactory;
            this.jellyfin = jellyfin;
            this.config = config;
            this.logger = logger;
        }

        public async Task<JellyfinSyncReport> RunAsync(bool dryRun, CancellationToken cancel = default)
        {
            var r = new JellyfinSyncReport { DryRun = dryRun };

            if (config.JellyfinPathMappings.Count == 0)
            {
                r.Aborted = "No JellyfinPathMappings configured — nothing can match.";
                return r;
            }

            var info = await jellyfin.GetSystemInfoAsync(cancel);
            r.ServerName = info.ServerName;
            r.Version = info.Version;

            var items = await jellyfin.GetAllMovieItemsAsync(cancel);
            r.MovieItems = items.Count;

            using var db = await dbFactory.CreateDbContextAsync(cancel);
            var movies = await db.Movies
                .Where(m => m.FilePath != null && m.FilePath != "")
                .Select(m => new { m.id, m.Title, m.FilePath, m.imdbID, m.PlayableId })
                .ToListAsync(cancel);
            // Files now hang off the movie's Playable (Phase-4 cutover). Dry-run works before any
            // MediaFile rows exist; existing rows only matter when writing.
            var existingFiles = dryRun ? new List<MediaFile>() : await db.MediaFiles.ToListAsync(cancel);
            var filesByPlayable = existingFiles.ToLookup(f => f.PlayableId);
            r.MoviesWithPath = movies.Count;
            r.ExistingFileRows = existingFiles.Count;

            // DB path → movie. Duplicate paths (DB duplicate rows) are matched to the first
            // movie and reported rather than guessed at.
            var byPath = new Dictionary<string, (int Id, string Title, string FilePath)>();
            foreach (var m in movies)
            {
                var key = JellyfinPathMapper.NormalizeForCompare(m.FilePath!);
                if (!byPath.TryAdd(key, (m.id, m.Title ?? "?", m.FilePath!)))
                    r.DuplicatePaths.Add($"{m.FilePath} (movie {m.id} '{m.Title}' collides with movie {byPath[key].Id} '{byPath[key].Title}')");
            }
            var byImdb = movies.Where(m => !string.IsNullOrEmpty(m.imdbID))
                .GroupBy(m => m.imdbID!).ToDictionary(g => g.Key, g => g.First());
            var movieById = movies.ToDictionary(m => m.id);

            var imdbFallbackCandidates = new List<(int MovieId, string Line)>();
            int created = 0, updated = 0;
            var now = DateTime.UtcNow;

            // Pass 1: resolve each Jellyfin item to a movie, keeping one item per movie.
            // Jellyfin can hold duplicate items for one file (e.g. a leftover drive-letter
            // library folder beside the UNC one); the earlier-listed mapping wins.
            var chosen = new Dictionary<int, (JellyfinItem Item, int MappingIndex)>();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Path)) { r.Untracked.Add($"(no path) {item.Name} [{item.Id}]"); continue; }

                if (!JellyfinPathMapper.TryTranslateToDb(item.Path, config.JellyfinPathMappings, out var dbPath, out var mappingIndex))
                {
                    r.Untranslatable.Add(item.Path);
                    continue;
                }

                if (!byPath.TryGetValue(JellyfinPathMapper.NormalizeForCompare(dbPath), out var movie))
                {
                    // Path didn't match; an IMDB-id agreement is reported for review, not trusted.
                    if (item.ImdbId != null && byImdb.TryGetValue(item.ImdbId, out var byId))
                        imdbFallbackCandidates.Add((byId.id, $"{item.Path} ↔ movie {byId.id} '{byId.Title}' (shared {item.ImdbId})"));
                    else
                        r.Untracked.Add(item.Path);
                    continue;
                }

                if (chosen.TryGetValue(movie.Id, out var existing))
                {
                    var loser = mappingIndex < existing.MappingIndex ? existing.Item : item;
                    if (mappingIndex < existing.MappingIndex)
                        chosen[movie.Id] = (item, mappingIndex);
                    r.DuplicateItems.Add($"movie {movie.Id} '{movie.Title}': kept [{chosen[movie.Id].Item.Path}], ignored [{loser.Path}]");
                }
                else
                {
                    chosen[movie.Id] = (item, mappingIndex);
                }
            }

            // Pass 2: write the chosen item per movie.
            if (!dryRun)
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

            r.MoviesMatched = chosen.Count;
            r.MoviesTotal = movies.Count;
            r.Created = created;
            r.Updated = updated;

            // Reverse diff: movies whose file Jellyfin no longer (or never) reported.
            var missing = movies.Where(m => !chosen.ContainsKey(m.id)).ToList();
            if (!dryRun)
            {
                foreach (var m in missing)
                {
                    if (m.PlayableId == null) continue;
                    foreach (var row in filesByPlayable[m.PlayableId.Value])
                        row.MissingSinceUtc ??= now;
                }
                await db.SaveChangesAsync(cancel);
            }
            r.MissingMovies.AddRange(missing.Select(m => $"{m.id} '{m.Title}' → {m.FilePath}"));

            // An imdb-id agreement only matters for movies that found no path match at all;
            // extra files of already-matched movies (e.g. a stray .IFO) are just untracked.
            var realFallbacks = imdbFallbackCandidates.Where(c => !chosen.ContainsKey(c.MovieId)).Select(c => c.Line).ToList();
            r.ImdbFallbacks.AddRange(realFallbacks);

            // ── Episodes, misc videos + extra movie files (Parts/Variants/Extras) ─────
            // These hang off Episode / MiscVideo Playables (no Movie.FilePath) OR are a movie's non-Primary
            // files. Match Jellyfin Episode/Video items PLUS the movie items (series/misc filed UNDER
            // 1 - Movies surfaces as Movie items) to all those MediaFile rows by path; item TYPE is
            // irrelevant to playback.
            var epVidItems = (await jellyfin.GetAllEpisodeAndVideoItemsAsync(cancel)).Concat(items).ToList();
            r.EpVidItems = epVidItems.Count;

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
            // One physical file can back SEVERAL MediaFile rows (a stacked file covering multiple episodes):
            // group by path and stamp the id onto EVERY row sharing it.
            var nonMovieByPath = new Dictionary<string, List<MediaFile>>();
            foreach (var f in nonMovieFiles)
            {
                var key = JellyfinPathMapper.NormalizeForCompare(f.Path);
                if (!nonMovieByPath.TryGetValue(key, out var list))
                    nonMovieByPath[key] = list = new List<MediaFile>();
                list.Add(f);
            }

            var matchedEpFileIds = new HashSet<int>();
            foreach (var item in epVidItems)
            {
                if (string.IsNullOrEmpty(item.Path)) { r.EpUntracked.Add($"(no path) {item.Name} [{item.Id}]"); continue; }
                if (!JellyfinPathMapper.TryTranslateToDb(item.Path, config.JellyfinPathMappings, out var dbPath, out _))
                { r.EpUntranslatable.Add(item.Path); continue; }
                if (!nonMovieByPath.TryGetValue(JellyfinPathMapper.NormalizeForCompare(dbPath), out var rows))
                { r.EpUntracked.Add(item.Path); continue; }

                var src = item.MediaSources?.FirstOrDefault();
                var vid = src?.MediaStreams?.FirstOrDefault(s => s.Type == "Video");
                var aud = src?.MediaStreams?.Where(s => s.Type == "Audio").OrderByDescending(s => s.IsDefault).FirstOrDefault();
                foreach (var row in rows)
                {
                    if (!matchedEpFileIds.Add(row.Id)) continue;   // first Jellyfin item wins per file
                    if (dryRun) continue;
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
            if (!dryRun)
            {
                foreach (var f in nonMovieFiles)
                    if (!matchedEpFileIds.Contains(f.Id)) f.MissingSinceUtc ??= now;
                await db.SaveChangesAsync(cancel);
            }

            r.EpMatched = matchedEpFileIds.Count;
            r.EpTotal = nonMovieFiles.Count;

            logger.LogInformation("Jellyfin sync ({Mode}): movies {MM}/{MT}, ep/misc {EM}/{ET}, created {C}, updated {U}",
                dryRun ? "dry-run" : "write", r.MoviesMatched, r.MoviesTotal, r.EpMatched, r.EpTotal, r.Created, r.Updated);
            return r;
        }

        private static string? Truncate(string? s, int max) =>
            s != null && s.Length > max ? s.Substring(0, max) : s;
    }

    /// <summary>Structured result of a <see cref="JellyfinSyncService.RunAsync"/> pass — counts plus the
    /// two-way diff lists (the CLI prints them; the admin endpoint returns counts + samples).</summary>
    public class JellyfinSyncReport
    {
        public bool DryRun { get; set; }
        public string? Aborted { get; set; }
        public string? ServerName { get; set; }
        public string? Version { get; set; }

        public int MovieItems { get; set; }
        public int MoviesWithPath { get; set; }
        public int ExistingFileRows { get; set; }
        public int MoviesMatched { get; set; }
        public int MoviesTotal { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public List<string> MissingMovies { get; } = new();
        public List<string> Untracked { get; } = new();
        public List<string> ImdbFallbacks { get; } = new();
        public List<string> Untranslatable { get; } = new();
        public List<string> DuplicateItems { get; } = new();
        public List<string> DuplicatePaths { get; } = new();

        public int EpVidItems { get; set; }
        public int EpMatched { get; set; }
        public int EpTotal { get; set; }
        public List<string> EpUntracked { get; } = new();
        public List<string> EpUntranslatable { get; } = new();
    }
}
