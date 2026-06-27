using System;
using System.Collections.Generic;
using System.IO;
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
    /// <see cref="JellyfinSyncReport"/>.
    ///
    /// Move/rename aware: matching is path-keyed, so tidying the NAS (moving files or renaming folders)
    /// would otherwise leave a title's row pointing at a dead path. After the path passes, a fingerprint
    /// pass re-points any unmatched DB row to an untracked Jellyfin item when their (filename, size) match
    /// is UNIQUE 1:1 on both sides — so the title follows the file instead of going missing. Ambiguous or
    /// name-changed candidates are reported, never silently applied.
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

            // One fetch of every leaf media item (Movie/Episode/Video), routed below PURELY by file path —
            // never by Jellyfin item type — so the sync is identical for typed and "homevideos" libraries.
            var items = await jellyfin.GetAllVideoItemsAsync(cancel);
            r.MovieItems = items.Count;

            using var db = await dbFactory.CreateDbContextAsync(cancel);
            var movies = await db.Movies
                .Where(m => m.FilePath != null && m.FilePath != "")
                .Select(m => new { m.id, m.Title, m.FilePath, m.imdbID, m.PlayableId })
                .ToListAsync(cancel);
            // Loaded always (not just on write) because the move-detection pass fingerprints existing rows.
            var existingFiles = await db.MediaFiles.ToListAsync(cancel);
            var filesByPlayable = existingFiles.ToLookup(f => f.PlayableId);
            r.MoviesWithPath = movies.Count;
            r.ExistingFileRows = existingFiles.Count;

            // DB path → movie. Duplicate paths are matched to the first movie and reported.
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
            var movieIdByPlayable = movies.Where(m => m.PlayableId != null)
                .ToDictionary(m => m.PlayableId!.Value, m => m.id);

            // Jellyfin item ids and DB rows linked this run (across all passes); the move/missing/untracked
            // accounting keys off these.
            var matchedItemIds = new HashSet<string>();
            var matchedRows = new HashSet<MediaFile>();
            var imdbFallbackItemIds = new HashSet<string>();

            var imdbFallbackCandidates = new List<(int MovieId, string Line)>();
            int created = 0, updated = 0;
            var now = DateTime.UtcNow;

            // Pass 1: resolve each Jellyfin movie item to a movie, keeping one item per movie.
            var chosen = new Dictionary<int, (JellyfinItem Item, int MappingIndex)>();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Path)) continue;   // counted as untracked at the end
                if (!JellyfinPathMapper.TryTranslateToDb(item.Path, config.JellyfinPathMappings, out var dbPath, out var mappingIndex))
                    continue;
                if (!byPath.TryGetValue(JellyfinPathMapper.NormalizeForCompare(dbPath), out var movie))
                {
                    if (item.ImdbId != null && byImdb.TryGetValue(item.ImdbId, out var byId))
                    {
                        imdbFallbackCandidates.Add((byId.id, $"{item.Path} ↔ movie {byId.id} '{byId.Title}' (shared {item.ImdbId})"));
                        imdbFallbackItemIds.Add(item.Id);
                    }
                    continue;
                }
                if (chosen.TryGetValue(movie.Id, out var existing))
                {
                    var loser = mappingIndex < existing.MappingIndex ? existing.Item : item;
                    if (mappingIndex < existing.MappingIndex) chosen[movie.Id] = (item, mappingIndex);
                    r.DuplicateItems.Add($"movie {movie.Id} '{movie.Title}': kept [{chosen[movie.Id].Item.Path}], ignored [{loser.Path}]");
                }
                else chosen[movie.Id] = (item, mappingIndex);
            }

            // Pass 1 write: the chosen item per movie.
            foreach (var (movieId, (item, _)) in chosen)
            {
                matchedItemIds.Add(item.Id);
                var moviePath = movieById[movieId].FilePath!;
                var playableId = movieById[movieId].PlayableId;
                if (playableId == null) continue;
                var row = filesByPlayable[playableId.Value].FirstOrDefault(f =>
                    JellyfinPathMapper.NormalizeForCompare(f.Path) == JellyfinPathMapper.NormalizeForCompare(moviePath));
                if (!dryRun)
                {
                    if (row == null) { row = new MediaFile { PlayableId = playableId.Value, Path = moviePath }; db.MediaFiles.Add(row); created++; }
                    else updated++;
                    StampFromItem(row, item, now);
                }
                if (row != null) matchedRows.Add(row);
            }
            r.MoviesMatched = chosen.Count;
            r.MoviesTotal = movies.Count;

            // Pass 2: episode/misc files + a movie's non-Primary files + movie primaries pass 1 missed.
            // Reuses the single item list fetched above (it already holds every leaf item, regardless of
            // library type); routing is purely by file path, so nothing here depends on Jellyfin grouping.
            var epVidItems = items;
            r.EpVidItems = epVidItems.Count;

            var pass1MatchedPlayables = chosen.Keys
                .Select(id => movieById[id].PlayableId).Where(p => p != null).Select(p => p!.Value).ToHashSet();
            var nonMovieFiles = (await (
                    from f in db.MediaFiles
                    join p in db.Playables on f.PlayableId equals p.Id
                    select new { File = f, p.Kind }).ToListAsync(cancel))
                .Where(x => x.Kind != PlayableKind.Movie || x.File.Role != MovieFileRole.Primary
                            || !pass1MatchedPlayables.Contains(x.File.PlayableId))
                .Select(x => x.File).ToList();
            // One physical file can back SEVERAL MediaFile rows (a stacked file covering multiple episodes):
            // group by path and stamp the id onto EVERY row sharing it.
            var nonMovieByPath = new Dictionary<string, List<MediaFile>>();
            foreach (var f in nonMovieFiles)
            {
                var key = JellyfinPathMapper.NormalizeForCompare(f.Path);
                if (!nonMovieByPath.TryGetValue(key, out var list)) nonMovieByPath[key] = list = new List<MediaFile>();
                list.Add(f);
            }

            var matchedEpFileIds = new HashSet<int>();
            foreach (var item in epVidItems)
            {
                if (string.IsNullOrEmpty(item.Path)) continue;
                if (!JellyfinPathMapper.TryTranslateToDb(item.Path, config.JellyfinPathMappings, out var dbPath, out _)) continue;
                if (!nonMovieByPath.TryGetValue(JellyfinPathMapper.NormalizeForCompare(dbPath), out var rows)) continue;
                matchedItemIds.Add(item.Id);
                foreach (var row in rows)
                {
                    if (!matchedEpFileIds.Add(row.Id)) continue;   // first Jellyfin item wins per file
                    matchedRows.Add(row);
                    if (!dryRun) StampFromItem(row, item, now);
                }
            }
            r.EpMatched = matchedEpFileIds.Count;
            r.EpTotal = nonMovieFiles.Count;

            // ── Move / rename detection ───────────────────────────────────────────────
            // Anything still unmatched is a DB row whose path no Jellyfin item has. Pair those against
            // Jellyfin items nothing matched, by (filename, size). A unique 1:1 (name,size) match is a
            // moved file or renamed folder — re-point the row to the new path so it keeps streaming.
            var translatedUntracked = new List<(JellyfinItem Item, string DbPath, long? Size)>();
            foreach (var item in epVidItems.GroupBy(i => i.Id).Select(g => g.First()))
            {
                if (matchedItemIds.Contains(item.Id)) continue;
                if (string.IsNullOrEmpty(item.Path)) { r.Untracked.Add($"(no path) {item.Name} [{item.Id}]"); continue; }
                if (!JellyfinPathMapper.TryTranslateToDb(item.Path, config.JellyfinPathMappings, out var dbPath, out _))
                { r.Untranslatable.Add(item.Path); continue; }
                translatedUntracked.Add((item, dbPath, item.MediaSources?.FirstOrDefault()?.Size));
            }

            static string Base(string p) => Path.GetFileName(p.Replace('/', '\\'));
            // Candidate rows = EVERY existing file, not just unmatched ones. A renamed FOLDER leaves a stale
            // Jellyfin item at the OLD path (incremental scans don't purge it), so the path pass can match a
            // row to that DEAD item and mask the move. Re-pointing keys off a same-(name,size) UNtracked item
            // (the file's new location) and skips rows already at the right path, so a correctly-matched row
            // is left untouched. We never delete anything from Jellyfin — the stale entry is left for
            // Jellyfin's own scan validation to drop; we just keep OUR row pointing at the live item.
            var itemByFp = UniqueByKey(translatedUntracked.Where(u => u.Size != null), u => (Base(u.DbPath).ToLowerInvariant(), u.Size));
            var fileByFp = UniqueByKey(existingFiles.Where(f => f.SizeBytes != null), f => (Base(f.Path).ToLowerInvariant(), f.SizeBytes));

            var repointedRows = new HashSet<MediaFile>();
            var repointedItemIds = new HashSet<string>();
            var movieFilePathUpdates = new List<(int MovieId, string NewPath)>();
            int maskedRepoints = 0;
            foreach (var (fp, row) in fileByFp)
            {
                if (!itemByFp.TryGetValue(fp, out var u)) continue;   // no unique item with same name+size
                if (JellyfinPathMapper.NormalizeForCompare(row.Path) == JellyfinPathMapper.NormalizeForCompare(u.DbPath))
                    continue;                                          // already sitting at the right path
                if (matchedRows.Contains(row)) maskedRepoints++;       // was linked to a now-superseded (dead) item
                if (!dryRun) { row.Path = u.DbPath; StampFromItem(row, u.Item, now); }
                repointedRows.Add(row);
                repointedItemIds.Add(u.Item.Id);
                matchedRows.Add(row);
                if (row.Role == MovieFileRole.Primary && movieIdByPlayable.TryGetValue(row.PlayableId, out var mid))
                    movieFilePathUpdates.Add((mid, u.DbPath));
                r.Repointed.Add($"{fp.Item1} → {u.DbPath}");
            }
            r.SupersededOrphans = maskedRepoints;

            // Size matches where the NAME changed (a true file rename) are surfaced for review, not applied:
            // size alone is weaker evidence, so we never guess. Only unique-1:1-by-size pairs are listed.
            var stillUnmatched = existingFiles.Where(f => !matchedRows.Contains(f)).ToList();
            var fileBySize = UniqueByKey(stillUnmatched.Where(f => f.SizeBytes != null), f => f.SizeBytes);
            var itemBySize = UniqueByKey(translatedUntracked.Where(u => !repointedItemIds.Contains(u.Item.Id) && u.Size != null), u => u.Size);
            foreach (var (size, row) in fileBySize)
                if (itemBySize.TryGetValue(size, out var u))
                    r.PossibleRenames.Add($"{row.Path}  ↔  {u.DbPath}  (same size {size} B, name changed — review)");

            // Whatever we didn't re-point (and isn't already reported as an IMDB-id fallback) is genuinely
            // a Jellyfin item the DB doesn't track.
            foreach (var u in translatedUntracked)
                if (!repointedItemIds.Contains(u.Item.Id) && !imdbFallbackItemIds.Contains(u.Item.Id))
                    r.Untracked.Add(u.Item.Path ?? u.DbPath);

            // Carry the Movie.FilePath of any re-pointed movie primary to the new location too, so the
            // movie pass matches it directly next time (not just via the episode/misc fallback).
            if (!dryRun && movieFilePathUpdates.Count > 0)
            {
                var ids = movieFilePathUpdates.Select(x => x.MovieId).ToHashSet();
                var entities = await db.Movies.Where(m => ids.Contains(m.id)).ToListAsync(cancel);
                var newPathById = movieFilePathUpdates.GroupBy(x => x.MovieId).ToDictionary(g => g.Key, g => g.Last().NewPath);
                foreach (var m in entities)
                    if (newPathById.TryGetValue(m.id, out var np)) m.FilePath = np;
            }

            // Rescue hidden alternate versions: a row that path-matched nothing but whose ALREADY-STORED
            // JellyfinItemId still resolves to a live item at the same path is NOT missing. Jellyfin groups
            // multi-part movies ("Title (CD 1)"/"(CD 2)") as alternate versions and excludes the secondary
            // parts from every path-based listing (server-root AND parentId), so the normal passes can't see
            // them — but an explicit id lookup does, and they remain streamable. Without this they'd be flagged
            // MissingSinceUtc and the stream endpoint (which requires MissingSinceUtc == null) would refuse to
            // play that part. Verified by path so a stale id pointing elsewhere is never silently kept.
            var rescueCandidates = existingFiles
                .Where(f => !matchedRows.Contains(f) && !string.IsNullOrEmpty(f.JellyfinItemId))
                .ToList();
            if (rescueCandidates.Count > 0)
            {
                var liveItems = await jellyfin.GetItemsByIdsAsync(rescueCandidates.Select(f => f.JellyfinItemId!), cancel);
                var liveByPath = new Dictionary<string, JellyfinItem>();
                foreach (var it in liveItems)
                {
                    if (string.IsNullOrEmpty(it.Path)) continue;
                    if (JellyfinPathMapper.TryTranslateToDb(it.Path, config.JellyfinPathMappings, out var dbPath, out _))
                        liveByPath[JellyfinPathMapper.NormalizeForCompare(dbPath)] = it;
                }
                foreach (var f in rescueCandidates)
                    if (liveByPath.TryGetValue(JellyfinPathMapper.NormalizeForCompare(f.Path), out var item))
                    {
                        matchedRows.Add(f);
                        r.RescuedAlternateVersions++;
                        if (!dryRun) StampFromItem(f, item, now);
                    }
            }

            // Still unmatched after the move pass → stamp MissingSinceUtc (existing rows only).
            if (!dryRun)
            {
                foreach (var f in existingFiles.Where(f => !matchedRows.Contains(f))) f.MissingSinceUtc ??= now;
                await db.SaveChangesAsync(cancel);
            }

            r.Created = created;
            r.Updated = updated;
            // Missing titles = movies whose file we couldn't locate even after move-detection.
            r.MissingMovies.AddRange(movies.Where(m => !chosen.ContainsKey(m.id)
                    && (m.PlayableId == null || filesByPlayable[m.PlayableId.Value].All(f => !matchedRows.Contains(f))))
                .Select(m => $"{m.id} '{m.Title}' → {m.FilePath}"));
            r.ImdbFallbacks.AddRange(imdbFallbackCandidates.Where(c => !chosen.ContainsKey(c.MovieId)).Select(c => c.Line));

            logger.LogInformation("Jellyfin sync ({Mode}): movies {MM}/{MT}, ep/misc {EM}/{ET}, created {C}, updated {U}, re-pointed {R}, rescued-versions {RV}",
                dryRun ? "dry-run" : "write", r.MoviesMatched, r.MoviesTotal, r.EpMatched, r.EpTotal, r.Created, r.Updated, r.Repointed.Count, r.RescuedAlternateVersions);
            return r;
        }

        // ── Per-movie "re-link files from disk" ──────────────────────────────────────────────────────
        // For when ONE movie's file is replaced on disk (new rip, old file deleted, folder renamed). The
        // whole-library sync only auto-repoints a moved file whose (name,size) is unchanged — a new rip
        // changes both, so it would go missing and need a hand fix. These two methods do the targeted
        // repair instead: every detail (rating/viewings/poster/IMDb cache/tags) lives on the Movie/Playable
        // row, so re-pointing the file row IN PLACE keeps all of it — nothing is deleted or recreated.

        /// <summary>
        /// Kicks a SCOPED Jellyfin re-scan of just this title's shelf so a replaced/renamed file is indexed,
        /// without the full-library scan. Anchors on the stored Jellyfin item id (or the movie's IMDb id),
        /// then refreshes the GRANDPARENT folder so a RENAMED movie folder is rediscovered (falling back to
        /// the movie folder for an in-place swap). Returns immediately; poll <see cref="TryRelinkMovieFilesAsync"/>.
        /// </summary>
        public async Task<MovieRelinkRefreshResult> TriggerMovieFolderRefreshAsync(int movieId, CancellationToken cancel = default)
        {
            var res = new MovieRelinkRefreshResult();
            using var db = await dbFactory.CreateDbContextAsync(cancel);
            var movie = await db.Movies.FirstOrDefaultAsync(m => m.id == movieId, cancel);
            if (movie == null) { res.Message = "Movie not found."; return res; }
            if (movie.PlayableId == null) { res.Message = "This title has no playable file slot."; return res; }

            var primary = await db.MediaFiles
                .Where(f => f.PlayableId == movie.PlayableId.Value)
                .OrderBy(f => f.Role)   // Primary (0) first
                .FirstOrDefaultAsync(cancel);

            // Anchor item in Jellyfin: the stored item id is most direct; the IMDb id re-finds it across a
            // folder rename. Either still resolves before the re-scan (the old item lingers until then).
            JellyfinItem? anchor = null;
            if (!string.IsNullOrEmpty(primary?.JellyfinItemId))
                anchor = (await jellyfin.GetItemsByIdsAsync(new[] { primary!.JellyfinItemId! }, cancel)).FirstOrDefault();
            if (anchor == null && !string.IsNullOrEmpty(movie.imdbID))
                anchor = (await jellyfin.GetItemsByImdbAsync(movie.imdbID!, cancel)).FirstOrDefault(i => !string.IsNullOrEmpty(i.Path));

            if (anchor?.ParentId is not string movieFolderId)
            {
                res.Message = "Couldn't locate this title in Jellyfin to scope a re-scan — use the full Sync from Jellyfin.";
                return res;
            }

            // Refresh the shelf ABOVE the movie folder so a renamed folder is rediscovered; if Jellyfin
            // doesn't expose the grandparent, fall back to the movie folder (covers a same-folder swap).
            var folderItem = (await jellyfin.GetItemsByIdsAsync(new[] { movieFolderId }, cancel)).FirstOrDefault();
            var refreshId = folderItem?.ParentId ?? movieFolderId;
            await jellyfin.RefreshItemAsync(refreshId, cancel);
            res.Ok = true;
            res.Message = "Re-scan started.";
            return res;
        }

        /// <summary>
        /// One idempotent probe of the title's current Jellyfin items: if the replaced file is now indexed,
        /// re-points the existing Primary <see cref="MediaFile"/> to it IN PLACE (refreshing item id, codec,
        /// size, duration; clearing MissingSinceUtc) and ingests any new sibling Extras; otherwise reports
        /// <see cref="MovieRelinkResult.Scanning"/> so the caller polls again. Re-running after success is a
        /// no-op ("already linked"). The Movie row and everything attached to it are never touched.
        /// </summary>
        public async Task<MovieRelinkResult> TryRelinkMovieFilesAsync(int movieId, CancellationToken cancel = default)
        {
            var res = new MovieRelinkResult();
            if (config.JellyfinPathMappings.Count == 0) { res.Message = "No JellyfinPathMappings configured."; return res; }

            using var db = await dbFactory.CreateDbContextAsync(cancel);
            var movie = await db.Movies.FirstOrDefaultAsync(m => m.id == movieId, cancel);
            if (movie == null) { res.Message = "Movie not found."; return res; }
            res.MovieTitle = movie.Title;
            if (movie.PlayableId == null) { res.Message = "This title has no playable file slot."; return res; }
            var playableId = movie.PlayableId.Value;

            var files = await db.MediaFiles.Where(f => f.PlayableId == playableId).ToListAsync(cancel);
            var primary = files.FirstOrDefault(f => f.Role == MovieFileRole.Primary) ?? files.FirstOrDefault();
            var oldPath = primary?.Path ?? movie.FilePath;
            if (string.IsNullOrEmpty(oldPath)) { res.Message = "No recorded path to locate this title's folder."; return res; }
            res.OldPath = oldPath;
            var oldNorm = JellyfinPathMapper.NormalizeForCompare(oldPath);
            var oldSize = primary?.SizeBytes;

            // Candidate Jellyfin items for this movie: by IMDb id (re-finds across a folder rename) plus the
            // old stored item id (covers an in-place swap that kept the same item).
            var candidates = new List<JellyfinItem>();
            if (!string.IsNullOrEmpty(movie.imdbID))
                candidates.AddRange(await jellyfin.GetItemsByImdbAsync(movie.imdbID!, cancel));
            if (!string.IsNullOrEmpty(primary?.JellyfinItemId))
                candidates.AddRange(await jellyfin.GetItemsByIdsAsync(new[] { primary!.JellyfinItemId! }, cancel));

            var resolved = new List<(JellyfinItem Item, string DbPath, long? Size)>();
            foreach (var it in candidates.GroupBy(i => i.Id).Select(g => g.First()))
                if (!string.IsNullOrEmpty(it.Path)
                    && JellyfinPathMapper.TryTranslateToDb(it.Path!, config.JellyfinPathMappings, out var dp, out _))
                    resolved.Add((it, dp, it.MediaSources?.FirstOrDefault()?.Size));

            if (resolved.Count == 0) { res.Scanning = true; res.Message = "Jellyfin hasn't indexed a file for this title yet."; return res; }

            // The "new" primary: a path that differs from the recorded one, OR the same path with a changed
            // size (an in-place re-rip). If all we still see is the old path at the old size, the re-scan
            // hasn't landed yet → report scanning so the caller polls again.
            var change = resolved.FirstOrDefault(x =>
                JellyfinPathMapper.NormalizeForCompare(x.DbPath) != oldNorm
                || (x.Size != null && oldSize != null && x.Size != oldSize));
            if (change.Item == null)
            {
                var same = resolved.FirstOrDefault(x => JellyfinPathMapper.NormalizeForCompare(x.DbPath) == oldNorm);
                if (same.Item != null && primary != null && primary.JellyfinItemId == same.Item.Id && primary.MissingSinceUtc == null)
                {
                    res.Done = true; res.NewPath = oldPath; res.NowStreamable = true;
                    res.Message = "Already linked to the current file.";
                    return res;
                }
                res.Scanning = true; res.Message = "Waiting for Jellyfin to pick up the new file…";
                return res;
            }

            var newItem = change.Item;
            var newPath = change.DbPath;
            var now = DateTime.UtcNow;

            if (primary == null)
            {
                primary = new MediaFile { PlayableId = playableId, Path = newPath, Role = MovieFileRole.Primary };
                db.MediaFiles.Add(primary);
            }
            else primary.Path = newPath;
            StampFromItem(primary, newItem, now);
            movie.FilePath = newPath;
            res.PrimaryRepointed = true;
            res.NewPath = newPath;
            res.NowStreamable = primary.JellyfinItemId != null;

            // Sibling Extras: other video files in the same folder not already mapped to this title. Added as
            // Extra-role rows and reported, never deleted — re-running just picks up anything missed.
            if (!string.IsNullOrEmpty(newItem.ParentId))
            {
                var siblings = await jellyfin.GetItemsUnderParentAsync(newItem.ParentId!, cancel);
                var mapped = files.Select(f => JellyfinPathMapper.NormalizeForCompare(f.Path)).ToHashSet();
                mapped.Add(JellyfinPathMapper.NormalizeForCompare(newPath));
                foreach (var sib in siblings.GroupBy(i => i.Id).Select(g => g.First()))
                {
                    if (sib.Id == newItem.Id || string.IsNullOrEmpty(sib.Path)) continue;
                    if (!JellyfinPathMapper.TryTranslateToDb(sib.Path!, config.JellyfinPathMappings, out var sp, out _)) continue;
                    if (!mapped.Add(JellyfinPathMapper.NormalizeForCompare(sp))) continue;   // already mapped / the primary
                    var extra = new MediaFile { PlayableId = playableId, Path = sp, Role = MovieFileRole.Extra, Label = Truncate(sib.Name, 128) };
                    StampFromItem(extra, sib, now);
                    db.MediaFiles.Add(extra);
                    res.ExtrasAdded.Add(sib.Name ?? sp);
                }
            }

            await db.SaveChangesAsync(cancel);
            res.Done = true;
            res.Message = $"Re-linked '{movie.Title}' to the new file.";
            logger.LogInformation("Re-linked movie {Id} '{Title}': {Old} → {New} (+{Extras} extras)",
                movieId, movie.Title, oldPath, newPath, res.ExtrasAdded.Count);
            return res;
        }

        private static void StampFromItem(MediaFile row, JellyfinItem item, DateTime now)
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

        /// <summary>Index by key, keeping ONLY keys that occur exactly once (so a match is unambiguous).</summary>
        private static Dictionary<TKey, TVal> UniqueByKey<TKey, TVal>(IEnumerable<TVal> source, Func<TVal, TKey> key)
            where TKey : notnull
        {
            var seen = new Dictionary<TKey, TVal>();
            var dup = new HashSet<TKey>();
            foreach (var v in source)
            {
                var k = key(v);
                if (!seen.TryAdd(k, v)) dup.Add(k);
            }
            foreach (var k in dup) seen.Remove(k);
            return seen;
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
        public int RescuedAlternateVersions { get; set; }
        public List<string> MissingMovies { get; } = new();
        public List<string> Untracked { get; } = new();
        public List<string> ImdbFallbacks { get; } = new();
        public List<string> Untranslatable { get; } = new();
        public List<string> DuplicateItems { get; } = new();
        public List<string> DuplicatePaths { get; } = new();
        /// <summary>Moved files / renamed folders re-pointed to their new location (name+size matched 1:1).</summary>
        public List<string> Repointed { get; } = new();
        /// <summary>Of <see cref="Repointed"/>, how many were rescued from a DEAD Jellyfin item the path
        /// pass had matched (a renamed-folder orphan masking the move) rather than from a clean gap.</summary>
        public int SupersededOrphans { get; set; }
        /// <summary>Same-size-but-renamed candidates surfaced for review — never auto-applied.</summary>
        public List<string> PossibleRenames { get; } = new();

        public int EpVidItems { get; set; }
        public int EpMatched { get; set; }
        public int EpTotal { get; set; }
        public List<string> EpUntracked { get; } = new();
        public List<string> EpUntranslatable { get; } = new();
    }

    /// <summary>Result of <see cref="JellyfinSyncService.TriggerMovieFolderRefreshAsync"/> — did the scoped
    /// re-scan start, and why not if it didn't.</summary>
    public class MovieRelinkRefreshResult
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>Result of one <see cref="JellyfinSyncService.TryRelinkMovieFilesAsync"/> probe. <see cref="Scanning"/>
    /// means Jellyfin hasn't indexed the new file yet (poll again); <see cref="Done"/> means it's linked.</summary>
    public class MovieRelinkResult
    {
        public bool Done { get; set; }
        public bool Scanning { get; set; }
        public bool PrimaryRepointed { get; set; }
        public bool NowStreamable { get; set; }
        public string? OldPath { get; set; }
        public string? NewPath { get; set; }
        public string? MovieTitle { get; set; }
        public string? Message { get; set; }
        public List<string> ExtrasAdded { get; } = new();
    }
}
