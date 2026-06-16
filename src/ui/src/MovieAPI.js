import { detectStreamCapabilities } from "./streamCapabilities";

function getMoviePoster(id, posterVersion) {
  return posterVersion ? `/Image/${id}?v=${posterVersion}` : `/Image/${id}`;
}

function getPosterThumbnail(id, posterVersion) {
  return posterVersion ? `/ImageThumb/${id}?v=${posterVersion}` : `/ImageThumb/${id}`;
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

// Restores the session from the auth cookie without re-running login.
function getCurrentUser() {
  return fetch("/API/Me");
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
function startStream({ movieId = null, playableId = null, mediaFileId = null, maxBitrateBps = null, audioStreamIndex = null, subtitleStreamIndex = null, startSeconds = null }) {
  // Negotiate the codec profile from this browser's real capabilities (§14.1) so
  // HEVC/AV1-capable clients avoid a needless H.264 re-encode.
  const caps = detectStreamCapabilities();
  return fetch("/API/Stream/Start", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ movieId, playableId, mediaFileId, maxBitrateBps, audioStreamIndex, subtitleStreamIndex, startSeconds, ...caps }),
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

function ingestReviewList() {
  return fetch("/API/Admin/IngestReview/List");
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
  getUsers,
  insertMovie,
  updateMovie,
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
  getChannelAdminList,
  saveChannel,
  deleteChannel,
  adminGetUsers,
  adminSetUserPassword,
  adminSetUserSetting,
  ingestReviewList,
  ingestReviewDetail,
  ingestReviewApprove,
  ingestReviewReject,
  ingestReviewUpdate,
  ingestReviewReclassify,
};

export { MovieAPI };
