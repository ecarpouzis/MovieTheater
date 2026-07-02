import { detectStreamCapabilities } from "./streamCapabilities";

// Posters are on-disk files keyed by id, and the Movie / Series / MiscVideo id spaces overlap (a single
// id can be both a Movie and a Series), so each non-movie kind has its own route namespace or they'd serve
// each other's poster. Movies keep /Image; series use /SeriesImage; misc videos use /MiscImage (no version).
function getMoviePoster(id, posterVersion, kind) {
  if (kind === "misc") return `/MiscImage/${id}`;
  const base = kind === "series" ? `/SeriesImage/${id}` : `/Image/${id}`;
  return posterVersion ? `${base}?v=${posterVersion}` : base;
}

function getPosterThumbnail(id, posterVersion, kind) {
  if (kind === "misc") return `/MiscImageThumb/${id}`;
  const base = kind === "series" ? `/SeriesImageThumb/${id}` : `/ImageThumb/${id}`;
  return posterVersion ? `${base}?v=${posterVersion}` : base;
}

function getMovie(id) {
  const url = "/API/GetMovie?id=" + id;

  return fetch(url, {
    method: "get",
  });
}

function getSeries(id) {
  return fetch("/API/GetSeries?id=" + id, { method: "get" });
}

// A card carries a kind ("movie" | "series"); fetch the right detail.
function getTitle(id, kind) {
  return kind === "series" ? getSeries(id) : getMovie(id);
}

// The franchise rail for the modal: the title's franchise(s), each an ordered list of fellow members.
function getFranchiseRail(id, kind) {
  return fetch(`/API/GetFranchiseRail?id=${id}&kind=${kind === "series" ? "series" : "movie"}`, { method: "get" });
}

function insertMovie(movie) {
  const url = "/API/InsertMovie";
  movie.releaseDate = new Date(movie.releaseDate);

  return fetch(url, {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },

    body: JSON.stringify(movie),
  });
}

function updateMovie(movie) {
  const url = "/API/UpdateMovie";

  const payload = {
    id: movie.id,
    title: movie.title,
    simpleTitle: movie.simpleTitle,
    rating: movie.rating,
    releaseDate: movie.releaseDate ? new Date(movie.releaseDate).toISOString() : null,
    runtime: movie.runtime,
    genre: movie.genre,
    director: movie.director,
    writer: movie.writer,
    actors: movie.actors,
    plot: movie.plot,
    posterLink: movie.posterLink,
    imdbRating: movie.imdbRating === "" || movie.imdbRating == null ? null : Number(movie.imdbRating),
    imdbID: movie.imdbID,
    tomatoRating: movie.tomatoRating === "" || movie.tomatoRating == null ? null : Number(movie.tomatoRating),
    rtTomatometer: movie.rtTomatometer === "" || movie.rtTomatometer == null ? null : Number(movie.rtTomatometer),
    rtPopcornmeter: movie.rtPopcornmeter === "" || movie.rtPopcornmeter == null ? null : Number(movie.rtPopcornmeter),
    removeFromRandom: !!movie.removeFromRandom,
  };

  return fetch(url, {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(payload),
  });
}

// Edit a series in place (peer of updateMovie).
function updateSeries(series) {
  return fetch("/API/UpdateSeries", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      id: series.id,
      title: series.title,
      simpleTitle: series.simpleTitle,
      rating: series.rating,
      releaseDate: series.releaseDate ? new Date(series.releaseDate).toISOString() : null,
      runtime: series.runtime,
      genre: series.genre,
      director: series.director,
      writer: series.writer,
      actors: series.actors,
      plot: series.plot,
      posterLink: series.posterLink,
      imdbRating: series.imdbRating === "" || series.imdbRating == null ? null : Number(series.imdbRating),
      imdbID: series.imdbID,
      rtTomatometer: series.rtTomatometer === "" || series.rtTomatometer == null ? null : Number(series.rtTomatometer),
      rtPopcornmeter: series.rtPopcornmeter === "" || series.rtPopcornmeter == null ? null : Number(series.rtPopcornmeter),
      removeFromRandom: !!series.removeFromRandom,
    }),
  });
}

