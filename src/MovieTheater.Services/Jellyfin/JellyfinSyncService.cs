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

        public async Task<JellyfinSyncReport> RunAsync(bool dryRun, CancellationToken cancel = default,
            Action<string>? progress = null)
        {
            var r = new JellyfinSyncReport { DryRun = dryRun };
            // Short human phase labels for whoever is watching the run (the background runner's
            // status endpoint) — a minutes-long job must be seen advancing, not spinning.
            void Step(string s) => progress?.Invoke(s);
            Step("contacting Jellyfin");

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
            Step("fetching the Jellyfin item list");
            var reported = await jellyfin.GetAllVideoItemsAsync(cancel);
            var items = family.Filter(reported, out var excludedItems);
            r.FamilyItemsExcluded += excludedItems;
            r.MovieItems = items.Count;
            // Breadcrumb logs at each phase so a run that dies mid-flight shows WHERE in the pod log,
            // instead of leaving one all-or-nothing summary line that only a finished run ever writes.
            logger.LogInformation("Jellyfin sync ({Mode}): fetched {N} items ({Fam} family-excluded) from {Server} {Version}",
                dryRun ? "dry-run" : "write", items.Count, excludedItems, r.ServerName, r.Version);

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
                .Select(m => new MovieLite(m.id, m.Title, m.FilePath, m.imdbID, m.PlayableId))
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
            // Structured twins of the report lines, kept for the candidate pass: an untracked item
            // sharing a movie's IMDb id is the strongest upgrade signal there is.
            var imdbFallbackPairs = new List<(int MovieId, JellyfinItem Item, string DbPath)>();
            int created = 0, updated = 0;
            var now = DateTime.UtcNow;

            Step($"matching {items.Count} items against {movies.Count} movies");
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
                        imdbFallbackPairs.Add((byId.id, item, dbPath));
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
            logger.LogInformation("Jellyfin sync: movie pass matched {M}/{T}", chosen.Count, movies.Count);
            Step($"movies matched {chosen.Count}/{movies.Count}; matching episodes and misc files");

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
            logger.LogInformation("Jellyfin sync: episode/part/misc pass matched {M}/{T}", r.EpMatched, r.EpTotal);
            Step($"episodes/misc matched {r.EpMatched}/{r.EpTotal}; detecting moved and renamed files");

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
            var possibleRenamePairs = new List<(MediaFile Row, JellyfinItem Item, string DbPath)>();
            foreach (var (size, row) in fileBySize)
                if (itemBySize.TryGetValue(size, out var u))
                {
                    r.PossibleRenames.Add($"{row.Path}  ↔  {u.DbPath}  (same size {size} B, name changed — review)");
                    possibleRenamePairs.Add((row, u.Item, u.DbPath));
                }

            // Whatever we didn't re-point (and isn't already reported as an IMDB-id fallback) is genuinely
            // a Jellyfin item the DB doesn't track.
            foreach (var u in translatedUntracked)
                if (!repointedItemIds.Contains(u.Item.Id) && !imdbFallbackItemIds.Contains(u.Item.Id))
                    r.Untracked.Add(u.Item.Path ?? u.DbPath);
            logger.LogInformation("Jellyfin sync: move detection re-pointed {R} ({Masked} from dead items), {PR} possible rename(s), {U} untracked",
                r.Repointed.Count, maskedRepoints, r.PossibleRenames.Count, r.Untracked.Count);
            Step($"re-pointed {r.Repointed.Count} moved file(s); placing extras and classifying candidates");

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

            // ── Sync candidates: persist what "untracked" actually means, so the review tool can act ──
            // Everything the passes above left untracked is classified (upgrade of an existing movie /
            // new title / unclassified) and upserted into SyncCandidate — the durable version of the
            // report sections, keyed by path so re-syncs refresh rather than duplicate, and a
            // rejection is remembered. Extras attached this run are excluded (they found their owner).
            var candidateItems = translatedUntracked
                .Where(u => !repointedItemIds.Contains(u.Item.Id)
                            && !imdbFallbackItemIds.Contains(u.Item.Id)
                            && !extraMapped.Contains(JellyfinPathMapper.NormalizeForCompare(u.DbPath)))
                .ToList();
            // Non-fatal by design: candidate persistence is an AUXILIARY product of the sync. A bug
            // here must degrade to "no candidates this run" — loudly — never to a failed sync whose
            // matching/missing work is all thrown away.
            try
            {
                await UpsertSyncCandidatesAsync(db, r, dryRun, now, candidateItems,
                    imdbFallbackPairs.Where(p => !chosen.ContainsKey(p.MovieId)).ToList(),
                    possibleRenamePairs, matchedRows, filesByPlayable, movieById.Values.ToList(),
                    movieIdByPlayable, folderToPlayable, chosen, cancel);
                logger.LogInformation(
                    "Jellyfin sync: candidates classified — {U} upgrade, {N} new-title, {E} episode file(s) in {G} show(s), {X} unclassified, {S} retired",
                    r.CandidateUpgrades, r.CandidateNewTitles, r.CandidateSeriesEpisodes, r.CandidateSeriesGroups,
                    r.CandidateUnclassified, r.CandidatesSuperseded);
            }
            catch (Exception ex) when (!cancel.IsCancellationRequested)
            {
                r.CandidateError = ex.Message;
                logger.LogError(ex, "Jellyfin sync: candidate classification failed — continuing without candidates this run");
                // Half-built candidate rows must not ride along with the sync's own save.
                foreach (var entry in db.ChangeTracker.Entries<SyncCandidate>().ToList())
                    entry.State = entry.State == EntityState.Added
                        ? EntityState.Detached
                        : entry.State == EntityState.Modified ? EntityState.Unchanged : entry.State;
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
                // The sync's own product (matches, re-points, stamps, extras, candidates) is saved FIRST.
                // The keyframe lanes come after: each call there is a Jellyfin round trip that can take
                // minutes per file, and losing the whole sync to a keyframe hiccup — the exact work this
                // run just finished — is strictly worse than a file temporarily playing via legacy
                // segmentation until the nightly re-extraction covers it.
                logger.LogInformation("Jellyfin sync: saving — {Stamp} newly missing, {Cand} candidate row change(s)",
                    wouldStamp.Count, db.ChangeTracker.Entries<SyncCandidate>().Count(e => e.State != EntityState.Unchanged));
                Step("saving results");
                await db.SaveChangesAsync(cancel);
                logger.LogInformation("Jellyfin sync: core save complete");
                Step("results saved; refreshing keyframe custody");

                try
                {
                    if (staleKeyframeRows.Count > 0)
                    {
                        logger.LogInformation("Jellyfin sync: re-extracting keyframes for {N} replaced file(s)", staleKeyframeRows.Count);
                        await ReExtractStaleKeyframesAsync(staleKeyframeRows, now, cancel);
                    }
                    if (restoreKeyframeRows.Count > 0)
                    {
                        logger.LogInformation("Jellyfin sync: restoring banked keyframes for {N} re-pointed file(s)", restoreKeyframeRows.Count);
                        await RestoreBankedKeyframesAsync(db, restoreKeyframeRows, now, cancel);
                    }
                    if (staleKeyframeRows.Count > 0 || restoreKeyframeRows.Count > 0)
                        await db.SaveChangesAsync(cancel);   // just the JfKeyframesUtc stamps
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested)
                {
                    r.KeyframeError = ex.Message;
                    logger.LogError(ex,
                        "Jellyfin sync: keyframe re-extract/restore failed AFTER the core save — sync results are intact; nightly re-extraction covers the affected files");
                }
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

        /// <summary>
        /// Applies one approved <see cref="SyncCandidateKind.Upgrade"/>: re-points the target movie's
        /// Primary <see cref="MediaFile"/> IN PLACE to the candidate's file — same repair the per-movie
        /// re-link does, driven from the persisted candidate instead of a shelf probe. The item is
        /// re-fetched and its path re-verified against the candidate first, so a file that moved after
        /// detection refuses instead of linking blind. Keyframe custody rides along (stale re-extract /
        /// banked restore), the movie's everything-else (ratings, viewings, posters) is untouched, and
        /// pending sibling candidates in an extras subfolder of the new movie folder are attached as
        /// Extras and retired. Never deletes a file or a row.
        /// </summary>
        public async Task<SyncUpgradeResult> ApplyUpgradeCandidateAsync(int candidateId, string? approvedBy, CancellationToken cancel = default)
        {
            var res = new SyncUpgradeResult();
            using var db = await dbFactory.CreateDbContextAsync(cancel);
            var cand = await db.SyncCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, cancel);
            if (cand == null) { res.Message = "Candidate not found."; return res; }
            if (cand.Status != SyncCandidateStatus.Pending) { res.Message = $"Candidate is {cand.Status}, not Pending."; return res; }
            if (cand.Kind != SyncCandidateKind.Upgrade || cand.TargetMovieId == null || string.IsNullOrEmpty(cand.JellyfinItemId))
            { res.Message = "Not an applicable upgrade candidate."; return res; }

            var movie = await db.Movies.FirstOrDefaultAsync(m => m.id == cand.TargetMovieId, cancel);
            if (movie == null) { res.Message = "The target movie no longer exists."; return res; }
            res.MovieTitle = movie.Title;

            // Re-fetch the item and require it to still be the file the candidate described.
            var detail = (await jellyfin.GetItemsByIdsAsync(new[] { cand.JellyfinItemId! }, cancel)).FirstOrDefault();
            if (detail == null || string.IsNullOrEmpty(detail.Path))
            { res.Message = "The file's Jellyfin item is gone — run Sync from Jellyfin and review again."; return res; }
            var family = await FamilyExclusionAsync(cancel);
            if (family.IsExcluded(detail.Path!)) { res.Message = "That file belongs to the excluded family library."; return res; }
            if (!JellyfinPathMapper.TryTranslateToDb(detail.Path!, config.JellyfinPathMappings, out var dbPath, out _))
            { res.Message = $"No path mapping covers '{detail.Path}'."; return res; }
            if (JellyfinPathMapper.NormalizeForCompare(dbPath) != JellyfinPathMapper.NormalizeForCompare(cand.Path))
            { res.Message = $"The file moved since detection (now at '{dbPath}') — run Sync from Jellyfin and review again."; return res; }

            // Refuse if some OTHER title claimed the path meanwhile (an approval race, a hand map).
            var pathNorm = JellyfinPathMapper.NormalizeForCompare(dbPath);
            var claimed = (await db.MediaFiles.Select(f => new { f.Path, f.PlayableId }).ToListAsync(cancel))
                .FirstOrDefault(f => JellyfinPathMapper.NormalizeForCompare(f.Path) == pathNorm && f.PlayableId != movie.PlayableId);
            if (claimed != null) { res.Message = "Another title already owns that file — nothing changed."; return res; }

            var now = DateTime.UtcNow;
            if (movie.PlayableId == null)
            {   // pre-cutover stragglers: give the movie its file slot rather than refusing the upgrade
                var playable = new Playable { Kind = PlayableKind.Movie };
                db.Playables.Add(playable);
                await db.SaveChangesAsync(cancel);
                movie.PlayableId = playable.Id;
            }
            var files = await db.MediaFiles.Where(f => f.PlayableId == movie.PlayableId!.Value).ToListAsync(cancel);
            var primary = files.FirstOrDefault(f => f.Role == MovieFileRole.Primary) ?? files.FirstOrDefault();
            if (primary == null)
            {
                primary = new MediaFile { PlayableId = movie.PlayableId!.Value, Path = dbPath, Role = MovieFileRole.Primary };
                db.MediaFiles.Add(primary);
            }
            else primary.Path = dbPath;
            var restoreRows = new List<MediaFile>();
            if (StampFromItem(primary, detail, now, restoreRows))
                await ReExtractStaleKeyframesAsync(new[] { primary }, now, cancel);
            if (restoreRows.Count > 0)
                await RestoreBankedKeyframesAsync(db, restoreRows, now, cancel);
            movie.FilePath = dbPath;
            res.NewPath = dbPath;

            // Pending sibling candidates under the new movie folder ride along with the approval:
            // files in an extras-type subfolder become Extras, and cdN/partN files DIRECTLY in the
            // movie folder become Parts — a multi-disc upgrade must land whole, not as the one file
            // this candidate happened to describe (its discs would otherwise resurface as competing
            // upgrade candidates of the same movie). Anything else is left alone.
            var newFolder = ParentDir(dbPath);
            if (newFolder != null)
            {
                var folderNorm = JellyfinPathMapper.NormalizeForCompare(newFolder);
                var attachedNorms = files.Select(f => JellyfinPathMapper.NormalizeForCompare(f.Path)).ToHashSet();
                attachedNorms.Add(pathNorm);
                var siblings = (await db.SyncCandidates.Where(c => c.Status == SyncCandidateStatus.Pending && c.Id != cand.Id).ToListAsync(cancel))
                    .Where(c => !string.IsNullOrEmpty(c.JellyfinItemId)
                                && JellyfinPathMapper.NormalizeForCompare(c.Path).StartsWith(folderNorm + "\\")
                                && !attachedNorms.Contains(JellyfinPathMapper.NormalizeForCompare(c.Path)))
                    .ToList();
                if (siblings.Count > 0)
                {
                    var sibDetails = (await jellyfin.GetItemsByIdsAsync(siblings.Select(s => s.JellyfinItemId!), cancel))
                        .ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
                    foreach (var s in siblings)
                    {
                        if (!sibDetails.TryGetValue(s.JellyfinItemId!, out var sd)) continue;
                        var sNorm = JellyfinPathMapper.NormalizeForCompare(s.Path);
                        var isExtra = ExtrasClassifier.ExtraKeyword(s.Path.Substring(newFolder.Length)) != null;
                        var partNo = !isExtra && JellyfinPathMapper.NormalizeForCompare(ParentDir(s.Path) ?? "") == folderNorm
                            ? PartNumberOf(LeafLabel(s.Path))
                            : null;
                        if (!isExtra && partNo == null) continue;   // a sample/alt-cut — never guess
                        if (!attachedNorms.Add(sNorm)) continue;
                        if (isExtra)
                        {
                            db.MediaFiles.Add(NewExtraRow(movie.PlayableId!.Value, s.Path, sd, now));
                            res.ExtrasAttached.Add(LeafLabel(s.Path));
                        }
                        else
                        {
                            var partRow = new MediaFile
                            {
                                PlayableId = movie.PlayableId!.Value, Path = s.Path,
                                Role = MovieFileRole.Part, PartNumber = partNo,
                            };
                            StampFromItem(partRow, sd, now);
                            db.MediaFiles.Add(partRow);
                            res.PartsAttached.Add(LeafLabel(s.Path));
                        }
                        s.Status = SyncCandidateStatus.Superseded;
                        s.ResolvedUtc = now;
                    }
                }
            }

            cand.Status = SyncCandidateStatus.Approved;
            cand.ResolvedUtc = now;
            cand.ResolvedBy = Truncate(approvedBy, 64);
            await db.SaveChangesAsync(cancel);

            res.Ok = true;
            res.NowStreamable = primary.JellyfinItemId != null;
            res.Message = $"'{movie.Title}' re-pointed to the new file.";
            logger.LogInformation("Sync-candidate upgrade applied: movie {Id} '{Title}' → {Path} (candidate {Cand}, {Signal}, by {User}, +{Extras} extras)",
                movie.id, movie.Title, dbPath, cand.Id, cand.Signal, approvedBy, res.ExtrasAttached.Count);
            return res;
        }

        /// <summary>Slim projection of a movie row for the sync passes; property names deliberately
        /// mirror the entity's so call sites read identically.</summary>
        private sealed record MovieLite(int id, string? Title, string? FilePath, string? imdbID, int? PlayableId);

        /// <summary>A classified untracked file on its way into <see cref="SyncCandidate"/>.</summary>
        private sealed class CandidateDraft
        {
            public SyncCandidateKind Kind;
            public string DbPath = default!;
            public string ItemId = default!;
            public long? Size;
            public int? TargetMovieId;
            public string? Signal;
            public string? OldPath;
            public string? ParsedTitle;
            public int? ParsedYear;
            // Episodic (SeriesEpisode) only: the folder every episode of one show shares, the series it
            // was attributed to, and which episode the file name says it is.
            public string? SeriesFolder;
            public int? TargetSeriesId;
            public int? SeasonNumber;
            public int? EpisodeNumber;
            public int? SpansToEpisode;
        }

        /// <summary>The series folder a file belongs to — see
        /// <see cref="MovieFolderParser.SeriesRootOf"/>, which owns the climb (and its tests).</summary>
        private static string? SeriesRootOf(string? filePath) => MovieFolderParser.SeriesRootOf(filePath);

        /// <summary>
        /// Classifies every file the sync left untracked and upserts the result into
        /// <see cref="SyncCandidate"/> so the review tool can approve upgrades and ingest new titles
        /// instead of reading about them in a report. Signals, strongest first: a shared IMDb id, a
        /// unique same-size pair (the rename the sync refuses to auto-apply), the file sitting in an
        /// existing movie's own folder, a title-token match against a movie whose file just went
        /// missing. What remains parses as "Title (Year)" → NewTitle, or stays Unclassified (episodic
        /// files are always Unclassified — series belong to the mapping pipeline). Rows are keyed by
        /// path: re-syncs refresh Pending rows, reopen Superseded ones that reappear, never touch
        /// Rejected/Approved/Ingested (a rejection is a decision, not a cache), and Pending rows whose
        /// file stopped being untracked are marked Superseded. Nothing here writes in dry-run; the
        /// caller's single SaveChanges persists everything or (on an aborted run) nothing.
        /// </summary>
        private async Task UpsertSyncCandidatesAsync(
            MovieDb db, JellyfinSyncReport r, bool dryRun, DateTime now,
            List<(JellyfinItem Item, string DbPath, long? Size)> untracked,
            List<(int MovieId, JellyfinItem Item, string DbPath)> imdbPairs,
            List<(MediaFile Row, JellyfinItem Item, string DbPath)> renamePairs,
            HashSet<MediaFile> matchedRows,
            ILookup<int, MediaFile> filesByPlayable,
            List<MovieLite> movies,
            Dictionary<int, int> movieIdByPlayable,
            Dictionary<string, int> folderToPlayable,
            Dictionary<int, (JellyfinItem Item, int MappingIndex)> chosen,
            CancellationToken cancel)
        {
            var movieId2Movie = movies.ToDictionary(m => m.id);
            var drafts = new Dictionary<string, CandidateDraft>();   // norm path → first (strongest) claim

            void Claim(string dbPath, JellyfinItem item, long? size, SyncCandidateKind kind,
                int? target = null, string? signal = null, string? oldPath = null,
                string? parsedTitle = null, int? parsedYear = null,
                string? seriesFolder = null, int? targetSeriesId = null,
                int? season = null, int? episode = null, int? spansTo = null)
            {
                var norm = JellyfinPathMapper.NormalizeForCompare(dbPath);
                if (drafts.ContainsKey(norm)) return;
                // Every candidate gets a best-effort folder parse, upgrades included — it's what the
                // reviewer's "not an upgrade → new title" flip falls back on for a title.
                if (parsedTitle == null)
                {
                    var p = MovieFolderParser.Parse(LeafLabel(ParentDir(dbPath) ?? dbPath));
                    if (p != null) { parsedTitle = p.Value.Title; parsedYear ??= p.Value.Year; }
                }
                drafts[norm] = new CandidateDraft
                {
                    Kind = kind, DbPath = dbPath, ItemId = item.Id, Size = size,
                    TargetMovieId = target, Signal = signal, OldPath = oldPath,
                    ParsedTitle = parsedTitle, ParsedYear = parsedYear,
                    SeriesFolder = seriesFolder, TargetSeriesId = targetSeriesId,
                    SeasonNumber = season, EpisodeNumber = episode, SpansToEpisode = spansTo,
                };
            }

            // 0. Anything that is not a video container is claimed FIRST, as unclassified. Jellyfin
            // enumerates a DVD rip's .ifo/.bup sidecars as items, and the "same-folder" signal below
            // happily offered one as an upgrade of the movie whose .avi sits beside it — approving that
            // re-points a working title at an index file. Claiming first is what makes the guard total:
            // every later step short-circuits on an already-claimed path.
            foreach (var u in untracked)
                if (!MovieFolderParser.IsVideoFile(u.DbPath))
                    Claim(u.DbPath, u.Item, u.Size, SyncCandidateKind.Unclassified, signal: "not-video",
                        parsedTitle: LeafLabel(u.DbPath));

            // 1. Shared IMDb id — the item names the movie it replaces.
            foreach (var p in imdbPairs)
                if (movieId2Movie.TryGetValue(p.MovieId, out var m))
                    Claim(p.DbPath, p.Item, p.Item.MediaSources?.FirstOrDefault()?.Size,
                        SyncCandidateKind.Upgrade, p.MovieId, "imdb-id", m.FilePath);

            // 2. Unique same-size pair whose dead row is a movie's Primary.
            foreach (var (row, item, dbPath) in renamePairs)
                if (row.Role == MovieFileRole.Primary && movieIdByPlayable.TryGetValue(row.PlayableId, out var mid))
                    Claim(dbPath, item, row.SizeBytes, SyncCandidateKind.Upgrade, mid, "same-size", row.Path);

            // 3. The file sits directly in a folder the DB already knows as some movie's folder.
            foreach (var u in untracked)
            {
                var folder = ParentDir(u.DbPath);
                if (folder != null
                    && folderToPlayable.TryGetValue(JellyfinPathMapper.NormalizeForCompare(folder), out var pid)
                    && movieIdByPlayable.TryGetValue(pid, out var mid))
                {
                    var primary = filesByPlayable[pid].FirstOrDefault(f => f.Role == MovieFileRole.Primary);
                    Claim(u.DbPath, u.Item, u.Size, SyncCandidateKind.Upgrade, mid, "same-folder",
                        primary?.Path ?? movieId2Movie[mid].FilePath);
                }
            }

            // 4. Title tokens of the file's folder against movies whose files all went missing this
            // run (replaced rip in a renamed folder: no path, size or id survives — only the title).
            // ≥2 shared meaningful tokens (or total match for a 1-token title), and a UNIQUE best —
            // "Breakin'" must never claim the "Breakin' 2" rip on a tie.
            var missingMovies = movies
                .Where(m => !chosen.ContainsKey(m.id)
                            && (m.PlayableId == null || filesByPlayable[m.PlayableId.Value].All(f => !matchedRows.Contains(f))))
                .Select(m => (Movie: m, Toks: Tokens(m.Title ?? "")))
                .Where(t => t.Toks.Count > 0)
                .ToList();
            foreach (var u in untracked)
            {
                if (drafts.ContainsKey(JellyfinPathMapper.NormalizeForCompare(u.DbPath))) continue;
                var folderLeaf = LeafLabel(ParentDir(u.DbPath) ?? u.DbPath);
                var folderToks = Tokens(folderLeaf);
                if (folderToks.Count == 0) continue;
                var scored = missingMovies
                    .Select(t => (t.Movie, Score: TokenOverlap(t.Toks, folderToks), Need: t.Toks.Count))
                    .Where(t => t.Score >= 2 || (t.Score >= 1 && t.Need == 1))
                    .OrderByDescending(t => t.Score).ToList();
                if (scored.Count == 0 || (scored.Count > 1 && scored[1].Score == scored[0].Score)) continue;
                Claim(u.DbPath, u.Item, u.Size, SyncCandidateKind.Upgrade,
                    scored[0].Movie.id, "title-match", scored[0].Movie.FilePath);
            }

            // 5. Episode files. These are NOT one candidate each — 84 loose lines for one show is a
            // report, not a review queue. Every episodic file is stamped with the folder its whole
            // series shares (SeriesRootOf) plus its own SxxExx, so the review tool can fold them into a
            // single card; attribution to an existing Series happens here, while the sync already holds
            // the evidence. Strongest first: the folder ALREADY holds mapped episodes of exactly one
            // series, the folder is a series' recorded ReviewSourcePath, then a unique title-token match.
            var episodic = untracked
                .Where(u => !drafts.ContainsKey(JellyfinPathMapper.NormalizeForCompare(u.DbPath)))
                .Select(u => (u.Item, u.DbPath, u.Size, Ep: MovieFolderParser.ParseEpisode(LeafLabel(u.DbPath))))
                .Where(u => u.Ep != null)
                .ToList();
            if (episodic.Count > 0)
            {
                // Where each series' ALREADY-mapped episode files live, keyed by the same climb the
                // candidates use — an exact key match, never a path-prefix scan over 17k rows.
                var mappedRoots = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
                var mapped = await (from f in db.MediaFiles
                                    join e in db.Episodes on f.PlayableId equals e.PlayableId
                                    where e.SeriesId != null
                                    select new { f.Path, SeriesId = e.SeriesId!.Value }).ToListAsync(cancel);
                foreach (var mf in mapped)
                {
                    var root = SeriesRootOf(mf.Path);
                    if (root == null) continue;
                    var key = JellyfinPathMapper.NormalizeForCompare(root);
                    if (!mappedRoots.TryGetValue(key, out var set)) mappedRoots[key] = set = new HashSet<int>();
                    set.Add(mf.SeriesId);
                }

                var allSeries = await db.Series
                    .Select(s => new { s.Id, s.Title, s.ReviewSourcePath })
                    .ToListAsync(cancel);
                var byReviewPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in allSeries)
                    if (!string.IsNullOrEmpty(s.ReviewSourcePath))
                        byReviewPath.TryAdd(JellyfinPathMapper.NormalizeForCompare(s.ReviewSourcePath), s.Id);
                var seriesToks = allSeries
                    .Select(s => (s.Id, s.Title, Toks: Tokens(s.Title ?? "")))
                    .Where(s => s.Toks.Count > 0).ToList();

                // One attribution decision per FOLDER, not per file: every episode of a show must land
                // on the same series or the card fragments.
                var rootAttrib = new Dictionary<string, (int? SeriesId, string Signal)>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in episodic.GroupBy(u => JellyfinPathMapper.NormalizeForCompare(SeriesRootOf(u.DbPath) ?? u.DbPath)))
                {
                    if (rootAttrib.ContainsKey(group.Key)) continue;
                    var rootPath = SeriesRootOf(group.First().DbPath) ?? group.First().DbPath;
                    var leaf = MovieFolderParser.SeriesFolderLeaf(rootPath);

                    if (mappedRoots.TryGetValue(group.Key, out var owners) && owners.Count == 1)
                    { rootAttrib[group.Key] = (owners.First(), "series-folder"); continue; }
                    if (owners != null && owners.Count > 1)
                    { rootAttrib[group.Key] = (null, "series-ambiguous"); continue; }
                    if (byReviewPath.TryGetValue(group.Key, out var byPath))
                    { rootAttrib[group.Key] = (byPath, "series-source-path"); continue; }

                    var folderToks = Tokens(MovieFolderParser.ParseSeriesFolder(leaf)?.Title ?? leaf);
                    var scored = seriesToks
                        .Select(t => (t.Id, Score: TokenOverlap(t.Toks, folderToks), Need: t.Toks.Count))
                        .Where(t => t.Score >= 2 || (t.Score >= 1 && t.Need == 1))
                        .OrderByDescending(t => t.Score).ToList();
                    rootAttrib[group.Key] = (scored.Count == 1 || (scored.Count > 1 && scored[1].Score < scored[0].Score))
                        ? (scored[0].Id, "series-title-match")
                        : (null, "series-new");
                }

                foreach (var u in episodic)
                {
                    var rootPath = SeriesRootOf(u.DbPath) ?? u.DbPath;
                    var key = JellyfinPathMapper.NormalizeForCompare(rootPath);
                    var (sid, signal) = rootAttrib.TryGetValue(key, out var a) ? a : (null, "series-new");
                    var parsedFolder = MovieFolderParser.ParseSeriesFolder(MovieFolderParser.SeriesFolderLeaf(rootPath));
                    Claim(u.DbPath, u.Item, u.Size, SyncCandidateKind.SeriesEpisode,
                        signal: signal,
                        parsedTitle: parsedFolder?.Title ?? MovieFolderParser.SeriesFolderLeaf(rootPath),
                        parsedYear: parsedFolder?.Year,
                        seriesFolder: rootPath, targetSeriesId: sid,
                        season: u.Ep!.Value.Season, episode: u.Ep.Value.Episode,
                        spansTo: u.Ep.Value.Spans != u.Ep.Value.Episode ? u.Ep.Value.Spans : null);
                }
            }

            // 6. What's left is either a new movie (folder parses as "Title (Year)") or unclassified.
            foreach (var u in untracked)
            {
                if (drafts.ContainsKey(JellyfinPathMapper.NormalizeForCompare(u.DbPath))) continue;
                var folderLeaf = LeafLabel(ParentDir(u.DbPath) ?? u.DbPath);
                var parsed = MovieFolderParser.Parse(folderLeaf);
                if (parsed != null)
                    Claim(u.DbPath, u.Item, u.Size, SyncCandidateKind.NewTitle,
                        parsedTitle: parsed.Value.Title, parsedYear: parsed.Value.Year);
                else
                    Claim(u.DbPath, u.Item, u.Size, SyncCandidateKind.Unclassified, parsedTitle: folderLeaf);
            }

            r.CandidateUpgrades = drafts.Values.Count(d => d.Kind == SyncCandidateKind.Upgrade);
            r.CandidateNewTitles = drafts.Values.Count(d => d.Kind == SyncCandidateKind.NewTitle);
            r.CandidateUnclassified = drafts.Values.Count(d => d.Kind == SyncCandidateKind.Unclassified);
            r.CandidateSeriesEpisodes = drafts.Values.Count(d => d.Kind == SyncCandidateKind.SeriesEpisode);
            r.CandidateSeriesGroups = drafts.Values.Where(d => d.Kind == SyncCandidateKind.SeriesEpisode)
                .Select(d => d.SeriesFolder ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
            foreach (var d in drafts.Values.Where(d => d.Kind != SyncCandidateKind.SeriesEpisode))
                r.CandidateLines.Add(d.Kind switch
                {
                    SyncCandidateKind.Upgrade =>
                        $"upgrade: {d.DbPath} → movie {d.TargetMovieId} '{(d.TargetMovieId != null && movieId2Movie.TryGetValue(d.TargetMovieId.Value, out var m) ? m.Title : "?")}' ({d.Signal})",
                    SyncCandidateKind.NewTitle => $"new: {d.DbPath} → '{d.ParsedTitle}' ({d.ParsedYear})",
                    _ => $"unclassified: {d.DbPath}" + (d.Signal != null ? $" ({d.Signal})" : ""),
                });
            // One line per SHOW, not per episode — the whole point of the grouping.
            foreach (var g in drafts.Values.Where(d => d.Kind == SyncCandidateKind.SeriesEpisode)
                         .GroupBy(d => d.SeriesFolder ?? "", StringComparer.OrdinalIgnoreCase))
                r.CandidateLines.Add(
                    $"series: {g.Key} → {(g.First().TargetSeriesId != null ? $"series {g.First().TargetSeriesId}" : $"NEW '{g.First().ParsedTitle}'")} " +
                    $"({g.Count()} episode file(s), {g.Select(d => d.SeasonNumber).Distinct().Count()} season(s), {g.First().Signal})");

            if (dryRun) return;

            var existing = await db.SyncCandidates.ToListAsync(cancel);
            var existingByNorm = new Dictionary<string, SyncCandidate>();
            foreach (var row in existing)
                existingByNorm.TryAdd(JellyfinPathMapper.NormalizeForCompare(row.Path), row);

            foreach (var (norm, d) in drafts)
            {
                if (existingByNorm.TryGetValue(norm, out var row))
                {
                    row.LastSeenUtc = now;
                    row.JellyfinItemId = d.ItemId;
                    if (d.Size != null) row.SizeBytes = d.Size;
                    if (row.Status == SyncCandidateStatus.Superseded)
                    {   // the file is untracked again — the dismissal described a state that no longer holds
                        row.Status = SyncCandidateStatus.Pending;
                        row.ResolvedUtc = null;
                        row.ResolvedBy = null;
                    }
                    // A reviewer's hand corrections (pin/retitle/reclassify) outrank the machine's
                    // re-classification — re-deriving the same wrong answer must not undo the fix.
                    if (row.Status == SyncCandidateStatus.Pending && !row.PinnedByReviewer)
                    {
                        row.Kind = d.Kind;
                        row.TargetMovieId = d.TargetMovieId;
                        row.Signal = d.Signal;
                        row.OldPath = Truncate(d.OldPath, 1024);
                        row.ParsedTitle = Truncate(d.ParsedTitle, 512);
                        row.ParsedYear = d.ParsedYear;
                        row.SeriesFolder = Truncate(d.SeriesFolder, 1024);
                        row.SeasonNumber = d.SeasonNumber;
                        row.EpisodeNumber = d.EpisodeNumber;
                        row.SpansToEpisode = d.SpansToEpisode;
                        // A series this candidate was already resolved into outranks re-attribution: the
                        // refresh must not un-point an episode from the series a previous Resolve created
                        // for it just because the title-token guess now reads differently.
                        row.TargetSeriesId ??= d.TargetSeriesId;
                    }
                }
                else
                {
                    db.SyncCandidates.Add(new SyncCandidate
                    {
                        Kind = d.Kind,
                        Status = SyncCandidateStatus.Pending,
                        Path = Truncate(d.DbPath, 1024)!,
                        JellyfinItemId = d.ItemId,
                        SizeBytes = d.Size,
                        TargetMovieId = d.TargetMovieId,
                        Signal = d.Signal,
                        OldPath = Truncate(d.OldPath, 1024),
                        ParsedTitle = Truncate(d.ParsedTitle, 512),
                        ParsedYear = d.ParsedYear,
                        SeriesFolder = Truncate(d.SeriesFolder, 1024),
                        TargetSeriesId = d.TargetSeriesId,
                        SeasonNumber = d.SeasonNumber,
                        EpisodeNumber = d.EpisodeNumber,
                        SpansToEpisode = d.SpansToEpisode,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    });
                }
            }

            // A Pending row the sync no longer finds untracked either got mapped (this run's matching,
            // an approval, a hand fix) or its file is gone — both mean the offer is stale.
            foreach (var row in existing)
                if (row.Status == SyncCandidateStatus.Pending
                    && !drafts.ContainsKey(JellyfinPathMapper.NormalizeForCompare(row.Path)))
                {
                    row.Status = SyncCandidateStatus.Superseded;
                    row.ResolvedUtc = now;
                    r.CandidatesSuperseded++;
                }
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

        private static readonly System.Text.RegularExpressions.Regex PartRx =
            new(@"(?i)\b(?:cd|disc|disk|part|pt)\s*0*(\d{1,2})\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Disc/part ordinal from a filename ("Title cd2" → 2), or null when it isn't one.</summary>
        private static int? PartNumberOf(string fileName)
        {
            var m = PartRx.Match(fileName);
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }

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

        /// <summary>Set when candidate classification failed (non-fatally — the sync itself completed).</summary>
        public string? CandidateError { get; set; }
        /// <summary>Set when the post-save keyframe re-extract/restore failed (sync results intact).</summary>
        public string? KeyframeError { get; set; }

        // ── Sync candidates (the durable, actionable form of Untracked/PossibleRenames) ──
        /// <summary>Untracked files classified as an upgrade/replacement of an existing movie.</summary>
        public int CandidateUpgrades { get; set; }
        /// <summary>Untracked files whose folder parses as a movie the library doesn't have.</summary>
        public int CandidateNewTitles { get; set; }
        /// <summary>Untracked files the classifier couldn't place (unparseable folder, non-video sidecar).</summary>
        public int CandidateUnclassified { get; set; }

        /// <summary>What went wrong in the scan phase, when the run did one and it misbehaved. Not an
        /// abort — an unreachable or wedged scan still leaves a library worth syncing — but the
        /// reviewer has to know the sync saw a stale index rather than assume it saw the disk.</summary>
        public string? ScanNote { get; set; }

        /// <summary>Untracked EPISODE files (SxxExx), stamped with their series folder + episode number.</summary>
        public int CandidateSeriesEpisodes { get; set; }

        /// <summary>How many distinct SHOWS those episode files represent — the number of review cards
        /// they will fold into, which is the figure worth reading (84 files, 1 card).</summary>
        public int CandidateSeriesGroups { get; set; }
        /// <summary>Previously-pending candidates retired this run (file got mapped or vanished).</summary>
        public int CandidatesSuperseded { get; set; }
        /// <summary>One line per classified candidate, for the CLI report.</summary>
        public List<string> CandidateLines { get; } = new();
    }

    /// <summary>Result of <see cref="JellyfinSyncService.ApplyUpgradeCandidateAsync"/> — either the movie
    /// now points at the new file, or <see cref="Message"/> says why nothing was changed.</summary>
    public class SyncUpgradeResult
    {
        public bool Ok { get; set; }
        public bool NowStreamable { get; set; }
        public string? MovieTitle { get; set; }
        public string? NewPath { get; set; }
        public string? Message { get; set; }
        public List<string> ExtrasAttached { get; } = new();
        /// <summary>cdN/partN sibling files attached as Parts alongside the re-pointed Primary.</summary>
        public List<string> PartsAttached { get; } = new();
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
