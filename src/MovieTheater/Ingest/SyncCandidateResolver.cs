using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Normalization;
using MovieTheater.Services;
using MovieTheater.Services.Google;
using MovieTheater.Services.Jellyfin;
using MovieTheater.Services.Omdb;
using MovieTheater.Services.Poster;
using MovieTheater.Services.Series;
using MovieTheater.Services.Tmdb;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Turns the sync's raw findings into finished review cards: resolves new-title candidates into
    /// quarantined Movie rows, and folders of episode files into identified, enriched, episode-listed
    /// Series with their files attached.
    ///
    /// <para>It lives outside the controller because it is not a request. The sync job runs it
    /// server-side the moment classification finishes, so "run the sync" produces a review queue
    /// rather than a pile of candidates waiting for someone to press two more buttons — the whole
    /// point of the operation is the queue, and leaving the last mile behind a manual step meant the
    /// work sat unfinished and looked like a bug. The endpoints remain for re-running a piece by
    /// hand after a correction.</para>
    ///
    /// <para>Every method here is chunked and idempotent for the same reason the job is: progress is
    /// durable in the database, so an interrupted run continues rather than restarting.</para>
    /// </summary>
    public class SyncCandidateResolver : ISyncCandidateResolver
    {
        private readonly MovieDb movieDb;
        private readonly OmdbApi omdb;
        private readonly TmdbApi tmdb;
        private readonly IMDBApiService imdbApiService;
        private readonly GoogleSearchService googleSearchService;
        private readonly JellyfinApi jellyfinApi;
        private readonly PosterFetchService posterFetchService;
        private readonly TitleEnrichService titleEnrichService;
        private readonly SeriesEpisodeCatalog episodeCatalog;
        private readonly ILogger<SyncCandidateResolver> logger;

        public SyncCandidateResolver(MovieDb movieDb, OmdbApi omdb, TmdbApi tmdb,
            IMDBApiService imdbApiService, GoogleSearchService googleSearchService,
            JellyfinApi jellyfinApi, PosterFetchService posterFetchService,
            TitleEnrichService titleEnrichService, SeriesEpisodeCatalog episodeCatalog,
            ILogger<SyncCandidateResolver> logger)
        {
            this.movieDb = movieDb;
            this.omdb = omdb;
            this.tmdb = tmdb;
            this.imdbApiService = imdbApiService;
            this.googleSearchService = googleSearchService;
            this.jellyfinApi = jellyfinApi;
            this.posterFetchService = posterFetchService;
            this.titleEnrichService = titleEnrichService;
            this.episodeCatalog = episodeCatalog;
            this.logger = logger;
        }

        /// <summary>Bound a string to a column's MaxLength — the write that records a failure must
        /// never itself fail on 'string or binary data would be truncated'.</summary>
        private static string? TruncCol(string? s, int max) => s != null && s.Length > max ? s.Substring(0, max) : s;

        public static bool IsValidImdbId(string? input) =>
            !string.IsNullOrWhiteSpace(input) && System.Text.RegularExpressions.Regex.IsMatch(input.Trim(), @"^tt\d{7,8}$");

        // Copy every shared scalar column (string / value-type) from one title entity to another
        // (Movie ⇄ Series). Skips keys and the NotMapped PosterLink passthrough, so it auto-carries
        // new metadata columns as the schema grows.
        private static readonly HashSet<string> TitleScalarSkip = new(StringComparer.Ordinal) { "id", "Id", "PosterLink" };
        private static void CopyTitleScalars(object src, object dst)
        {
            var srcType = src.GetType();
            foreach (var dp in dst.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!dp.CanWrite || TitleScalarSkip.Contains(dp.Name)) continue;
                if (!(dp.PropertyType == typeof(string) || dp.PropertyType.IsValueType)) continue;
                var sp = srcType.GetProperty(dp.Name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (sp == null || !sp.CanRead || sp.PropertyType != dp.PropertyType) continue;
                dp.SetValue(dst, sp.GetValue(src));
            }
        }

        /// <summary>
        /// Records a decision the resolver made on a show's behalf, where the reviewer will see it:
        /// on the series' own review flags. Used when the mapping succeeded but did something worth
        /// declaring — an override of what the file names said has to be visible and reversible, or
        /// it is just a silent guess with extra steps.
        /// </summary>
        private async Task NoteOnSeriesAsync(int seriesId, string note)
        {
            var s = await movieDb.Series.FirstOrDefaultAsync(x => x.Id == seriesId);
            if (s == null) return;
            s.ImdbNeedsReview = true;
            s.ImdbReviewReason = TruncCol(note, 512);
            await movieDb.SaveChangesAsync();
        }

        private static string? ParentDirOfPath(string? p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            var s = p.Replace('/', '\\').TrimEnd('\\');
            var i = s.LastIndexOf('\\');
            return i <= 0 ? null : s.Substring(0, i);
        }

        public async Task<List<Movie>> GetMoviesFromNames(string[] movieNames, bool forceBackupLogic = false)
        {
            List<Movie> movies = new List<Movie>();
            foreach (var givenTitle in movieNames)
            {
                Movie movie = null;
                string Name = ParseName(givenTitle);
                string Year = ParseYear(givenTitle);
                var imdbID = "";

                //First check if the input is already an IMDBID
                if (IsValidImdbId(givenTitle))
                    imdbID = givenTitle;

                //If we're forcing backup logic, perform backup IMDB search before anything else.
                if (forceBackupLogic)
                    imdbID = await googleSearchService.FindImdbIdFromMovieName($"{Name} ({Year})");

                //We don't have a valid IMDBId, Search.
                if (!IsValidImdbId(imdbID))
                {
                    //The input is not an IMDBID, check to see if we can retrieve the movie by Name and Year
                    movie = await omdb.GetMovieByNameAndYear(Name, Year);

                    //If that fails, try to find the IMDBID via other services
                    if (movie == null)
                    {
                        //  OMDB lookup-by-title is very inconsistent
                        //  Google search is best, but Google has been unreliable to search using HttpClient
                        //  ImdbApi seems reliable, but has been down at times
                        if (string.IsNullOrEmpty(imdbID))
                            imdbID = await imdbApiService.FindImdbIdFromMovieName(Name);
                        if (string.IsNullOrEmpty(imdbID))
                            imdbID = await googleSearchService.FindImdbIdFromMovieName(Name);
                    }
                }

                //If we have an IMDBID but not yet retrieved a movie, try to get the movie by the ID
                if (!string.IsNullOrEmpty(imdbID) && movie == null)
                    movie = await omdb.GetMovieByImdbId(imdbID);

                movie = await PrepMovieTitle(movie);

                movies.Add(movie);
            }
            return movies;
        }

        private async Task<Movie> PrepMovieTitle(Movie movie)
        {
            // Invert a leading "The " to the library's ", The" sort form. Colon-aware: the article
            // re-attaches to the main title ("The X: Y" -> "X, The: Y"), not after the subtitle.
            var inverted = MovieTheater.Ingest.TitleNorm.InvertLeadingThe(movie.Title.Trim());
            if (!string.Equals(inverted, movie.Title.Trim(), StringComparison.Ordinal))
            {
                movie.Title = inverted;
                movie.SimpleTitle = inverted;
            }

            //Check if we've already got a copy of this movie
            var checkMovie = await movieDb.Movies.AnyAsync(d => d.imdbID == movie.imdbID);

            if (checkMovie)
                movie.Title = "!DUPLICATE DETECTED! - " + movie.Title;

            return movie;
        }


        /*
         1. If givenName is null/whitespace -> return empty string.
         2. Trim surrounding whitespace.
         3. Find the first parenthetical group that contains a 4-digit year (supports ranges like (2012-2013) or (2012–2013)).
            - Use a regex that matches a parenthesis group with a 4-digit year.
            - Use Match to locate the first occurrence; this returns the index of that parenthesis.
         4. If a match is found:
            - Return the substring from start up to the match.Index, trimmed.
            - This covers inputs like "Swan, The (2023) [junk] 1080p" -> "Swan, The".
         5. If no such parenthetical year is found:
            - Fall back to the previous behavior of removing a trailing "(YYYY)" if it exists at the end.
            - Otherwise return the trimmed input unchanged.
         6. Ensure returned string has no trailing punctuation or stray characters (trim).
        */
        private string ParseName(string givenName)
        {
            if (string.IsNullOrWhiteSpace(givenName))
                return string.Empty;

            var trimmed = givenName.Trim();

            // Regex to find a parenthetical year (e.g. "(2023)", "(2012-2013)", support en-dash or hyphen)
            var yearParenRegex = new System.Text.RegularExpressions.Regex(@"\(\s*\d{4}(?:[–-]\d{4})?\s*\)");
            var match = yearParenRegex.Match(trimmed);

            if (match.Success)
            {
                // Return everything before the first year-parenthesis occurrence
                var titleBeforeYear = trimmed.Substring(0, match.Index).Trim();

                // Additional cleanup: remove trailing separators or stray characters
                titleBeforeYear = System.Text.RegularExpressions.Regex.Replace(titleBeforeYear, @"[\s\-\:\–\—]+$", "").Trim();

                return titleBeforeYear;
            }

            // Fallback: remove a trailing "(YYYY)" or "(YYYY-YYYY)" if present at the end
            var stripped = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s*\(\s*\d{4}(?:[–-]\d{4})?\s*\)\s*$", "");
            return stripped.Trim();
        }

        private string ParseYear(string givenTitle)
        {
            /*
             1. If givenTitle is null, empty, or whitespace -> return empty string.
             2. Trim the input to remove surrounding whitespace.
             3. Attempt a strict regex match for a trailing year in parentheses,
                capturing the first 4-digit year. Support ranges like "(2012-2013)" or "(2012–2013)".
                Regex: @"\(\s*(\d{4})(?:[–-]\d{4})?\s*\)\s*$"
             4. If that match succeeds, return the captured year (group 1).
             5. If not matched, attempt a looser search for a standalone 4-digit year
                (preferring 19xx or 20xx) anywhere in the string using: @"\b(19|20)\d{2}\b"
             6. If found, return that year; otherwise return empty string.
             */

            if (string.IsNullOrWhiteSpace(givenTitle))
                return string.Empty;

            var trimmed = givenTitle.Trim();

            // Strict trailing parentheses match e.g. "Title (2012)" or "Title (2012-2013)"
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\(\s*(\d{4})(?:[–-]\d{4})?\s*\)\s*$");
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            // Fallback: find any standalone 4-digit year (prefer 1900-2099)
            var looseMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"\b(19|20)\d{2}\b");
            if (looseMatch.Success)
            {
                return looseMatch.Value;
            }

            return string.Empty;
        }
        // Resolve a few folders' worth of pending NewTitle candidates into quarantined ReviewBatch
        // Movie rows (with their files attached), exactly like the old batch-insert page did for a
        // pasted list — the caller loops until done=true. Per folder: resolve details through the
        // OMDB → IMDb-API → Google cascade (or a pinned IMDb id), refuse duplicates by tt against
        // Movie/Series/MiscVideo (a tt owned by a dead-file movie converts the candidate to an
        // Upgrade instead), then create the Movie + Playable + Primary MediaFile, attach cdN/partN
        // siblings as Parts, and leave anything ambiguous Pending with a visible reason.
        public async Task<ResolveNewTitlesResult> ResolveNewTitlesChunkAsync(int limit, string? user)
        {
            limit = Math.Clamp(limit, 1, 10);

            static string? ParentDirOf(string? p)
            {
                if (string.IsNullOrEmpty(p)) return null;
                var s = p.Replace('/', '\\').TrimEnd('\\');
                var i = s.LastIndexOf('\\');
                return i <= 0 ? null : s.Substring(0, i);
            }

            var pendingNew = await movieDb.SyncCandidates
                .Where(c => c.Status == SyncCandidateStatus.Pending && c.Kind == SyncCandidateKind.NewTitle)
                .OrderBy(c => c.Path)
                .ToListAsync();
            var folders = pendingNew
                .Where(c => c.ResolutionError == null)   // errored folders wait for a hand fix (Update) before retrying
                .GroupBy(c => (ParentDirOf(c.Path) ?? c.Path).ToLowerInvariant())
                .ToList();
            var work = folders.Take(limit).ToList();

            int created = 0, converted = 0, failed = 0;
            var now = DateTime.UtcNow;
            var partRx = new System.Text.RegularExpressions.Regex(@"(?i)\b(?:cd|disc|disk|part|pt)\s*0*(\d{1,2})\b");

            foreach (var group in work)
            {
                var members = group.OrderByDescending(c => c.SizeBytes ?? 0).ToList();
                var primaryCand = members[0];
                try
                {
                    // 1. Resolve: pinned tt wins; otherwise the same cascade the batch-insert page used,
                    // over each spelling of the folder's title (natural order before the sort form).
                    var resolved = await ResolveThroughCascadeAsync(
                        primaryCand.ResolvedImdbId, primaryCand.ParsedTitle, primaryCand.ParsedYear, preferFilm: true);
                    if (resolved == null)
                    {
                        primaryCand.ResolutionError = "No confident metadata match — set the IMDb id or fix the title, then resolve again.";
                        failed++;
                        continue;
                    }
                    var tt = resolved.imdbID!;

                    // 1b. The catalogue's verdict on WHAT this id is, when it disagrees with the shelf.
                    // FLAGGED, never refused: a title filed as a movie really can be a mini-series
                    // (that is how they are filed), so blocking would reject correct work. But a plain
                    // title search also answers a movie query with a same-named ongoing show
                    // ("Obsession" 2025), which is simply the wrong title — and the dedup below only
                    // asks whether WE already own the id, so nothing else would catch it. Writing it
                    // with the disagreement attached puts it in front of a person at LOW trust, which
                    // is the house rule for an IMDb mismatch; the card's "Reclassify as series" is one
                    // click away. A pinned id is the reviewer's own decision and is left alone.
                    var typeDisagrees = string.IsNullOrEmpty(primaryCand.ResolvedImdbId)
                        && (resolved.TitleType == TitleType.TvSeries || resolved.TitleType == TitleType.TvMiniSeries);

                    // 2. Dedup by tt across ALL THREE playable tables before creating anything.
                    var ownerMovie = await movieDb.Movies.FirstOrDefaultAsync(m => m.imdbID == tt);
                    if (ownerMovie != null)
                    {
                        var ownerAlive = ownerMovie.PlayableId != null && await movieDb.MediaFiles.AnyAsync(f =>
                            f.PlayableId == ownerMovie.PlayableId && f.JellyfinItemId != null && f.MissingSinceUtc == null);
                        if (!ownerAlive)
                        {
                            // The library already owns this title but its file is dead/absent —
                            // this "new" file is really that movie's upgrade.
                            primaryCand.Kind = SyncCandidateKind.Upgrade;
                            primaryCand.TargetMovieId = ownerMovie.id;
                            primaryCand.Signal = "tt-owned";
                            primaryCand.OldPath = ownerMovie.FilePath;
                            primaryCand.ResolvedImdbId = tt;
                            // The folder's other files must not be re-grouped into a SECOND upgrade
                            // of the same movie on the next chunk (approving both would ping-pong the
                            // Primary and lose parts). Stamp them with the reason: part-patterned
                            // siblings attach as Parts when the upgrade is APPROVED; the rest wait
                            // for a hand decision. The error text also excludes them from grouping.
                            foreach (var sibling in members.Skip(1))
                                sibling.ResolutionError = TruncCol(
                                    $"Sibling of the upgrade candidate for movie {ownerMovie.id} '{ownerMovie.Title}' — disc parts attach when that upgrade is approved.", 512);
                            converted++;
                        }
                        else
                        {
                            primaryCand.ResolutionError = TruncCol($"{tt} is already movie {ownerMovie.id} '{ownerMovie.Title}' with a live file — likely a duplicate rip.", 512);
                            failed++;
                        }
                        continue;
                    }
                    var ownerSeries = await movieDb.Series.FirstOrDefaultAsync(s => s.imdbID == tt);
                    if (ownerSeries != null)
                    {
                        primaryCand.ResolutionError = TruncCol($"{tt} is series {ownerSeries.Id} '{ownerSeries.Title}' — an episode file, not a movie.", 512);
                        failed++;
                        continue;
                    }
                    // (MiscVideo rows carry no IMDb id of their own — nothing to collide with there.)

                    // 3. Create the quarantined row + its file(s). Confidence: a year agreeing with the
                    // folder is the strongest cheap signal this resolution grabbed the right film.
                    var yearAgrees = primaryCand.ParsedYear != null && resolved.ReleaseDate != null
                        && Math.Abs(resolved.ReleaseDate.Value.Year - primaryCand.ParsedYear.Value) <= 1;
                    resolved.imdbID = tt;
                    resolved.UploadedDate = DateTime.Now;
                    resolved.ReviewBatch = "sync-scan";
                    resolved.ReviewProvenance = string.IsNullOrEmpty(primaryCand.ResolvedImdbId) ? "sync-scan" : "manual";
                    resolved.ReviewConfidence = typeDisagrees ? "LOW" : yearAgrees ? "HIGH" : "MEDIUM";
                    if (typeDisagrees)
                    {
                        resolved.ImdbNeedsReview = true;
                        resolved.ImdbReviewReason = TruncCol(
                            $"Filed as a movie, but IMDb lists {tt} as a {(resolved.TitleType == TitleType.TvMiniSeries ? "mini-series" : "TV series")}. " +
                            "That is normal for a mini-series; for an ongoing show it usually means the title matched the wrong work. " +
                            "Confirm the id, or use \"Reclassify as a TV series\".", 512);
                    }
                    resolved.ReviewSourcePath = primaryCand.Path;
                    resolved.FilePath = primaryCand.Path;
                    resolved.Playable = new Playable { Kind = PlayableKind.Movie };
                    movieDb.Movies.Add(resolved);
                    await movieDb.SaveChangesAsync();

                    try { await MovieNormalizer.ApplyAllAsync(movieDb, resolved); } catch { /* normalized parse is best-effort */ }

                    // Poster NOW, not at approve time. A review card is a judgement about whether
                    // this is the right film, and the poster is most of what makes that judgement
                    // possible — fetching it only on approval meant reviewing a wall of text and
                    // finding out afterwards. Best-effort: a title with no art is still reviewable.
                    try { await posterFetchService.EnsurePosterAsync(resolved.id, tt, isSeries: false); }
                    catch (Exception ex) { logger.LogWarning(ex, "Poster fetch failed for new movie {Id} ({Tt})", resolved.id, tt); }

                    // Stamp Jellyfin identity onto the files now so the title is streamable on approval
                    // (codec detail arrives with the next sync).
                    var itemIds = members.Where(m => !string.IsNullOrEmpty(m.JellyfinItemId)).Select(m => m.JellyfinItemId!).ToList();
                    var details = itemIds.Count > 0
                        ? (await jellyfinApi.GetItemsByIdsAsync(itemIds)).ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, MovieTheater.Services.Jellyfin.JellyfinItem>(StringComparer.OrdinalIgnoreCase);

                    MediaFile NewRow(SyncCandidate cand, MovieFileRole role, int? partNo) =>
                        new()
                        {
                            Playable = resolved.Playable,
                            Path = cand.Path,
                            Role = role,
                            PartNumber = partNo,
                            Label = "match:sync-scan",
                            JellyfinItemId = cand.JellyfinItemId,
                            SizeBytes = cand.SizeBytes,
                            DurationTicks = cand.JellyfinItemId != null && details.TryGetValue(cand.JellyfinItemId, out var d) ? d.RunTimeTicks : null,
                            LastSyncedUtc = now,
                        };

                    movieDb.MediaFiles.Add(NewRow(primaryCand, MovieFileRole.Primary, null));
                    primaryCand.Status = SyncCandidateStatus.Ingested;
                    primaryCand.CreatedMovieId = resolved.id;
                    primaryCand.ResolvedImdbId = tt;
                    primaryCand.ResolvedUtc = now;
                    primaryCand.ResolvedBy = user;

                    foreach (var sibling in members.Skip(1))
                    {
                        var fileName = sibling.Path.Replace('/', '\\');
                        fileName = fileName.Substring(fileName.LastIndexOf('\\') + 1);
                        var pm = partRx.Match(fileName);
                        if (pm.Success)
                        {
                            movieDb.MediaFiles.Add(NewRow(sibling, MovieFileRole.Part, int.Parse(pm.Groups[1].Value)));
                            sibling.Status = SyncCandidateStatus.Ingested;
                            sibling.CreatedMovieId = resolved.id;
                            sibling.ResolvedUtc = now;
                            sibling.ResolvedBy = user;
                        }
                        else
                        {
                            // Not confidently a disc part (could be a sample, an alt cut, an extra) —
                            // never guess on an attach; leave it visible with the reason.
                            sibling.ResolutionError = TruncCol($"Sibling of '{resolved.Title}' (movie {resolved.id}) — attach by hand from its review card if it belongs.", 512);
                        }
                    }
                    await movieDb.SaveChangesAsync();
                    created++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Sync-candidate resolution failed for folder of {Path}", primaryCand.Path);
                    // Detach whatever this folder Added (Movie/Playable/MediaFiles/normalizer graph) —
                    // a failed entity left in the tracker would make EVERY later SaveChanges in this
                    // request rethrow the same error and no candidate updates would persist.
                    foreach (var entry in movieDb.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
                        entry.State = EntityState.Detached;
                    primaryCand.ResolutionError = TruncCol("Resolution error: " + ex.Message, 512);
                    failed++;
                }
            }
            await movieDb.SaveChangesAsync();

            var remaining = folders.Count - work.Count;
            return new ResolveNewTitlesResult
            {
                Processed = work.Count, Created = created, Converted = converted,
                Failed = failed, Remaining = remaining, Done = remaining == 0,
            };
        }

        // ── Series-episode resolution (the "Resolve series" loop) ─────────────────────────────────────
        // Turns a folder of loose episode files into a finished series card, in bounded steps the UI
        // drives to completion. A "unit" is one of three things, and a call does at most `limit` of
        // them before returning {processed, remaining, done} — no single request has to survive a
        // whole show, let alone a whole queue:
        //
        //   identify  — resolve the folder to a Series (existing one, or create a quarantined row via
        //               the same OMDB → IMDb-API → Google cascade the batch page uses), fetch a poster
        //   episodes  — enumerate ONE season into Episode rows (TMDB + OMDB, no browser)
        //   map       — attach each file to the episode its NAME claims, as a Playable + MediaFile
        //
        // Each unit's result is durable, so an interrupted run resumes exactly where it stopped: the
        // state is the DB (TargetSeriesId set / Episode rows present / candidate no longer Pending),
        // never an in-memory cursor. Termination is guaranteed because every unit either advances that
        // state or writes a ResolutionError, and errored groups are excluded from the queue until a
        // reviewer clears them by hand.

        public async Task<ResolveSeriesResult> ResolveSeriesChunkAsync(int limit, string? user)
        {
            limit = Math.Clamp(limit, 1, 10);

            var now = DateTime.UtcNow;
            int identified = 0, enriched = 0, seasonsEnumerated = 0, episodesAdded = 0, filesMapped = 0, failed = 0;
            var log = new List<string>();

            var pendingEpisodes = await movieDb.SyncCandidates
                .Where(c => c.Status == SyncCandidateStatus.Pending && c.Kind == SyncCandidateKind.SeriesEpisode)
                .ToListAsync();

            // A group with an unresolved error waits for a hand fix (Update clears it) — retrying the
            // same failing lookup on every tick is the infinite loop this must not become.
            var queue = pendingEpisodes
                .GroupBy(c => c.SeriesFolder ?? ParentDirOfPath(c.Path) ?? c.Path, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.All(c => c.ResolutionError == null))
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int units = 0;
            foreach (var group in queue)
            {
                if (units >= limit) break;
                var members = group.OrderBy(c => c.SeasonNumber ?? 0).ThenBy(c => c.EpisodeNumber ?? 0).ThenBy(c => c.Path).ToList();
                var head = members[0];
                try
                {
                    // ── unit: identify ────────────────────────────────────────────────────────────
                    var sid = members.Select(c => c.TargetSeriesId).FirstOrDefault(x => x != null);
                    if (sid == null)
                    {
                        units++;
                        var created = await IdentifySyncSeriesGroupAsync(members, head, now);
                        if (created == null) { failed++; log.Add($"identify failed: {group.Key}"); continue; }
                        sid = created.Value;
                        identified++;
                        log.Add($"identified {group.Key} → series {sid}");
                        await movieDb.SaveChangesAsync();
                        if (units >= limit) continue;
                    }

                    var series = await movieDb.Series.FirstOrDefaultAsync(s => s.Id == sid.Value);
                    if (series == null || string.IsNullOrEmpty(series.imdbID))
                    {
                        MarkGroupError(members, $"Series {sid} has no IMDb id — set one on its review card, then resolve again.");
                        failed++; await movieDb.SaveChangesAsync(); continue;
                    }

                    // ── unit: enrich a bare show ──────────────────────────────────────────────────
                    // The sync can attribute files to a series that exists but was never filled in —
                    // Nick Arcade arrived from an archive.org ingest with an id, a title and nothing
                    // else. Identification only enriches shows it CREATES, so without this a matched
                    // show would reach the reviewer with no poster, plot or cast. Keyed on
                    // ImdbVerifiedDate, which is what makes it run once rather than every tick.
                    if (series.ImdbVerifiedDate == null)
                    {
                        if (units >= limit) break;
                        units++;
                        try
                        {
                            if (await titleEnrichService.EnrichAsync(series.Id, isSeries: true))
                            {
                                enriched++;
                                log.Add($"series {sid} '{series.Title}': enriched (poster, plot, rating)");
                            }
                        }
                        catch (Exception ex) { logger.LogWarning(ex, "Enrich failed for series {Sid}", sid); }
                        // A failed enrich must not block the episodes — the card is poorer, not stuck.
                        if (units >= limit) continue;
                    }

                    // ── unit(s): enumerate seasons ────────────────────────────────────────────────
                    // ONLY for a series with no episode list at all. An existing show's episodes came
                    // from the curated pipeline (title-authority re-maps and all), and TMDB/IMDb
                    // disagree with it often enough that pouring catalogue rows into it would fight
                    // that work and invent duplicates. For those, the resolver maps into the list that
                    // is already there and reports whatever doesn't fit.
                    var existingEpisodeCount = await movieDb.Episodes.CountAsync(e => e.SeriesId == sid.Value);
                    if (existingEpisodeCount == 0 && !members.Any(m => m.SeriesListOwned))
                        foreach (var m in members) m.SeriesListOwned = true;   // durable across the chunked walk
                    if (members.Any(m => m.SeriesListOwned))
                    {
                        var fileSeasons = members.Where(c => c.SeasonNumber != null).Select(c => c.SeasonNumber!.Value).Distinct().ToList();
                        var plan = await episodeCatalog.PlanAsync(series.imdbID);
                        // The catalogue's season list, not the disk's: a card that says 43 of 84 is the
                        // honest one, and the missing episodes are a fact worth showing.
                        var wanted = plan.Seasons.Concat(fileSeasons).Distinct().OrderBy(n => n).ToList();
                        if (wanted.Count == 0)
                        {
                            MarkGroupError(members, TruncCol($"No season list for {series.imdbID} ({plan.Note ?? "no catalogue match"}) — check the IMDb id on the series card.", 512));
                            failed++; await movieDb.SaveChangesAsync(); continue;
                        }

                        // Seasons already written by an earlier chunk of THIS enumeration.
                        var haveSeasons = (await movieDb.Episodes
                            .Where(e => e.SeriesId == sid.Value)
                            .Select(e => e.SeasonNumber).Distinct().ToListAsync()).ToHashSet();
                        // Seasons this call actually asked about. A season the catalogue turns out to
                        // have nothing for writes no rows, so "has rows" alone would mark it
                        // outstanding forever and the group would never reach the mapping step.
                        var attempted = new HashSet<int>();
                        bool stalled = false;
                        foreach (var season in wanted.Where(n => !haveSeasons.Contains(n)))
                        {
                            if (units >= limit) break;
                            attempted.Add(season);
                            units++; seasonsEnumerated++;
                            var eps = await episodeCatalog.FetchSeasonAsync(series.imdbID, plan.TmdbTvId, season);
                            if (eps.Count == 0)
                            {
                                // A season on disk that no catalogue knows: never invent episodes for
                                // it, and never retry it forever. Say so and stop this group.
                                if (fileSeasons.Contains(season))
                                {
                                    MarkGroupError(members, TruncCol(
                                        $"Season {season} has files on disk but neither TMDB nor IMDb lists it for {series.imdbID}. " +
                                        "Fix the series id or the season numbering, then resolve again.", 512));
                                    failed++; stalled = true;
                                }
                                continue;
                            }
                            foreach (var e in eps)
                                movieDb.Episodes.Add(new Episode
                                {
                                    SeriesId = sid.Value,
                                    SeasonNumber = e.Season,
                                    EpisodeNumber = e.Episode,
                                    Title = e.Title,
                                    ImdbId = e.ImdbId,
                                    Plot = e.Plot,
                                    AirDate = e.AirDate,
                                    RuntimeMinutes = e.RuntimeMinutes,
                                    ImdbRating = e.ImdbRating,
                                    StillPath = e.StillPath,
                                });
                            episodesAdded += eps.Count;
                            haveSeasons.Add(season);
                            await movieDb.SaveChangesAsync();
                            log.Add($"series {sid} S{season}: +{eps.Count} episodes");
                        }
                        if (stalled) { await movieDb.SaveChangesAsync(); continue; }
                        // Seasons we ran out of budget for — come back for them on the next call
                        // rather than running a 30-season show inside one request. Seasons we DID ask
                        // about are done either way, so the walk always terminates.
                        if (wanted.Any(n => !haveSeasons.Contains(n) && !attempted.Contains(n)))
                        {
                            await movieDb.SaveChangesAsync();
                            continue;
                        }
                    }

                    // ── unit: map files to episodes ───────────────────────────────────────────────
                    if (units >= limit) break;
                    units++;
                    var mapped = await MapSyncSeriesFilesAsync(members, sid.Value, now, user);
                    filesMapped += mapped.Mapped;
                    log.Add($"series {sid}: mapped {mapped.Mapped}/{members.Count} file(s)" +
                            (mapped.Unmatched > 0 ? $", {mapped.Unmatched} left for review" : ""));
                    await movieDb.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Sync series resolution failed for {Folder}", group.Key);
                    foreach (var entry in movieDb.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
                        entry.State = EntityState.Detached;
                    MarkGroupError(members, TruncCol("Resolution error: " + ex.Message, 512));
                    failed++;
                    await movieDb.SaveChangesAsync();
                }
            }

            // Recompute what's left from the DB, not from the loop's own bookkeeping — an independent
            // count is the only "remaining" that can catch a unit that thought it made progress.
            var stillPending = await movieDb.SyncCandidates
                .Where(c => c.Status == SyncCandidateStatus.Pending && c.Kind == SyncCandidateKind.SeriesEpisode)
                .ToListAsync();
            var remainingGroups = stillPending
                .GroupBy(c => c.SeriesFolder ?? ParentDirOfPath(c.Path) ?? c.Path, StringComparer.OrdinalIgnoreCase)
                .Count(g => g.All(c => c.ResolutionError == null));

            return new ResolveSeriesResult
            {
                Processed = units, Identified = identified, Enriched = enriched,
                SeasonsEnumerated = seasonsEnumerated, EpisodesAdded = episodesAdded,
                FilesMapped = filesMapped, Failed = failed, Remaining = remainingGroups,
                Blocked = stillPending
                    .GroupBy(c => c.SeriesFolder ?? ParentDirOfPath(c.Path) ?? c.Path, StringComparer.OrdinalIgnoreCase)
                    .Count(g => g.Any(c => c.ResolutionError != null)),
                Done = remainingGroups == 0, Log = log,
            };
        }

        /// <summary>
        /// The reviewer's answer to a season-boundary disagreement: map this show's files to its
        /// catalogued episodes in ABSOLUTE order — nth file to nth episode — because the disk and the
        /// catalogue split the same episodes into seasons differently. Deliberately a separate,
        /// explicit action rather than a mode of the bulk loop: it overrides what the file names say,
        /// which is a judgement the tool is not entitled to make on its own.
        /// </summary>
        public async Task<MapAbsoluteResult> MapSeriesAbsoluteAsync(int candidateId, string? user)
        {
            var head = await movieDb.SyncCandidates.FirstOrDefaultAsync(c => c.Id == candidateId);
            if (head == null) return new MapAbsoluteResult { Message = "Candidate not found." };
            if (head.TargetSeriesId == null)
                return new MapAbsoluteResult { Message = "Identify the show first." };

            var folder = head.SeriesFolder;
            var members = (folder == null
                ? new List<SyncCandidate> { head }
                : await movieDb.SyncCandidates
                    .Where(c => c.Status == SyncCandidateStatus.Pending && c.SeriesFolder == folder)
                    .ToListAsync())
                .OrderBy(c => c.SeasonNumber ?? 0).ThenBy(c => c.EpisodeNumber ?? 0).ThenBy(c => c.Path)
                .ToList();
            foreach (var m in members) m.ResolutionError = null;

            var res = await MapSyncSeriesFilesAsync(members, head.TargetSeriesId.Value,
                DateTime.UtcNow, user, absoluteOrder: true);
            await movieDb.SaveChangesAsync();
            logger.LogInformation("Sync series absolute-order mapping: series {Sid}, {Mapped}/{Total} file(s) (by {User})",
                head.TargetSeriesId, res.Mapped, members.Count, user ?? "?");
            return new MapAbsoluteResult { Success = res.Mapped > 0, Mapped = res.Mapped, Unmatched = res.Unmatched, Total = members.Count };
        }

        /// <summary>
        /// The forms of a folder-derived title worth asking a catalogue about, best first. The folder
        /// carries the library's A-Z sort convention ("Sheep Detectives, The"); IMDb, OMDB and TMDB all
        /// carry the natural title, so the un-inverted form is tried FIRST and the folder's own
        /// spelling is kept only as a fallback for the rare title that really does end in an article.
        /// </summary>
        private static List<string> TitleLookupForms(string? parsedTitle, int? parsedYear)
        {
            var forms = new List<string>();
            var title = (parsedTitle ?? "").Trim();
            if (title.Length == 0) return forms;
            var suffix = parsedYear != null ? $" ({parsedYear})" : "";

            var natural = MovieTheater.Ingest.TitleNorm.RestoreLeadingThe(title);
            if (!string.Equals(natural, title, StringComparison.Ordinal)) forms.Add(natural + suffix);
            forms.Add(title + suffix);
            return forms;
        }

        /// <summary>
        /// Runs the batch page's OMDB → IMDb-API → Google cascade over each lookup form until one
        /// answers with a valid IMDb id. A pinned id short-circuits everything. Returns null when no
        /// form resolved — the caller turns that into a visible "set the IMDb id" on the card.
        ///
        /// <para><paramref name="preferFilm"/> adds one retry for the movie lane, and only when the
        /// cascade's answer is unusable: a general title search has no notion of what KIND of work
        /// the shelf meant, so "Obsession" in the movies tree comes back as a same-named television
        /// series. Asking TMDB's film index by name settles it, exactly as the series lane asks the TV
        /// index. The retry is a fallback, not the primary — the cascade keeps answering the ordinary
        /// cases the way it always has.</para>
        /// </summary>
        private async Task<Movie?> ResolveThroughCascadeAsync(
            string? pinnedTt, string? parsedTitle, int? parsedYear, bool preferFilm = false)
        {
            if (!string.IsNullOrEmpty(pinnedTt))
            {
                try { return (await GetMoviesFromNames(new[] { pinnedTt! })).FirstOrDefault(); }
                catch { return null; }
            }

            Movie? best = null;
            foreach (var form in TitleLookupForms(parsedTitle, parsedYear))
            {
                Movie? m = null;
                // A fully-failed lookup can throw inside the cascade — same outcome as no match, and
                // it must not stop the remaining forms from being tried.
                try { m = (await GetMoviesFromNames(new[] { form })).FirstOrDefault(); }
                catch { }
                if (m == null || string.IsNullOrEmpty(m.imdbID) || !IsValidImdbId(m.imdbID)) continue;
                var wrongKind = preferFilm && (m.TitleType == TitleType.TvSeries || m.TitleType == TitleType.TvMiniSeries);
                if (!wrongKind) return m;
                best ??= m;   // remember it: if the film index also comes up empty, this is still the best we have
            }

            if (preferFilm)
            {
                var film = await ResolveFilmThroughTmdbAsync(parsedTitle, parsedYear);
                if (film != null) return film;
            }
            return best;
        }

        /// <summary>
        /// Asks TMDB's film index for a title and carries the hit back through the normal by-id
        /// lookup, so the returned row is built the same way every other resolution is. Null when
        /// TMDB has no film by that name, or holds no IMDb id for the one it found.
        /// </summary>
        private async Task<Movie?> ResolveFilmThroughTmdbAsync(string? parsedTitle, int? parsedYear)
        {
            foreach (var form in TitleLookupForms(parsedTitle, null))
            {
                try
                {
                    var hits = parsedYear != null ? await tmdb.SearchMovie(form, parsedYear) : new List<MovieDto>();
                    if (hits.Count == 0) hits = await tmdb.SearchMovie(form);
                    foreach (var hit in hits.Take(3))
                    {
                        var detail = await tmdb.GetMovieDetail(hit.Id);
                        var tt = detail?.ImdbId;
                        if (string.IsNullOrWhiteSpace(tt) || !IsValidImdbId(tt)) continue;
                        var m = (await GetMoviesFromNames(new[] { tt })).FirstOrDefault();
                        if (m != null && !string.IsNullOrEmpty(m.imdbID) && IsValidImdbId(m.imdbID))
                        {
                            logger.LogInformation("Film-index retry: '{Form}' → TMDB {TmdbId} → {Tt}", form, hit.Id, tt);
                            return m;
                        }
                    }
                }
                catch (Exception ex) { logger.LogDebug(ex, "TMDB film search failed for {Title}", form); }
            }
            return null;
        }

        /// <summary>
        /// Records why a show could not be finished — on ONE row, not the same sentence 84 times.
        /// It must be a row that is still Pending: after a partial map the mapped rows have left
        /// Pending, and both the card and the resolver's queue read Pending rows only, so an error
        /// parked on an Ingested row is invisible and the group silently re-queues.
        /// </summary>
        private static void MarkGroupError(List<SyncCandidate> members, string? message)
        {
            var target = members.FirstOrDefault(m => m.Status == SyncCandidateStatus.Pending) ?? members[0];
            target.ResolutionError = message;
        }

        /// <summary>
        /// Resolves an episode folder to a Series row — reusing an existing show when its IMDb id is
        /// already ours, otherwise creating a quarantined <c>ReviewBatch</c> series from the same
        /// cascade the batch-insert page uses, and fetching its poster so the review card is complete
        /// BEFORE approval rather than after it. Returns the series id, or null when the lookup could
        /// not confidently name the show (the reason is left on the group).
        /// </summary>
        private async Task<int?> IdentifySyncSeriesGroupAsync(List<SyncCandidate> members, SyncCandidate head, DateTime now)
        {
            if (string.IsNullOrEmpty(head.ResolvedImdbId) && string.IsNullOrWhiteSpace(head.ParsedTitle))
            {
                MarkGroupError(members, "No title could be parsed from the folder — set a title or IMDb id, then resolve again.");
                return null;
            }

            // TV index FIRST, when we are going on a title rather than a pinned id. A show and its
            // films share a name and a shelf — the Muppets being the standing example — and a general
            // title search will often hand back the movie, confidently. Asking TMDB's TV index cannot
            // make that mistake, so it settles the identity before the general cascade ever runs.
            var pinnedTt = head.ResolvedImdbId;
            bool tvIndexMatched = false;
            if (string.IsNullOrEmpty(pinnedTt) && !string.IsNullOrWhiteSpace(head.ParsedTitle))
            {
                foreach (var form in TitleLookupForms(head.ParsedTitle, null))
                {
                    var tv = await episodeCatalog.FindSeriesByTitleAsync(form, head.ParsedYear);
                    if (tv == null || !IsValidImdbId(tv.ImdbId)) continue;
                    // Hand the cascade an id rather than a name. Kept in a LOCAL: this is the tool's
                    // match, not the reviewer's, so it must not masquerade as a hand-pinned id in the
                    // provenance the card shows.
                    pinnedTt = tv.ImdbId;
                    tvIndexMatched = true;
                    logger.LogInformation("Sync series identify: '{Form}' matched TMDB tv {TvId} '{Name}' -> {Tt}",
                        form, tv.TmdbTvId, tv.Name, tv.ImdbId);
                    break;
                }
            }

            var resolved = await ResolveThroughCascadeAsync(pinnedTt, head.ParsedTitle, head.ParsedYear);
            if (resolved == null)
            {
                MarkGroupError(members, "No confident metadata match — set the IMDb id or fix the title, then resolve again.");
                return null;
            }
            var tt = resolved.imdbID!;

            // Already ours? Point at it — never a second row for the same show.
            var existing = await movieDb.Series.FirstOrDefaultAsync(s => s.imdbID == tt);
            if (existing != null)
            {
                foreach (var c in members) { c.TargetSeriesId = existing.Id; c.ResolvedImdbId = tt; }
                return existing.Id;
            }
            // A tt the MOVIE table owns is a classification conflict, not a series to create.
            var ownerMovie = await movieDb.Movies.FirstOrDefaultAsync(m => m.imdbID == tt);
            if (ownerMovie != null)
            {
                MarkGroupError(members, TruncCol(
                    $"{tt} is movie {ownerMovie.id} '{ownerMovie.Title}', not a series — reclassify that title or pin a different id.", 512));
                return null;
            }
            // The mirror of the movie lane's type guard: a folder of episode files whose title search
            // lands on a FILM has matched the wrong work, and creating a Series row from it would give
            // the show a tt whose episode list can never be enumerated.
            if (string.IsNullOrEmpty(head.ResolvedImdbId) && !tvIndexMatched && resolved.TitleType == TitleType.Movie)
            {
                MarkGroupError(members, TruncCol(
                    $"'{head.ParsedTitle}' resolved to {tt} '{resolved.Title}', which IMDb lists as a FILM, and TMDB's TV " +
                    "index has no show by that name — a show and its movies often share a shelf, so this is usually the " +
                    "wrong work. Pin the show's IMDb id on this card, then resolve again.", 512));
                return null;
            }

            var series = new Series();
            CopyTitleScalars(resolved, series);
            if (series.TitleType != TitleType.TvSeries && series.TitleType != TitleType.TvMiniSeries)
                series.TitleType = TitleType.TvSeries;
            series.imdbID = tt;
            series.UploadedDate = DateTime.Now;
            series.StartYear ??= resolved.ReleaseDate?.Year ?? head.ParsedYear;
            series.ReviewBatch = "sync-scan";
            series.ReviewProvenance = !string.IsNullOrEmpty(head.ResolvedImdbId) ? "manual"
                : tvIndexMatched ? "sync-scan-tv" : "sync-scan";
            var yearAgrees = head.ParsedYear != null && resolved.ReleaseDate != null
                && Math.Abs(resolved.ReleaseDate.Value.Year - head.ParsedYear.Value) <= 1;
            series.ReviewConfidence = yearAgrees ? "HIGH" : "MEDIUM";
            series.ReviewSourcePath = TruncCol(head.SeriesFolder ?? ParentDirOfPath(head.Path), 1024);
            movieDb.Series.Add(series);
            await movieDb.SaveChangesAsync();   // assigns series.Id

            try { await SeriesNormalizer.ApplyAllAsync(movieDb, series); } catch { /* normalized parse is best-effort */ }
            try { await posterFetchService.EnsurePosterAsync(series.Id, tt, isSeries: true); } catch { /* a card without art is still reviewable */ }

            foreach (var c in members) { c.TargetSeriesId = series.Id; c.ResolvedImdbId = tt; }
            return series.Id;
        }

        /// <summary>
        /// Attaches each of a group's files to the episode its FILE NAME names — (season, episode)
        /// exact match against the Episode rows, never position in a sorted list, because a folder
        /// with a gap or absolute numbering would otherwise off-by-one an entire season without
        /// anything looking wrong. A file whose episode does not exist, or whose episode already has a
        /// Primary, is left Pending with a reason so it lands in front of a person.
        ///
        /// <para><paramref name="absoluteOrder"/> is the reviewer's explicit override for the case the
        /// shape guard catches: the disk and the catalogue agree on the TOTAL but split it into
        /// seasons differently, so the nth file is the nth episode even though their season/episode
        /// labels differ. It is never chosen automatically — the tool can see the ambiguity but not
        /// resolve it, so a person does, and then the tool does the clerical part.</para>
        /// </summary>
        private async Task<(int Mapped, int Unmatched)> MapSyncSeriesFilesAsync(
            List<SyncCandidate> members, int seriesId, DateTime now, string? user, bool absoluteOrder = false)
        {
            var episodes = await movieDb.Episodes.Where(e => e.SeriesId == seriesId).ToListAsync();
            var byNumber = episodes
                .GroupBy(e => (e.SeasonNumber, e.EpisodeNumber))
                .ToDictionary(g => g.Key, g => g.First());
            var epIds = episodes.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).ToList();
            var filesByPlayable = (await movieDb.MediaFiles.Where(f => epIds.Contains(f.PlayableId)).ToListAsync())
                .GroupBy(f => f.PlayableId).ToDictionary(g => g.Key, g => g.ToList());

            // Absolute mode re-points each file at the nth catalogued episode instead of the one its
            // label names. Only legal on a 1:1 count with nothing already mapped — otherwise the zip
            // has no defined meaning and would collide with existing files.
            Dictionary<int, Episode>? absoluteTarget = null;
            string? autoAbsoluteNote = null;
            if (absoluteOrder)
            {
                absoluteTarget = filesByPlayable.Count > 0
                    ? null
                    : MovieTheater.Services.Series.SyncSeriesMatcher.AbsolutePairing(members, episodes);
                if (absoluteTarget == null)
                {
                    var n = members.Count(c => c.SeasonNumber != null && c.EpisodeNumber != null);
                    MarkGroupError(members, TruncCol(
                        $"Absolute-order mapping needs an exact 1:1 with nothing already mapped: {n} file(s) " +
                        $"vs {episodes.Count} catalogued episode(s), {filesByPlayable.Count} already mapped.", 512));
                    return (0, members.Count);
                }
            }
            else
            {
                var mismatch = MovieTheater.Services.Series.SyncSeriesMatcher.SeasonShapeMismatch(members, episodes);
                if (mismatch != null)
                {
                    var diskCount = members.Count(c => c.SeasonNumber != null && c.EpisodeNumber != null);
                    var totalsAgree = diskCount == episodes.Count && filesByPlayable.Count == 0;

                    // An EXACT 1:1 with nothing already mapped is not actually ambiguous: the two
                    // sources agree on how many episodes exist and disagree only on where a season
                    // boundary falls, so the nth file is the nth episode and there is no rival
                    // reading to choose between. Stopping here left a fully catalogued show sitting
                    // unmapped behind a button, which is not a finished job. Map it, and SAY the
                    // numbering was overridden so the decision is visible and reversible.
                    if (totalsAgree)
                    {
                        absoluteTarget = MovieTheater.Services.Series.SyncSeriesMatcher.AbsolutePairing(members, episodes);
                        if (absoluteTarget != null)
                        {
                            autoAbsoluteNote = TruncCol(
                                $"Mapped in absolute order: {mismatch}, but both agree on {diskCount} episodes, " +
                                "so each file was attached to the episode at its position rather than to the one its " +
                                "name claims. Check the first episode of season 2 if the show numbers its seasons oddly.", 512);
                            logger.LogInformation(
                                "Series {Sid}: season shapes disagree ({Mismatch}) but totals match at {N} — mapping in absolute order",
                                seriesId, mismatch, diskCount);
                        }
                    }

                    if (absoluteTarget == null)
                    {
                        MarkGroupError(members, TruncCol(
                            $"Not mapped — {mismatch}. Mapping by number would shift every later episode. " +
                            "Map these by hand from the series card.", 512));
                        return (0, members.Count);
                    }
                }
            }

            // One Jellyfin round trip for the whole group's runtimes rather than one per file.
            var itemIds = members.Where(m => !string.IsNullOrEmpty(m.JellyfinItemId)).Select(m => m.JellyfinItemId!).Distinct().ToList();
            var details = new Dictionary<string, MovieTheater.Services.Jellyfin.JellyfinItem>(StringComparer.OrdinalIgnoreCase);
            if (itemIds.Count > 0)
            {
                try
                {
                    foreach (var i in await jellyfinApi.GetItemsByIdsAsync(itemIds)) details[i.Id] = i;
                }
                catch { /* runtime detail is a nicety; the mapping itself does not need it */ }
            }

            int mapped = 0, unmatched = 0;
            var reasons = new List<string>();
            foreach (var c in members)
            {
                if (c.SeasonNumber == null || c.EpisodeNumber == null)
                { unmatched++; reasons.Add($"{LeafOf(c.Path)}: no SxxExx in the file name"); continue; }
                if (c.SpansToEpisode != null)
                {
                    unmatched++;
                    reasons.Add($"{LeafOf(c.Path)}: covers E{c.EpisodeNumber}–E{c.SpansToEpisode} — attach it by hand from the series card");
                    continue;
                }
                Episode? ep;
                if (absoluteTarget != null)
                {
                    if (!absoluteTarget.TryGetValue(c.Id, out ep))
                    { unmatched++; reasons.Add($"{LeafOf(c.Path)}: no position in the absolute order"); continue; }
                }
                else if (!byNumber.TryGetValue((c.SeasonNumber.Value, c.EpisodeNumber.Value), out ep))
                { unmatched++; reasons.Add($"{LeafOf(c.Path)}: S{c.SeasonNumber:00}E{c.EpisodeNumber:00} is not an episode of this series"); continue; }

                if (ep.PlayableId != null
                    && filesByPlayable.TryGetValue(ep.PlayableId.Value, out var existingFiles)
                    && existingFiles.Any(f => f.Role == MovieFileRole.Primary))
                {
                    unmatched++;
                    reasons.Add($"{LeafOf(c.Path)}: S{c.SeasonNumber:00}E{c.EpisodeNumber:00} already has a primary file");
                    continue;
                }

                if (ep.PlayableId == null)
                {
                    var pl = new Playable { Kind = PlayableKind.Episode };
                    movieDb.Playables.Add(pl);
                    await movieDb.SaveChangesAsync();
                    ep.PlayableId = pl.Id;
                }
                movieDb.MediaFiles.Add(new MediaFile
                {
                    PlayableId = ep.PlayableId!.Value,
                    Path = c.Path,
                    Role = MovieFileRole.Primary,
                    Label = "match:sync-scan-series",
                    JellyfinItemId = c.JellyfinItemId,
                    SizeBytes = c.SizeBytes,
                    DurationTicks = c.JellyfinItemId != null && details.TryGetValue(c.JellyfinItemId, out var d) ? d.RunTimeTicks : null,
                    LastSyncedUtc = now,
                });
                c.Status = SyncCandidateStatus.Ingested;
                c.ResolvedUtc = now;
                c.ResolvedBy = user;
                mapped++;
            }

            if (unmatched > 0)
                MarkGroupError(members, TruncCol(
                    $"{unmatched} of {members.Count} file(s) could not be mapped: " + string.Join("; ", reasons.Take(6))
                    + (reasons.Count > 6 ? $"; +{reasons.Count - 6} more" : ""), 512));
            else if (autoAbsoluteNote != null)
                // Every file mapped, so no candidate stays Pending to carry a message — record the
                // override on the SERIES, where the reviewer is looking at the result of it.
                await NoteOnSeriesAsync(seriesId, autoAbsoluteNote);
            return (mapped, unmatched);
        }

        private static string LeafOf(string path)
        {
            var s = path.Replace('/', '\\').TrimEnd('\\');
            var i = s.LastIndexOf('\\');
            return i < 0 ? s : s.Substring(i + 1);
        }

        // -- The whole last mile, driven to completion --------------------------------------------
        // Series first: a folder of episodes that resolves into a Series changes what the new-title
        // lane may do (a tt the show now owns must not also become a movie), so the ordering is
        // deterministic rather than incidental. Both loops stop on no-progress rather than a fixed
        // count: a chunk that neither advances nor shrinks the queue is done, however many items
        // remain blocked on a human decision.
        public async Task<SyncResolveSummary> ResolveAllAsync(Action<string>? progress, CancellationToken cancel)
        {
            var sum = new SyncResolveSummary();
            void Say(string m) { try { progress?.Invoke(m); } catch { } }

            int? lastRemaining = null;
            int idle = 0;
            for (int guard = 0; guard < 400 && !cancel.IsCancellationRequested; guard++)
            {
                var r = await ResolveSeriesChunkAsync(4, "sync");
                sum.SeriesIdentified += r.Identified;
                sum.SeriesEnriched += r.Enriched;
                sum.EpisodesCatalogued += r.EpisodesAdded;
                sum.EpisodeFilesMapped += r.FilesMapped;
                Say($"resolving shows - {r.Remaining} left");
                if (r.Done || r.Processed == 0) break;
                idle = r.Remaining == lastRemaining ? idle + 1 : 0;
                lastRemaining = r.Remaining;
                if (idle >= 3) { sum.Notes.Add("Series resolution stopped making progress; the rest need a hand fix."); break; }
            }

            lastRemaining = null; idle = 0;
            for (int guard = 0; guard < 400 && !cancel.IsCancellationRequested; guard++)
            {
                var r = await ResolveNewTitlesChunkAsync(3, "sync");
                sum.MoviesCreated += r.Created;
                sum.MoviesConvertedToUpgrade += r.Converted;
                Say($"resolving new titles - {r.Remaining} folder(s) left");
                if (r.Done || r.Processed == 0) break;
                idle = r.Remaining == lastRemaining ? idle + 1 : 0;
                lastRemaining = r.Remaining;
                if (idle >= 3) { sum.Notes.Add("New-title resolution stopped making progress; the rest need a hand fix."); break; }
            }

            // Anything still pending WITH a reason is a decision waiting on a person, not a failure.
            sum.NeedsAttention = await movieDb.SyncCandidates
                .CountAsync(c => c.Status == SyncCandidateStatus.Pending && c.ResolutionError != null, cancel);
            return sum;
        }

        public class ResolveNewTitlesResult
        {
            public int Processed { get; set; }
            public int Created { get; set; }
            public int Converted { get; set; }
            public int Failed { get; set; }
            public int Remaining { get; set; }
            public bool Done { get; set; }
        }

        public class ResolveSeriesResult
        {
            public int Processed { get; set; }
            public int Identified { get; set; }
            public int Enriched { get; set; }
            public int SeasonsEnumerated { get; set; }
            public int EpisodesAdded { get; set; }
            public int FilesMapped { get; set; }
            public int Failed { get; set; }
            public int Remaining { get; set; }
            public int Blocked { get; set; }
            public bool Done { get; set; }
            public List<string> Log { get; set; } = new();
        }

        public class MapAbsoluteResult
        {
            public bool Success { get; set; }
            public int Mapped { get; set; }
            public int Unmatched { get; set; }
            public int Total { get; set; }
            public string? Message { get; set; }
        }
    }
}