// Re-pull IMDb data (rating / cert / year / plot / poster) for one title from its stored tt — for after a
// tt correction. kind "movie" | "series".
function refetchTitle(id, kind) {
  return fetch(`/API/RefetchTitle?id=${encodeURIComponent(id)}&kind=${encodeURIComponent(kind || "movie")}`, { method: "post" });
}

function getUsers() {
  const url = "/API/API_UserList";
  return fetch(url);
}

// Password travels in the JSON body (never the query string) so it can't leak into logs.
function loginUser(username, password) {
  return fetch("/API/Login", {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      Username: username,
      Password: password ?? null,
    }),
  });
}

// Restores the session from the auth cookie without re-running login. cache:"no-store" so a browser
// never serves a stale session payload (which once showed an empty ratings list on a returning device).
function getCurrentUser() {
  return fetch("/API/Me", { cache: "no-store" });
}

// Pass a null/empty newPassword to remove the password from the account.
function setPassword(currentPassword, newPassword) {
  return fetch("/API/SetPassword", {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      CurrentPassword: currentPassword ?? null,
      NewPassword: newPassword ?? null,
    }),
  });
}

// ── Streaming control plane (docs/streaming-plan.md §6) ──────────────────────
// Start returns { playSessionId, hlsUrl, durationTicks, isDirectStream,
// audioTracks, subtitleTracks, resumePositionTicks }.

// A stream targets a Playable: either a movie (legacy movieId → its Primary file),
// any title's playableId (episode / misc / movie), and optionally a specific
// mediaFileId (a Part / Variant / Extra). The server resolves movieId → playableId
// when playableId is absent.
function startStream({ movieId = null, playableId = null, mediaFileId = null, maxBitrateBps = null, audioStreamIndex = null, subtitleStreamIndex = null, startSeconds = null, forceTranscode = false }) {
  // Negotiate the codec profile from this browser's real capabilities (§14.1) so
  // HEVC/AV1-capable clients avoid a needless H.264 re-encode.
  const caps = detectStreamCapabilities();
  return fetch("/API/Stream/Start", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ movieId, playableId, mediaFileId, maxBitrateBps, audioStreamIndex, subtitleStreamIndex, startSeconds, forceTranscode, ...caps }),
  });
}

// passive=true (TV channels) keeps Jellyfin throttling honest without writing
// resume progress or auto-Seen — background play shouldn't claim you watched it.
function reportStreamProgress({ playSessionId, movieId = null, playableId = null, mediaFileId = null, positionTicks, paused, passive = false }) {
  // keepalive lets the final progress report survive a navigation away.
  return fetch("/API/Stream/Progress", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    keepalive: true,
    body: JSON.stringify({ playSessionId, movieId, playableId, mediaFileId, positionTicks, paused, passive }),
  }).catch(() => {});
}

function stopStream({ playSessionId, movieId = null, playableId = null, mediaFileId = null }) {
  return fetch("/API/Stream/Stop", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    keepalive: true,
    body: JSON.stringify({ playSessionId, movieId, playableId, mediaFileId }),
  }).catch(() => {});
}

// Fire-and-forget Stop for tab close — sendBeacon survives page teardown,
// which is what actually kills the server-side ffmpeg process promptly.
function beaconStopStream({ playSessionId, movieId = null, playableId = null, mediaFileId = null }) {
  const payload = JSON.stringify({ playSessionId, movieId, playableId, mediaFileId });
  if (navigator.sendBeacon) {
    navigator.sendBeacon("/API/Stream/Stop", new Blob([payload], { type: "application/json" }));
  } else {
    stopStream({ playSessionId, movieId, playableId, mediaFileId });
  }
}

