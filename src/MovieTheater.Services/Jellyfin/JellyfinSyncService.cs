using System;
using System.Globalization;
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
    ///
    /// <para><b>The family photo library is excluded before anything else happens</b>
    /// (docs/photos-plan.md §2.3). Every item list this class obtains from Jellyfin passes through
    /// <see cref="JellyfinFamilyExclusion"/> first, so a home video cannot become a
    /// <see cref="MediaFile"/> — and since Movie, MiscVideo, channels, recommendations and the review
    /// queue all read those rows, one filter at the source covers every downstream surface. The
    /// exclusion is by PATH and needs no library id; the id, when configured, only adds that library's
    /// own folders as further prefixes.</para>
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

            // The family photo library is dropped HERE, at the source (§2.3), before any matching, move
            // detection, extras placement or reporting can see it. Downstream code therefore needs no
            // knowledge of it at all, which is what makes the guarantee hold for surfaces written later.
            var family = await FamilyExclusionAsync(cancel);
            r.FamilyExclusionPrefixes.AddRange(family.Prefixes);

            // One fetch of every leaf media item (Movie/Episode/Video), routed below PURELY by file path —
            // never by Jellyfin item type — so the sync is identical for typed and "homevideos" libraries.
            var reported = await jellyfin.GetAllVideoItemsAsync(cancel);
            var items = family.Filter(reported, out var excludedItems);
            r.FamilyItemsExcluded += excludedItems;
            r.MovieItems = items.Count;

            // ── Blast-radius guard #1: the exclusion (§2.3) ──
            // The family collection is a corner of the disk, so excluding most of the library is not a
            // big exclusion, it is a BROKEN one — a PhotosLibraryDir that expands to a volume root, a
            // library location Jellyfin reports as a share root. IsMeaningfulRoot refuses that specific
            // shape at build time; this refuses the OUTCOME, which covers the shapes nobody predicted.
            // Aborting before a single write is the whole point: an over-wide exclusion that runs to
            // completion stamps the entire MediaFile table missing and reports a clean sync.
            if (ExceedsWriteCeiling(excludedItems, reported.Count))
            {
                r.Aborted = $"Family exclusion would drop {excludedItems} of {reported.Count} Jellyfin items "
                            + $"({Percent(excludedItems, reported.Count)}) — far more than a family collection. "
                            + "Nothing was written. Check PhotosLibraryDir and PhotosJellyfinLibraryId against the "
                            + $"prefixes in force: {string.Join(", ", family.Prefixes)}";
                logger.LogError("Jellyfin sync ABORTED: {Reason}", r.Aborted);
                return r;
            }

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
            // Rows whose server-side keyframe list went stale this run (file replaced in place) —
            // re-extracted in bulk before the final save; see ReExtractStaleKeyframesAsync.
            var staleKeyframeRows = new List<MediaFile>();
            // Rows a re-point moved to a NEW item id with the same bytes (rename/move) — their banked
            // keyframe lists are re-imported before the final save; see RestoreBankedKeyframesAsync.
            var restoreKeyframeRows = new List<MediaFile>();

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
                    if (StampFromItem(row, item, now, restoreKeyframeRows)) staleKeyframeRows.Add(row);
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
                    if (!dryRun && StampFromItem(row, item, now, restoreKeyframeRows)) staleKeyframeRows.Add(row);
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
                if (!dryRun) { row.Path = u.DbPath; if (StampFromItem(row, u.Item, now, restoreKeyframeRows)) staleKeyframeRows.Add(row); }
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
                    // Defence in depth: a row that already carries a family item id (from before the
                    // exclusion shipped) must not be RESCUED back into the movie library by it.
                    if (family.IsExcluded(it.Path)) { r.FamilyItemsExcluded++; continue; }
                    if (JellyfinPathMapper.TryTranslateToDb(it.Path, config.JellyfinPathMappings, out var dbPath, out _))
                        liveByPath[JellyfinPathMapper.NormalizeForCompare(dbPath)] = it;
                }
                foreach (var f in rescueCandidates)
                    if (liveByPath.TryGetValue(JellyfinPathMapper.NormalizeForCompare(f.Path), out var item))
                    {
                        matchedRows.Add(f);
                        r.RescuedAlternateVersions++;
                        if (!dryRun && StampFromItem(f, item, now, restoreKeyframeRows)) staleKeyframeRows.Add(f);
                    }
            }

            // ── Extras pass: attach movie bonus content (featurettes, deleted scenes, …) to its owner movie.
            // PRIMARY signal is this library's on-disk CONVENTION (shared with the file-mapping ingest via
            // ExtrasClassifier): a video in a subfolder whose name contains an extras keyword — "Extras
            // Content", "Featurettes Content", etc. The " Content" suffix is deliberate so Jellyfin scans them
            // as ORDINARY videos (here in `items`) rather than hiding them. Each is placed under the movie
            // whose folder is its nearest ancestor; never overwrites; idempotent (skips already-mapped).
            var folderToPlayable = new Dictionary<string, int>();
            foreach (var m in movies)
            {
                if (m.PlayableId == null) continue;
                var folder = ParentDir(m.FilePath);
                if (folder != null) folderToPlayable[JellyfinPathMapper.NormalizeForCompare(folder)] = m.PlayableId.Value;
            }
            var extraMapped = new HashSet<string>(existingFiles.Select(f => JellyfinPathMapper.NormalizeForCompare(f.Path)));

            // Nearest ancestor folder that is a movie's folder, plus the path RELATIVE to it (for the keyword
            // check). dir comes from dp, so it's a real prefix → Substring is safe.
            (int? Owner, string? Rel) OwnerOf(string dp)
            {
                var dir = ParentDir(dp);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    if (folderToPlayable.TryGetValue(JellyfinPathMapper.NormalizeForCompare(dir), out var pid))
                        return (pid, dp.Substring(dir.Length));
                    dir = ParentDir(dir);
                }
                return (null, null);
            }

            // (A) convention pass over the ordinary video listing.
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.Path)) continue;
                if (!JellyfinPathMapper.TryTranslateToDb(it.Path!, config.JellyfinPathMappings, out var dp, out _)) continue;
                if (extraMapped.Contains(JellyfinPathMapper.NormalizeForCompare(dp))) continue;
                var (owner, rel) = OwnerOf(dp);
                if (owner == null || ExtrasClassifier.ExtraKeyword(rel) == null) continue;
                extraMapped.Add(JellyfinPathMapper.NormalizeForCompare(dp));
                if (!dryRun) db.MediaFiles.Add(NewExtraRow(owner.Value, dp, it, now));
                r.ExtrasAttached++;
            }

            // (B) fallback for the rare rip Jellyfin DID hide as special features (a plain "Featurettes"
            // folder the library hadn't renamed to "…Content"): fetch those hidden extras and place them too.
            foreach (var ex in await jellyfin.GetAllExtraItemsAsync(cancel))
            {
                if (string.IsNullOrEmpty(ex.Path)) continue;
                // Extras come from their own sweep, so they need the family filter applied separately —
                // and this is the sweep that would otherwise pick up a family folder Jellyfin classified
                // as an extra because its name collides with a reserved one (§2.3's trap).
                if (family.IsExcluded(ex.Path)) { r.FamilyItemsExcluded++; continue; }
                if (!JellyfinPathMapper.TryTranslateToDb(ex.Path!, config.JellyfinPathMappings, out var dp, out _)) continue;
                if (extraMapped.Contains(JellyfinPathMapper.NormalizeForCompare(dp))) continue;
                var (owner, _) = OwnerOf(dp);
                if (owner == null) { r.ExtrasUnplaced++; continue; }
                extraMapped.Add(JellyfinPathMapper.NormalizeForCompare(dp));
                if (!dryRun) db.MediaFiles.Add(NewExtraRow(owner.Value, dp, ex, now));
                r.ExtrasAttached++;
            }

            // Still unmatched after the move pass → stamp MissingSinceUtc (existing rows only).
            var wouldStamp = existingFiles.Where(f => !matchedRows.Contains(f) && f.MissingSinceUtc == null).ToList();

            // ── Blast-radius guard #2: the missing-stamp sweep ──
            // A healthy run finds nearly every row it already had, so stamping most of the table is not a
            // large gap — it is an unmounted share, a changed mapping, or a Jellyfin that answered with a
            // partial library. Every one of those looks like a successful sync in the log, and every one
            // of them takes the watch button off most of the site in a single pass. Refuse the WRITE and
            // say so; the operator re-runs once the cause is fixed, and nothing had to be undone.
            if (!dryRun && ExceedsWriteCeiling(wouldStamp.Count, existingFiles.Count))
            {
                r.Aborted = $"Would stamp {wouldStamp.Count} of {existingFiles.Count} file rows as missing "
                            + $"({Percent(wouldStamp.Count, existingFiles.Count)}) — that is a broken run, not a "
                            + "library that lost its files. Nothing was written. Check that the NAS is mounted, "
                            + "that Jellyfin returned the whole library, and that JellyfinPathMappings still match.";
                logger.LogError("Jellyfin sync ABORTED before writing: {Reason}", r.Aborted);
                return r;
            }

            if (!dryRun)
            {
                foreach (var f in wouldStamp) f.MissingSinceUtc = now;
                if (staleKeyframeRows.Count > 0)
                    await ReExtractStaleKeyframesAsync(staleKeyframeRows, now, cancel);
                if (restoreKeyframeRows.Count > 0)
                    await RestoreBankedKeyframesAsync(db, restoreKeyframeRows, now, cancel);
                await db.SaveChangesAsync(cancel);
            }

            r.Created = created;
            r.Updated = updated;
            // Missing titles = movies whose file we couldn't locate even after move-detection.
            r.MissingMovies.AddRange(movies.Where(m => !chosen.ContainsKey(m.id)
                    && (m.PlayableId == null || filesByPlayable[m.PlayableId.Value].All(f => !matchedRows.Contains(f))))
                .Select(m => $"{m.id} '{m.Title}' → {m.FilePath}"));
            r.ImdbFallbacks.AddRange(imdbFallbackCandidates.Where(c => !chosen.ContainsKey(c.MovieId)).Select(c => c.Line));

            logger.LogInformation("Jellyfin sync ({Mode}): movies {MM}/{MT}, ep/misc {EM}/{ET}, created {C}, updated {U}, re-pointed {R}, rescued-versions {RV}, extras {EX}",
                dryRun ? "dry-run" : "write", r.MoviesMatched, r.MoviesTotal, r.EpMatched, r.EpTotal, r.Created, r.Updated, r.Repointed.Count, r.RescuedAlternateVersions, r.ExtrasAttached);
            return r;
        }

        // ── Per-movie "re-link files from disk" ──────────────────────────────────────────────────────
        // For when ONE movie's file is replaced on disk (new rip, old file deleted, folder renamed). The
        // whole-library sync only auto-repoints a moved file whose (name,size) is unchanged — a new rip
        // changes both, so it would go missing and need a hand fix. These two methods do the targeted
        // repair instead: every detail (rating/viewings/poster/IMDb cache/tags) lives on the Movie/Playable
        // row, so re-pointing the file row IN PLACE keeps all of it — nothing is deleted or recreated.

        /// <summary>
        /// Kicks a SCOPED Jellyfin re-scan of just this title's shelf (the alpha bucket above the movie
        /// folder) so a replaced/renamed file is indexed, without the full-library scan. Path-based: derives
        /// the shelf from the recorded path and posts it to Jellyfin's per-path "media updated" hook — no
        /// dependence on an existing item, IMDb provider id, or parent-id navigation (a homevideos library
        /// has no IMDb ids). Returns immediately; poll <see cref="TryRelinkMovieFilesAsync"/>.
        /// </summary>
        public async Task<MovieRelinkRefreshResult> TriggerMovieFolderRefreshAsync(int movieId, CancellationToken cancel = default)
        {
            var res = new MovieRelinkRefreshResult();
            if (config.JellyfinPathMappings.Count == 0) { res.Message = "No JellyfinPathMappings configured."; return res; }

            using var db = await dbFactory.CreateDbContextAsync(cancel);
            var movie = await db.Movies.FirstOrDefaultAsync(m => m.id == movieId, cancel);
            if (movie == null) { res.Message = "Movie not found."; return res; }
            if (movie.PlayableId == null) { res.Message = "This title has no playable file slot."; return res; }

            var primary = await db.MediaFiles
                .Where(f => f.PlayableId == movie.PlayableId.Value)
                .OrderBy(f => f.Role)   // Primary (0) first
                .FirstOrDefaultAsync(cancel);
            var recorded = primary?.Path ?? movie.FilePath;
            if (string.IsNullOrEmpty(recorded)) { res.Message = "No recorded path to locate this title's folder."; return res; }

            // Shelf = the bucket above the movie folder (e.g. ...\K). Re-scanning the bucket re-discovers a
            // RENAMED movie folder; scanning only the old folder would miss it (the folder no longer exists).
            var shelf = ParentDir(ParentDir(recorded));
            if (shelf == null) { res.Message = $"Couldn't determine a shelf folder from '{recorded}'."; return res; }
            var shelfNorm = JellyfinPathMapper.NormalizeForCompare(shelf);

            // Preferred trigger: resolve the shelf's Jellyfin FOLDER item and refresh it. A folder refresh
            // validates the folder's children — reliably indexing the new file and dropping the deleted one
            // (a per-path "media updated" hook is flakier across setups, so it's only the fallback).
            var shelfItemId = await ResolveShelfFolderIdAsync(shelfNorm, cancel);

            if (shelfItemId != null)
            {
                await jellyfin.RefreshItemAsync(shelfItemId, cancel);
                res.Ok = true;
                res.ShelfItemId = shelfItemId;
                res.Message = $"Re-scan started for shelf '{shelf}'.";
            }
            else if (JellyfinPathMapper.TryTranslateToJellyfin(shelf, config.JellyfinPathMappings, out var shelfJf))
            {
                await jellyfin.NotifyPathsUpdatedAsync(new[] { shelfJf }, cancel);
                res.Ok = true;
                res.Message = $"Re-scan requested for {shelfJf} (path hook — shelf folder not found in Jellyfin).";
            }
            else
            {
                var prefixes = string.Join(" | ", config.JellyfinPathMappings.Select(m => m.DbPrefix));
                res.Message = $"Couldn't find shelf '{shelf}' in Jellyfin and no path mapping covers it (DB prefixes: {prefixes}).";
                return res;
            }

            logger.LogInformation("Re-link: scoped re-scan for shelf {Shelf} (movie {Id} '{Title}', folderItem={Item})",
                shelf, movieId, movie.Title, shelfItemId ?? "(path-hook)");
            return res;
        }

        /// <summary>
        /// One idempotent probe: a cheap path-only enumeration (Jellyfin DB query, NOT a disk scan) restricted
        /// to this title's shelf. If a NEW untracked video has appeared there (the replaced rip), re-points the
        /// existing Primary <see cref="MediaFile"/> to it IN PLACE (refreshing item id, codec, size, duration;
        /// clearing MissingSinceUtc) and ingests any new sibling Extras in the same folder; otherwise reports
        /// <see cref="MovieRelinkResult.Scanning"/> so the caller polls again. Re-running after success is a
        /// no-op ("already linked"). The Movie row and everything attached to it are never touched.
        /// </summary>
        public async Task<MovieRelinkResult> TryRelinkMovieFilesAsync(int movieId, string? shelfItemId = null, CancellationToken cancel = default)
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
            var recorded = primary?.Path ?? movie.FilePath;
            if (string.IsNullOrEmpty(recorded)) { res.Message = "No recorded path to locate this title's folder."; return res; }
            res.OldPath = recorded;

            var shelf = ParentDir(ParentDir(recorded));
            if (shelf == null) { res.Message = "Couldn't determine this title's shelf folder."; return res; }
            var shelfNorm = JellyfinPathMapper.NormalizeForCompare(shelf);

            // Enumerate the shelf's videos via a ParentId-SCOPED query. This is essential, not just cheaper:
            // the global /Items?Recursive listing HIDES a file Jellyfin has grouped as an alternate "version"
            // (a re-rip sitting beside the old one gets a PrimaryVersionId and is excluded) — exactly the file
            // we're trying to find. A ParentId-scoped query reveals it. Resolve the shelf folder ourselves
            // when the trigger didn't hand us its id, so the probe never silently degrades to the global list.
            if (string.IsNullOrEmpty(shelfItemId))
                shelfItemId = await ResolveShelfFolderIdAsync(shelfNorm, cancel);
            if (string.IsNullOrEmpty(shelfItemId))
            {
                res.Scanning = true;
                res.Message = $"Couldn't find shelf '{shelf}' as a Jellyfin folder to scan — try the full Sync from Jellyfin.";
                return res;
            }
            var allItems = await jellyfin.GetVideoItemPathsUnderParentAsync(shelfItemId!, cancel);
            var relinkFamily = await FamilyExclusionAsync(cancel);
            var shelfItems = new List<(JellyfinItem Item, string DbPath)>();
            foreach (var it in allItems)
            {
                if (string.IsNullOrEmpty(it.Path)) continue;
                // A movie shelf can never be under the photo root, so this filter should never fire —
                // it is here because "should never" is not a guarantee, and this method WRITES a
                // MediaFile row (§2.3).
                if (relinkFamily.IsExcluded(it.Path)) continue;
                if (!JellyfinPathMapper.TryTranslateToDb(it.Path!, config.JellyfinPathMappings, out var dp, out _)) continue;
                var dn = JellyfinPathMapper.NormalizeForCompare(dp);
                if (dn == shelfNorm || dn.StartsWith(shelfNorm + "\\")) shelfItems.Add((it, dp));
            }

            // Untracked videos under the shelf (mapped to no MediaFile row anywhere) = candidate new files.
            // Deliberately NO "already linked" shortcut off the recorded path: a deleted file's stale Jellyfin
            // entry would satisfy it and falsely report success. Success comes ONLY from finding the new file.
            var mappedNorms = (await db.MediaFiles.Select(f => f.Path).ToListAsync(cancel))
                .Select(JellyfinPathMapper.NormalizeForCompare).ToHashSet();
            var untracked = shelfItems
                .Where(x => !mappedNorms.Contains(JellyfinPathMapper.NormalizeForCompare(x.DbPath)))
                .ToList();

            // Choose the new PRIMARY for this title: the untracked file whose containing folder best matches
            // the title (require a title-token overlap so we never grab an unrelated new file in the shelf;
            // a lone untracked file is taken only when the title gives us nothing to match on).
            var titleTokens = Tokens(movie.SimpleTitle ?? movie.Title ?? "");
            (JellyfinItem Item, string DbPath) chosen = default;
            bool found = false;
            if (untracked.Count > 0 && titleTokens.Count > 0)
            {
                var ranked = untracked
                    .Select(x => (x, score: TokenOverlap(titleTokens, Tokens(ParentDir(x.DbPath) ?? x.DbPath))))
                    .Where(t => t.score > 0)
                    .OrderByDescending(t => t.score).ToList();
                if (ranked.Count > 0) { chosen = ranked[0].x; found = true; }
            }
            else if (untracked.Count == 1) { chosen = untracked[0]; found = true; }

            if (!found)
            {
                res.Scanning = true;
                res.Message = $"No matching new file under shelf '{shelf}' yet (saw {shelfItems.Count} file(s), {untracked.Count} untracked). The re-scan may still be running.";
                return res;
            }

            // Pull the chosen item's full detail (MediaSources etc.) so the row gets codec/size/duration.
            var detail = (await jellyfin.GetItemsByIdsAsync(new[] { chosen.Item.Id }, cancel)).FirstOrDefault() ?? chosen.Item;
            var newPath = chosen.DbPath;
            var now = DateTime.UtcNow;

            if (primary == null)
            {
                primary = new MediaFile { PlayableId = playableId, Path = newPath, Role = MovieFileRole.Primary };
                db.MediaFiles.Add(primary);
            }
            else primary.Path = newPath;
            var relinkRestoreRows = new List<MediaFile>();
            var staleServerKeyframes = StampFromItem(primary, detail, now, relinkRestoreRows);
            if (staleServerKeyframes)
                await ReExtractStaleKeyframesAsync(new[] { primary }, now, cancel);
            if (relinkRestoreRows.Count > 0)
                await RestoreBankedKeyframesAsync(db, relinkRestoreRows, now, cancel);
            movie.FilePath = newPath;
            res.PrimaryRepointed = true;
            res.NewPath = newPath;
            res.NowStreamable = primary.JellyfinItemId != null;

            // Extras for this movie, both kinds — added as Role=Extra, never deleted:
            //  (a) videos in an extras-type subfolder of the new movie folder, per the on-disk CONVENTION
            //      ("Extras Content"/"Featurettes Content"/… — ExtrasClassifier, same as the ingest). Only
            //      these, NOT every sibling video, so an alt cut sitting beside the feature isn't mislabelled.
            //  (b) Jellyfin "special features" (a plain "Featurettes" rip Jellyfin hid) via SpecialFeatures.
            var attachedNorms = new HashSet<string>(files.Select(f => JellyfinPathMapper.NormalizeForCompare(f.Path)));
            attachedNorms.Add(JellyfinPathMapper.NormalizeForCompare(newPath));
            var newMovieFolder = ParentDir(newPath) ?? "";
            var newFolderNorm = JellyfinPathMapper.NormalizeForCompare(newMovieFolder);

            foreach (var x in untracked)
            {
                if (x.Item.Id == chosen.Item.Id) continue;
                var xFolder = JellyfinPathMapper.NormalizeForCompare(ParentDir(x.DbPath) ?? "");
                if (xFolder != newFolderNorm && !xFolder.StartsWith(newFolderNorm + "\\")) continue;   // only this movie's folder
                var rel = x.DbPath.Length > newMovieFolder.Length ? x.DbPath.Substring(newMovieFolder.Length) : x.DbPath;
                if (ExtrasClassifier.ExtraKeyword(rel) == null) continue;   // only files in an extras subfolder
                if (!attachedNorms.Add(JellyfinPathMapper.NormalizeForCompare(x.DbPath))) continue;
                var exDetail = (await jellyfin.GetItemsByIdsAsync(new[] { x.Item.Id }, cancel)).FirstOrDefault() ?? x.Item;
                db.MediaFiles.Add(NewExtraRow(playableId, x.DbPath, exDetail, now));
                res.ExtrasAdded.Add(LeafLabel(x.DbPath));
            }

            var specials = await jellyfin.GetSpecialFeaturesAsync(detail.Id, cancel);
            if (specials.Count > 0)
                foreach (var sd in await jellyfin.GetItemsByIdsAsync(specials.Select(s => s.Id), cancel))
                {
                    if (string.IsNullOrEmpty(sd.Path)) continue;
                    // Special features arrive from their OWN sweep — by item id, not from the shelf
                    // listing the loop above filtered — so the family exclusion has to be applied here
                    // too. This is the §2.3 trap in its exact shape: a family folder whose name collides
                    // with a reserved one ("Extras", "Featurettes"…) is what Jellyfin hands back as a
                    // special feature, and this branch WRITES a MediaFile row.
                    if (relinkFamily.IsExcluded(sd.Path)) continue;
                    if (!JellyfinPathMapper.TryTranslateToDb(sd.Path!, config.JellyfinPathMappings, out var sdp, out _)) continue;
                    if (!attachedNorms.Add(JellyfinPathMapper.NormalizeForCompare(sdp))) continue;
                    db.MediaFiles.Add(NewExtraRow(playableId, sdp, sd, now));
                    res.ExtrasAdded.Add(LeafLabel(sdp));
                }

            await db.SaveChangesAsync(cancel);
            res.Done = true;
            res.Message = $"Re-linked '{movie.Title}' to the new file.";
            logger.LogInformation("Re-linked movie {Id} '{Title}': {Old} → {New} (+{Extras} extras)",
                movieId, movie.Title, recorded, newPath, res.ExtrasAdded.Count);
            return res;
        }

        // ── Blast-radius guards ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether a would-be write touches an anomalous SHARE of what it was measured against — the one
        /// check both guards above share.
        ///
        /// <para>Deliberately outcome-shaped rather than cause-shaped: it does not care WHY the number is
        /// enormous, only that a sync whose normal answer is "a handful" has produced "most of the
        /// table". Below <see cref="MovieTheaterConfiguration.JellyfinSyncGuardMinRows"/> it never fires,
        /// because a fraction of a tiny library means nothing and a fresh install must stay syncable; a
        /// ceiling of 0 or less disables it, for the operator who really is retiring the catalogue.</para>
        /// </summary>
        private bool ExceedsWriteCeiling(int affected, int total)
        {
            var ceiling = config.JellyfinSyncMaxWriteFraction;
            if (ceiling <= 0) return false;
            if (total < Math.Max(1, config.JellyfinSyncGuardMinRows)) return false;
            return (double)affected / total > ceiling;
        }

        private static string Percent(int affected, int total) =>
            total <= 0 ? "n/a" : ((double)affected / total).ToString("P0", CultureInfo.InvariantCulture);

        // ── The family photo library's exclusion (docs/photos-plan.md §2.3) ──────────────────────────

        private JellyfinFamilyExclusion? familyExclusion;

        /// <summary>
        /// The family-library exclusion for this service instance, built once and reused.
        ///
        /// <para>The prefixes come from <c>PhotosLibraryDir</c> — a fact about the collection, available
        /// with no server round trip and correct before the Jellyfin library exists at all. When
        /// <c>PhotosJellyfinLibraryId</c> is ALSO set, that library's own locations are asked for and
        /// added; a failure to reach the server for that answer is logged and ignored rather than
        /// aborting the sync, because the path prefixes alone already satisfy §2.3 and a sync that
        /// refuses to run is not a safer outcome than one that excludes slightly less.</para>
        /// </summary>
        private async Task<JellyfinFamilyExclusion> FamilyExclusionAsync(CancellationToken cancel)
        {
            if (familyExclusion != null) return familyExclusion;

            List<string>? locations = null;
            var libraryId = config.PhotosJellyfinLibraryId;
            if (!string.IsNullOrWhiteSpace(libraryId))
            {
                try
                {
                    var folders = await jellyfin.GetVirtualFoldersAsync(cancel);
                    locations = folders
                        .Where(f => string.Equals(f.ItemId, libraryId, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(f => f.Locations)
                        .ToList();
                    if (locations.Count == 0)
                        logger.LogWarning("PhotosJellyfinLibraryId {Id} matched no Jellyfin library; the family exclusion " +
                                          "is running on PhotosLibraryDir alone", libraryId);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Could not read Jellyfin's library locations for the family exclusion; " +
                                         "falling back to the configured photo root");
                }
            }

            familyExclusion = JellyfinFamilyExclusion.Build(config.PhotosLibraryDir, config.JellyfinPathMappings, locations);
            if (familyExclusion.Configured)
                logger.LogInformation("Family photo library excluded from the movie sync by {Count} path prefix(es)",
                    familyExclusion.Prefixes.Count);
            return familyExclusion;
        }

        /// <summary>The Jellyfin folder item id whose on-disk path equals <paramref name="shelfNorm"/> (an
        /// already-normalized DB path), or null if none — found by listing folder items and translating each
        /// back to a DB path. Used to scope both the re-scan and the probe to one shelf.</summary>
        private async Task<string?> ResolveShelfFolderIdAsync(string shelfNorm, CancellationToken cancel)
        {
            foreach (var f in await jellyfin.GetFoldersAsync(cancel))
                if (!string.IsNullOrEmpty(f.Path)
                    && JellyfinPathMapper.TryTranslateToDb(f.Path!, config.JellyfinPathMappings, out var dp, out _)
                    && JellyfinPathMapper.NormalizeForCompare(dp) == shelfNorm)
                    return f.Id;
            return null;
        }

        /// <summary>A new Role=Extra MediaFile for a Jellyfin extra at <paramref name="dp"/> (DB path),
        /// labelled from its filename and stamped with the item's id/codec/size. Caller owns add + save.</summary>
        private static MediaFile NewExtraRow(int playableId, string dp, JellyfinItem item, DateTime now)
        {
            var row = new MediaFile { PlayableId = playableId, Path = dp, Role = MovieFileRole.Extra, Label = Truncate(LeafLabel(dp), 128) };
            StampFromItem(row, item, now);
            return row;
        }

        /// <summary>Filename without extension, from a Windows-style path (backslash split done MANUALLY —
        /// prod is Linux). Used as an extra's human label.</summary>
        private static string LeafLabel(string path)
        {
            var s = path.Replace('/', '\\').TrimEnd('\\');
            var name = s.Substring(s.LastIndexOf('\\') + 1);
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        /// <summary>Parent directory of a Windows-style path, split on backslash MANUALLY (prod is Linux, so
        /// Path.GetDirectoryName wouldn't split the DB's <c>\</c> separators). Null if there's no parent.</summary>
        private static string? ParentDir(string? p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            var s = p.Replace('/', '\\').TrimEnd('\\');
            var i = s.LastIndexOf('\\');
            return i <= 0 ? null : s.Substring(0, i);
        }

        /// <summary>Lower-case alphanumeric word tokens of a title/folder name, dropping articles, a 4-digit
        /// year, and common quality/source tags — so a folder matches its title on the meaningful words.</summary>
        private static HashSet<string> Tokens(string s)
        {
            var set = new HashSet<string>();
            foreach (var t in System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+"))
                if (t.Length >= 2 && !IsNoiseToken(t)) set.Add(t);
            return set;
        }

        private static bool IsNoiseToken(string t) =>
            t is "the" or "an" or "of" or "and"
            || (t.Length == 4 && int.TryParse(t, out var y) && y >= 1900 && y <= 2100)
            || t is "1080p" or "720p" or "2160p" or "480p" or "4k" or "bluray" or "brrip" or "webrip"
                or "web" or "hdtv" or "x264" or "x265" or "hevc" or "remux" or "dvdrip" or "proper";

        private static int TokenOverlap(HashSet<string> a, HashSet<string> b) => a.Count(b.Contains);

        /// <summary>
        /// Returns true when Jellyfin's SERVER-SIDE stored keyframe list for this item just went
        /// stale: the file's bytes were replaced under the SAME item id while the row carried a
        /// <c>JfKeyframesUtc</c> stamp. The caller must re-run keyframe extraction for those items —
        /// the patched Jellyfin builds exact per-keyframe copy playlists from that stored list, and a
        /// list describing the OLD encode reintroduces the very playlist/segment divergence (freezes)
        /// the whole mechanism exists to prevent. An item-id CHANGE is safe server-side (the new id
        /// has no stored list, so Jellyfin falls back to legacy segmentation) but still clears the
        /// stamp, since the stamp vouches for data the new item doesn't have.
        ///
        /// <para><b>Fingerprint semantics (keyframe custody, 2026-08-13).</b> A size change also nulls
        /// <c>ContentFingerprint</c> — a re-rip is different bytes, and a stale fingerprint would let a
        /// restore hand the new encode the OLD encode's keyframes, the silent-wrong-list case every
        /// check in this lane exists to refuse. An id change with the SAME size is the rename/move
        /// case: the bytes are untouched, so the row lands in <paramref name="restoreCandidates"/> and
        /// <see cref="RestoreBankedKeyframesAsync"/> re-imports the banked list onto the new item id —
        /// which is what makes a folder rename cost zero re-extraction and zero legacy-playback window.
        /// The old cascade-deleted server row is irrelevant by then; the master copy lives in
        /// <c>MediaKeyframes</c>, keyed by the bytes.</para>
        /// </summary>
        private static bool StampFromItem(MediaFile row, JellyfinItem item, DateTime now,
            List<MediaFile>? restoreCandidates = null)
        {
            var src = item.MediaSources?.FirstOrDefault();
            var vid = src?.MediaStreams?.FirstOrDefault(s => s.Type == "Video");
            var aud = src?.MediaStreams?.Where(s => s.Type == "Audio").OrderByDescending(s => s.IsDefault).FirstOrDefault();
            var idChanged = row.JellyfinItemId != null
                && !string.Equals(row.JellyfinItemId, item.Id, StringComparison.OrdinalIgnoreCase);
            // A changed size means the file on disk was replaced (a re-rip), so every keyframe
            // measurement describes an encode that no longer exists. Same-size updates keep them:
            // they are properties of the bytes, not of anything Jellyfin re-reports.
            var sizeChanged = row.SizeBytes != null && src?.Size != null && row.SizeBytes != src.Size;
            var serverKeyframesStale = sizeChanged && !idChanged && row.JfKeyframesUtc != null;
            if (sizeChanged || idChanged)
                row.JfKeyframesUtc = null;            // exact-copy authorization no longer holds
            if (sizeChanged)
                row.ContentFingerprint = null;        // different bytes; re-stamped by the next fingerprint run
            else if (idChanged && row.ContentFingerprint != null)
                restoreCandidates?.Add(row);
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
            return serverKeyframesStale;
        }

        /// <summary>
        /// Re-imports banked keyframe lists (<see cref="MediaKeyframes"/>) for rows a re-point just
        /// moved to a new item id with their bytes untouched. One cheap HTTP POST per row against the
        /// patch's <c>ImportKeyframes</c> endpoint — no ffprobe, no file reads — so even a whole-bucket
        /// rename (hundreds of files) restores in a couple of minutes inside the sync that noticed it.
        ///
        /// <para>Refusals are individually silent but collectively loud: a missing banked row means the
        /// fingerprint pass never covered the file (nightly re-extraction picks it up, exactly as
        /// before this lane existed); a SIZE mismatch against the banked row means the fingerprint is
        /// lying about the bytes and importing would be worse than re-measuring. Three consecutive
        /// 404s abort the pass — that is a stock (patch-wiped) Jellyfin answering, and every further
        /// call would 404 the same way.</para>
        /// </summary>
        private async Task<(int Restored, int NoBankedRow, int Skipped)> RestoreBankedKeyframesAsync(
            MovieDb db, IReadOnlyList<MediaFile> rows, DateTime now, CancellationToken cancel)
        {
            const int Cap = 1000;
            var work = rows.Take(Cap).ToList();
            if (rows.Count > Cap)
                logger.LogWarning(
                    "Keyframe restore cap ({Cap}) reached; {Left} re-pointed rows fall back to nightly re-extraction",
                    Cap, rows.Count - Cap);
            if (work.Count == 0) return (0, 0, rows.Count > Cap ? rows.Count - Cap : 0);

            var fingerprints = work.Select(r => r.ContentFingerprint!).Distinct().ToList();
            var banked = await db.MediaKeyframes.Where(k => fingerprints.Contains(k.Fingerprint))
                .ToDictionaryAsync(k => k.Fingerprint, cancel);

            int restored = 0, noRow = 0, skipped = 0, consecutive404 = 0;
            foreach (var row in work)
            {
                if (!banked.TryGetValue(row.ContentFingerprint!, out var keyframes))
                {
                    noRow++;
                    continue;
                }
                if (row.SizeBytes == null || keyframes.SizeBytes != row.SizeBytes)
                {
                    skipped++;
                    logger.LogWarning(
                        "Banked keyframes for MediaFile {Id} refused: size {RowSize} vs banked {BankedSize} (fingerprint {Fp}) — re-extraction will re-measure",
                        row.Id, row.SizeBytes, keyframes.SizeBytes, row.ContentFingerprint);
                    continue;
                }

                var outcome = await jellyfin.ImportKeyframesAsync(
                    row.JellyfinItemId!, keyframes.TotalDurationTicks, keyframes.KeyframeTicks, cancel);
                if (outcome.Ok)
                {
                    row.JfKeyframesUtc = now;
                    restored++;
                    consecutive404 = 0;
                }
                else if (outcome.StatusCode == 404 && ++consecutive404 >= 3)
                {
                    logger.LogWarning(
                        "Keyframe restore aborted after 3 consecutive 404s — the ImportKeyframes endpoint is absent " +
                        "(stock Jellyfin? a stock upgrade wipes the patch — see hls-copy-freeze). " +
                        "{Left} rows fall back to nightly re-extraction",
                        work.Count - restored - noRow - skipped);
                    break;
                }
                else
                {
                    skipped++;
                    logger.LogWarning(
                        "Keyframe import failed for MediaFile {Id} (item {ItemId}): {Status} {Error} — nightly re-extraction will cover it",
                        row.Id, row.JellyfinItemId, outcome.StatusCode, outcome.Error);
                }
            }

            if (restored > 0 || noRow > 0 || skipped > 0)
                logger.LogInformation(
                    "Keyframe restore after re-point: {Restored} restored from bank, {NoRow} unbanked (nightly re-extracts), {Skipped} refused/failed",
                    restored, noRow, skipped);
            return (restored, noRow, skipped);
        }

        /// <summary>
        /// Re-runs Jellyfin's full keyframe extraction for rows whose stored server-side list just
        /// went stale (see <see cref="StampFromItem"/>). The endpoint deletes the old list BEFORE
        /// extracting, so even a failed extraction leaves the server safe (legacy segmentation);
        /// only an unreachable server leaves a stale list behind — logged loudly, since that file
        /// would freeze on mid-file joins until re-extracted. Capped: replacements are rare, and a
        /// sync run must stay bounded; leftovers are listed for a manual pass.
        /// </summary>
        private async Task ReExtractStaleKeyframesAsync(IReadOnlyList<MediaFile> rows, DateTime now, CancellationToken cancel)
        {
            const int Cap = 25;
            foreach (var row in rows.Take(Cap))
            {
                var outcome = await jellyfin.ExtractKeyframesAsync(row.JellyfinItemId!, cancel);
                if (outcome.Ok)
                    row.JfKeyframesUtc = now;
                else
                    logger.LogWarning(
                        "Keyframe re-extraction after file replacement failed for MediaFile {Id} (item {ItemId}): {Status} {Error} — " +
                        "run extract-jellyfin-keyframes for it, its exact-copy playback is disabled until then",
                        row.Id, row.JellyfinItemId, outcome.StatusCode, outcome.Error);
            }

            foreach (var row in rows.Skip(Cap))
                logger.LogWarning(
                    "Keyframe re-extraction cap ({Cap}) reached; MediaFile {Id} (item {ItemId}) has a STALE server-side keyframe list — " +
                    "run extract-jellyfin-keyframes --playable-id {PlayableId} promptly",
                    Cap, row.Id, row.JellyfinItemId, row.PlayableId);
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

        /// <summary>Jellyfin items dropped because they belong to the FAMILY photo library (§2.3). Not a
        /// fault and not a gap — the number exists so the exclusion is visibly ON rather than assumed,
        /// and so a sudden zero after the family library is created is noticeable.</summary>
        public int FamilyItemsExcluded { get; set; }

        /// <summary>The path prefixes that exclusion is running on, printed by the CLI. An exclusion
        /// whose shape nobody can see is one nobody can tell is misconfigured.</summary>
        public List<string> FamilyExclusionPrefixes { get; } = new();

        /// <summary>Jellyfin extras (featurettes/deleted scenes/etc.) attached to their owner movie this run.</summary>
        public int ExtrasAttached { get; set; }
        /// <summary>Extras whose folder didn't map to any known movie folder — left unattached.</summary>
        public int ExtrasUnplaced { get; set; }
    }

    /// <summary>Result of <see cref="JellyfinSyncService.TriggerMovieFolderRefreshAsync"/> — did the scoped
    /// re-scan start, and why not if it didn't.</summary>
    public class MovieRelinkRefreshResult
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        /// <summary>The Jellyfin folder item id that was refreshed — threaded into the probe so it can poll
        /// just this shelf instead of re-listing the whole library. Null when the path-hook fallback was used.</summary>
        public string? ShelfItemId { get; set; }
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