function setWatchedState(username, movieID, isActive, kind = "movie") {
  const url = "/API/SetViewingState";

  return fetch(url, {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      Username: username,
      MovieID: movieID,
      Kind: kind,
      SetActive: isActive,
      Action: "SetWatched",
    }),
  });
}

function setWantToWatchState(username, movieID, isActive, kind = "movie") {
  const url = "/API/SetViewingState";

  return fetch(url, {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      Username: username,
      MovieID: movieID,
      Kind: kind,
      SetActive: isActive,
      Action: "SetWantToWatch",
    }),
  });
}

function tmdbLookupImdbID(id) {
  const url = "/API/TMDBLookupImdbID?imdbID=" + id;

  return fetch(url, {
    method: "get",
    headers: {
      "Content-Type": "application/json",
    },
  }).then((response) => {
    return response.json();
  });
}

function tmdbLookupName(name) {
  const url = "/API/TMDBLookupName?name=" + encodeURIComponent(name);

  return fetch(url, {
    method: "get",
    headers: {
      "Content-Type": "application/json",
    },
  }).then((response) => {
    return response.json();
  });
}

function omdbLookupImdbID(id) {
  const url = "/API/OMDBLookupImdbID?imdbID=" + id;

  return fetch(url, {
    method: "get",
    headers: {
      "Content-Type": "application/json",
    },
  }).then((response) => {
    return response.json();
  });
}

function omdbLookupName(name) {
  const url = "/API/OMDBLookupName?name=" + encodeURIComponent(name);

  return fetch(url, {
    method: "get",
    headers: {
      "Content-Type": "application/json",
    },
  }).then((response) => {
    return response.json();
  });
}

function movieLookupFromNames(movieNames, forceBackup = false) {
  const url = `/API/GetMoviesFromNames?forceBackupLogic=${forceBackup}`;

  return fetch(url, {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(movieNames),
  }).then((response) => {
    return response.json();
  });
}

function imdbApiLookupImdbId(id) {
  const url = "/API/ImdbApiLookupImdbID?imdbID=" + encodeURIComponent(id);

  return fetch(url, {
    method: "get",
    headers: {
      "Content-Type": "application/json",
    },
  }).then((response) => {
    return response.json();
  });
}

function imdbApiLookupName(name) {
  const url = "/API/ImdbApiLookupName?name=" + encodeURIComponent(name);

  return fetch(url, {
    method: "get",
    headers: {
      "Content-Type": "application/json",
    },
  }).then((response) => {
    return response.json();
  });
}

function getTotalMovieCount() {
  const url = "/API/GetTotalMovieCount";

  return fetch(url, {
    method: "get",
  });
}

function getMPARatings() {
  return fetch("/API/GetMPARatings");
}

function getGenres() {
  return fetch("/API/GetGenres");
}

function updateBoardgame(game) {
  return fetch("/API/UpdateBoardgame", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      Id: game.id,
      Name: game.name,
      Description: game.description,
      YearPublished: game.yearPublished,
      MinPlayers: game.minPlayers,
      MaxPlayers: game.maxPlayers,
      PlayingTime: game.playingTime,
      MinAge: game.minAge,
      ImageUrl: game.imageUrl ?? null,
      BaseGameId: game.baseGameId ?? null,
    }),
  });
}

function rematchBoardgame(id, newBggThingId) {
  return fetch("/API/RematchBoardgame", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Id: id, NewBggThingId: newBggThingId }),
  });
}

function setUserSetting(key, value) {
  return fetch("/API/SetUserSetting", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ settingKey: key, settingValue: value }),
  });
}

// Upsert the current user's own 0–100 ratings. items: [{ id, kind, value }] where value is 0..100, or
// null to clear (remove the row). Bounded per call — the server caps at 200, so the Rate page sends
// capped chunks and drives the loop. Returns the raw fetch promise ({ success, updated, skipped, deleted }).
function setRatings(items) {
  return fetch("/API/SetRatings", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ items }),
  });
}

// Convenience for the modal slider: set one title's score (value 0..100, or null to clear).
function setRating(id, value, kind = "movie") {
  return setRatings([{ id, kind, value }]);
}

// Full (un-paginated) cards for an explicit movie/series id set — the Rate page loads every watched
// title at once. Mirrors Browse's bare-array GetMoviesByIds call (pageSize defaults to 0 server-side).
function getTitlesByIds(ids) {
  return fetch("/API/GetMoviesByIds", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(ids),
  });
}

// Cards for an explicit MiscVideo id set (the Rate page's misc bars). MiscVideo has its own id space,
// so it needs a dedicated fetch separate from getTitlesByIds.
function getMiscByIds(ids) {
  return fetch("/API/GetMiscByIds", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(ids),
  });
}

function boardgameLookupFromInputs(inputs) {
  return fetch("/API/GetBoardgamesFromInputs", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(inputs),
  }).then((response) => response.json());
}

function insertBoardgameFromBgg(bggThingId) {
  return fetch(`/API/InsertBoardgameFromBgg?bggThingId=${encodeURIComponent(bggThingId)}`, {
    method: "POST",
  });
}

function batchInsertBoardgames(inputs, delayMs = 2000) {
  return fetch(`/API/BatchInsertBoardgames?delayMs=${encodeURIComponent(delayMs)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(inputs),
  });
}

function discoverBoardgameRules(id) {
  return fetch(`/API/DiscoverBoardgameRules?id=${encodeURIComponent(id)}`, { method: "POST" });
}

function approveBoardgameRulesPdf(id, url) {
  return fetch(`/API/ApproveBoardgameRulesPdf?id=${encodeURIComponent(id)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ url }),
  });
}

function removeBoardgameRulesPdf(id, slot) {
  return fetch(`/API/RemoveBoardgameRulesPdf?id=${encodeURIComponent(id)}&slot=${slot}`, { method: "POST" });
}

function removeBoardgameRulesPdfCandidate(id, url) {
  return fetch(`/API/RemoveBoardgameRulesPdfCandidate?id=${encodeURIComponent(id)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ url }),
  });
}

function uploadBoardgameRulesPdf(id, file) {
  const form = new FormData();
  form.append("file", file);
  return fetch(`/API/UploadBoardgameRulesPdf?id=${encodeURIComponent(id)}`, {
    method: "POST",
    body: form,
  });
}

function getSimilarBoardgames(id) {
  return fetch(`/API/SimilarBoardgames?id=${encodeURIComponent(id)}`);
}

function updateBoardgameRules(id, { howToPlayVideoUrls, rulesPdfUrls } = {}) {
  return fetch("/API/UpdateBoardgameRules", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      Id: id,
      HowToPlayVideoUrls: howToPlayVideoUrls,
      RulesPdfUrls: rulesPdfUrls?.map((e) => ({ Url: e.url, Name: e.name })),
    }),
  });
}

// ── Channel administration (streaming-plan.md §8, CanEditMovies-gated) ───────

function getChannelAdminMeta() {
  return fetch("/API/Channel/Admin/Meta");
}

function getChannelAdminPeople(q) {
  return fetch(`/API/Channel/Admin/People?q=${encodeURIComponent(q)}`);
}

function getChannelAdminList() {
  return fetch("/API/Channel/Admin/List");
}

function saveChannel(channel) {
  return fetch("/API/Channel/Admin/Save", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(channel),
  });
}

function deleteChannel(id) {
  return fetch("/API/Channel/Admin/Delete", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Id: id }),
  });
}

function getChannelShelves() {
  return fetch("/API/Channel/Admin/Shelves");
}

function saveChannelShelves(categories) {
  return fetch("/API/Channel/Admin/Shelves", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Categories: categories }),
  });
}

// ── Channel viewer surface (the poster browser, homepage rail, EPG) ──
function getChannelList() {
  return fetch("/API/Channel/List");
}

function getGuideGrid(hours = 6) {
  return fetch(`/API/Channel/GuideGrid?hours=${hours}`);
}

// Channel favorites ride on the generic per-user settings store as a JSON id array.
function setFavoriteChannels(ids) {
  return setUserSetting("FavoriteChannels", JSON.stringify(ids));
}

// ── User administration (admin-only; gated by AdminUsernames config + a
// password-verified session) ────────────────────────────────────────────────

function adminGetUsers() {
  return fetch("/API/Admin/Users");
}

// Pass a null/empty newPassword to clear the user's password.
function adminSetUserPassword(userId, newPassword) {
  return fetch("/API/Admin/SetPassword", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ UserId: userId, NewPassword: newPassword ?? null }),
  });
}

// Pass a null settingValue to delete the setting for that user.
function adminSetUserSetting(userId, settingKey, settingValue) {
  return fetch("/API/Admin/SetUserSetting", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ UserId: userId, SettingKey: settingKey, SettingValue: settingValue ?? null }),
  });
}

// ── Library-ingest review (editor-gated; quarantined rows pending approval) ───
// The bulk library ingest tags new rows with a ReviewBatch and hides them from
// browse; these drive the on-site review queue that approves / rejects / corrects
// them before they join the library.

// scope "gaps" also surfaces already-approved series that still have unmapped/unplayable episodes.
function ingestReviewList(scope) {
  return fetch("/API/Admin/IngestReview/List" + (scope ? "?scope=" + encodeURIComponent(scope) : ""));
}

function ingestReviewDetail(id, kind) {
  return fetch("/API/Admin/IngestReview/Detail?id=" + id + "&kind=" + (kind || "movie"));
}

// ids = movie ids; seriesIds = Series ids; miscIds = MiscVideo ids (separate id sequences — see Kind).
function ingestReviewApprove(ids, seriesIds, miscIds) {
  return fetch("/API/Admin/IngestReview/Approve", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Ids: ids || [], SeriesIds: seriesIds || [], MiscIds: miscIds || [] }),
  });
}

function ingestReviewReject(ids, seriesIds, miscIds) {
  return fetch("/API/Admin/IngestReview/Reject", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Ids: ids || [], SeriesIds: seriesIds || [], MiscIds: miscIds || [] }),
  });
}

// Move a pending row between movie / series / misc (see IngestReviewReclassify).
function ingestReviewReclassify({ id, fromKind, toKind, category, collectionName, relatedMovieId, relatedSeriesId }) {
  return fetch("/API/Admin/IngestReview/Reclassify", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      id,
      FromKind: fromKind || "movie",
      ToKind: toKind || "misc",
      Category: category ?? null,
      CollectionName: collectionName ?? null,
      RelatedMovieId: relatedMovieId ?? null,
      RelatedSeriesId: relatedSeriesId ?? null,
    }),
  });
}

// Fetch posters for already-approved titles that lack one (runs server-side / in prod where images persist).
function ingestReviewBackfillPosters() {
  return fetch("/API/Admin/IngestReview/BackfillPosters", { method: "post" });
}

// One chunk of the Movie/Series poster-namespace repair (gives series their own bucketed poster files).
// Chunked + cursor-driven so it can't time out; the caller loops on the returned { done, nextAfterId }.
// Prod-only (writes the image store).
function ingestReviewMigrateSeriesPosters(afterId = 0, limit = 40) {
  return fetch(`/API/Admin/IngestReview/MigrateSeriesPosters?afterId=${afterId}&limit=${limit}`, { method: "post" });
}

// One chunk of the missing-thumbnail backfill: regenerates "{id}_s.png" from the existing on-disk main
// poster for movies whose thumbnail was never made (modal poster works but the card has no thumbnail).
// Chunked + cursor-driven so it can't time out; the caller loops on { done, nextAfterId }. Prod-only.
function ingestReviewBackfillThumbnails(afterId = 0, limit = 200) {
  return fetch(`/API/Admin/IngestReview/BackfillThumbnails?afterId=${afterId}&limit=${limit}`, { method: "post" });
}

// ── "Sync from Jellyfin" (3 phases the IngestReview button chains) ──
// 1) tell Jellyfin to scan the disk (the periodic scan is disabled for NAS health).
function jellyfinTriggerScan() {
  return fetch("/API/Admin/Jellyfin/TriggerScan", { method: "post" });
}
// 2) poll the scan task state ({ running, progress, found, state }) until it's done.
function jellyfinScanStatus() {
  return fetch("/API/Admin/Jellyfin/ScanStatus");
}
// 3) run the sync that stamps JellyfinItemId onto MediaFile rows (same logic as the sync-jellyfin CLI).
function jellyfinRunSync() {
  return fetch("/API/Admin/Jellyfin/RunSync", { method: "post" });
}

// ── Per-movie "Re-link files from disk" (movie edit page) ──
// The movie's file got replaced (new rip / renamed folder)? 1) scoped re-scan of just this title's shelf;
// 2) poll the probe until the new file is indexed and re-pointed in place (+ any new extras). No full
// library scan, and every attached detail is kept (it all lives on the Movie row, not the file row).
function relinkRefresh(movieId) {
  return fetch(`/API/Admin/Movie/RelinkRefresh?movieId=${movieId}`, { method: "post" });
}
function relinkApply(movieId, shelfItemId) {
  const q = shelfItemId ? `&shelfItemId=${encodeURIComponent(shelfItemId)}` : "";
  return fetch(`/API/Admin/Movie/Relink?movieId=${movieId}${q}`, { method: "post" });
}

// ── Subtitle picker (movie modal) — find/download subtitles via Jellyfin's provider ──
// list current tracks ({ synced, current[] }); search ranked candidates; download a pick; remove a sidecar.
function jellyfinSubtitlesList(movieId) {
  return fetch(`/API/Admin/Jellyfin/Subtitles?movieId=${movieId}`);
}
function jellyfinSubtitlesSearch(movieId, language = "eng") {
  return fetch(`/API/Admin/Jellyfin/Subtitles/Search?movieId=${movieId}&language=${encodeURIComponent(language)}`, { method: "post" });
}
function jellyfinSubtitlesDownload(movieId, subtitleId, language = "eng") {
  return fetch(`/API/Admin/Jellyfin/Subtitles/Download?movieId=${movieId}&subtitleId=${encodeURIComponent(subtitleId)}&language=${encodeURIComponent(language)}`, { method: "post" });
}
function jellyfinSubtitlesDelete(movieId, index) {
  return fetch(`/API/Admin/Jellyfin/Subtitles/Delete?movieId=${movieId}&index=${index}`, { method: "post" });
}

// Generate the thumbnail for ONE title from its existing on-disk poster (movie/series edit modal).
function generateThumbnail(id, isSeries = false) {
  return fetch(`/API/GenerateThumbnail?id=${id}&isSeries=${isSeries}`, { method: "post" });
}

// Point a series episode at the correct on-disk file (chosen from the folder dump); empty path clears it.
function ingestReviewSetEpisodeFile(episodeId, path) {
  return fetch("/API/Admin/IngestReview/SetEpisodeFile", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ EpisodeId: episodeId, Path: path ?? null }),
  });
}

// Assign a file as Primary or Extra, to an episode (targetType "episode", targetId = episodeId) or to the
// series' Extras holder (targetType "series", targetId = seriesId; optional seasonNumber to scope it).
function ingestReviewSetFile({ targetType = "episode", targetId, seasonNumber = null, role = "Primary", path }) {
  return fetch("/API/Admin/IngestReview/SetFile", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ TargetType: targetType, TargetId: targetId, SeasonNumber: seasonNumber, Role: role, Path: path ?? null }),
  });
}

// Delete one mapped file by its MediaFile id (used to drop a wrong Primary or remove an Extra).
function ingestReviewRemoveFile(mediaFileId) {
  return fetch("/API/Admin/IngestReview/RemoveFile", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ MediaFileId: mediaFileId }),
  });
}

// Reorder a file within its title's Primary+Parts sequence. action: "primary" (promote a part/extra/
// variant to Primary), "up" / "down" (shift a part one slot). Server renumbers the sequence afterward.
function ingestReviewMoveFile(mediaFileId, action) {
  return fetch("/API/Admin/IngestReview/MoveFile", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ MediaFileId: mediaFileId, Action: action }),
  });
}

// Mark a live title's file oddity as reviewed so it stops surfacing in the "oddities" scope.
function ingestReviewAcknowledgeOddity(id, kind) {
  return fetch("/API/Admin/IngestReview/AcknowledgeOddity", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Id: id, Kind: kind || "movie" }),
  });
}

function ingestReviewUpdate({ id, kind, title, simpleTitle, year, imdbID, titleType, posterLink }) {
  return fetch("/API/Admin/IngestReview/Update", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      id,
      Kind: kind || "movie",
      Title: title ?? null,
      SimpleTitle: simpleTitle ?? null,
      Year: year === "" || year == null ? null : Number(year),
      imdbID: imdbID ?? null,
      TitleType: titleType ?? null,
      PosterLink: posterLink ?? null,
    }),
  });
}

const MovieAPI = {
  getMoviePoster,
  getPosterThumbnail,
  getMovie,
  getSeries,
  getTitle,
  getFranchiseRail,
  getUsers,
  insertMovie,
  updateMovie,
  updateSeries,
  refetchTitle,
  getTotalMovieCount,
  tmdbLookupImdbID,
  tmdbLookupName,
  omdbLookupImdbID,
  omdbLookupName,
  imdbApiLookupImdbId,
  imdbApiLookupName,
  loginUser,
  getCurrentUser,
  setPassword,
  startStream,
  reportStreamProgress,
  stopStream,
  beaconStopStream,
  setWatchedState,
  setWantToWatchState,
  setRatings,
  setRating,
  getTitlesByIds,
  getMiscByIds,
  movieLookupFromNames,
  getMPARatings,
  getGenres,
  setUserSetting,
  updateBoardgame,
  rematchBoardgame,
  boardgameLookupFromInputs,
  insertBoardgameFromBgg,
  batchInsertBoardgames,
  discoverBoardgameRules,
  approveBoardgameRulesPdf,
  removeBoardgameRulesPdf,
  removeBoardgameRulesPdfCandidate,
  uploadBoardgameRulesPdf,
  updateBoardgameRules,
  getSimilarBoardgames,
  getChannelAdminMeta,
  getChannelAdminPeople,
  getChannelAdminList,
  saveChannel,
  getChannelShelves,
  saveChannelShelves,
  deleteChannel,
  getChannelList,
  getGuideGrid,
  setFavoriteChannels,
  adminGetUsers,
  adminSetUserPassword,
  adminSetUserSetting,
  ingestReviewList,
  ingestReviewDetail,
  ingestReviewApprove,
  ingestReviewReject,
  ingestReviewUpdate,
  ingestReviewReclassify,
  ingestReviewBackfillPosters,
  ingestReviewMigrateSeriesPosters,
  ingestReviewBackfillThumbnails,
  jellyfinTriggerScan,
  jellyfinScanStatus,
  jellyfinRunSync,
  relinkRefresh,
  relinkApply,
  jellyfinSubtitlesList,
  jellyfinSubtitlesSearch,
  jellyfinSubtitlesDownload,
  jellyfinSubtitlesDelete,
  generateThumbnail,
  ingestReviewSetEpisodeFile,
  ingestReviewSetFile,
  ingestReviewRemoveFile,
  ingestReviewMoveFile,
  ingestReviewAcknowledgeOddity,
};

export { MovieAPI };
