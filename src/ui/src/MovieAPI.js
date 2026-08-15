import { detectStreamCapabilities } from "./streamCapabilities";

// ── Music (music-plan.md §2.4) ──────────────────────────────────────────────
// The artist/album catalogs ship whole (they're small) and filter client-side;
// only song search and per-album tracklists round-trip.

// The shelf facet (MusicArtist.Kind). Sending nothing means "music" — the server's default — so the
// ordinary library call is unchanged and only the Comedy/Audiobooks shelves carry a parameter.
function kindQuery(kind) {
  return kind ? `&kind=${encodeURIComponent(kind)}` : "";
}

function getMusicArtists(kind) {
  return fetch(`/API/Music/Artists${kind ? `?kind=${encodeURIComponent(kind)}` : ""}`, { method: "get" });
}

function getMusicArtist(id) {
  return fetch(`/API/Music/Artist/${id}`, { method: "get" });
}

function getMusicAlbums(kind) {
  return fetch(`/API/Music/Albums?pageSize=5000${kindQuery(kind)}`, { method: "get" });
}

function getMusicAlbum(id) {
  return fetch(`/API/Music/Album/${id}`, { method: "get" });
}

function searchMusicTracks(q, kind) {
  return fetch(`/API/Music/Search?q=${encodeURIComponent(q)}${kindQuery(kind)}`, { method: "get" });
}

// What this server can play: { streamingConfigured, transcodeEnabled }. Asked once, so the UI
// can offer .wma/.aif tracks when the gateway will transcode them instead of always greying
// them out.
function getMusicCapabilities() {
  return fetch("/API/Music/Capabilities", { method: "get" });
}

// Mints the signed gateway URL for one track (the audio data plane — bytes come
// straight off the StreamGateway, never this server).
function startMusicTrack(trackId) {
  return fetch("/API/Music/Stream/Start", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ trackId }),
  });
}

// The same thing for N tracks in one round trip: { tracks: [...same payload as above], skipped: [ids] }
// (music-mse-plan.md §"URLs: minted while awake, never needed while asleep"). Minting is signing,
// nothing more, so signing a queue's worth ahead of time is free — and the reason to want it is that
// a mint is a JS fetch, which is the first thing a backgrounded phone stops being allowed to run.
// Unknown/unplayable ids come back in `skipped` rather than failing the whole batch.
function startMusicTracks(trackIds) {
  return fetch("/API/Music/Stream/StartBatch", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ trackIds }),
  });
}

// Tracks for the MSE probe page to test itself with: { mp3, flac, hires, mono }, any of them null
// when the library has nothing to fill that slot (music-mse-plan.md §Phase 1). The server chooses,
// because the gate has to be "open the page, press one thing, put the phone down" — and nobody knows
// by heart which of their tracks is the 96 kHz one.
function getMusicProbeCandidates() {
  return fetch("/API/Music/Probe/Candidates", { method: "get" });
}

// Lyrics for one track: { plainText, syncedLrc, source } or 404 when we have none (§2.7).
function getMusicTrackLyrics(trackId) {
  return fetch(`/API/Music/Track/${trackId}/Lyrics`);
}

// Album art, served from the images mount (§2.5). ?v= only when art exists, so a card that
// gains art later stops being served the cached 404.
function getMusicAlbumArt(albumId, hasArt) {
  return hasArt ? `/MusicImage/${albumId}?v=1` : `/MusicImage/${albumId}`;
}

function getMusicAlbumArtThumb(albumId, hasArt) {
  return hasArt ? `/MusicImageThumb/${albumId}?v=1` : `/MusicImageThumb/${albumId}`;
}

// ── Music playlists (music-plan.md §2.4, Phase 3) ───────────────────────────
// Their own endpoints over the Music* tables — the /API/Channel/Playlist/* verbs above are the
// TV ones (video playables), same shape, different storage.

function createMusicPlaylist(name, trackIds) {
  return fetch("/API/Music/Playlist/Create", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name, trackIds }),
  });
}

// The caller's playlists: [{ id, name, count, trackTitles[], albumIds[] }].
function getMyMusicPlaylists() {
  return fetch("/API/Music/Playlist/Mine");
}

// A playlist's ordered tracks, already shaped like queue entries.
function getMusicPlaylistItems(id) {
  return fetch(`/API/Music/Playlist/${id}/Items`);
}

function addMusicPlaylistItems(id, trackIds) {
  return fetch(`/API/Music/Playlist/${id}/AddItems`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ trackIds }),
  });
}

// Replace the whole ordered lineup (covers reorder + remove).
function setMusicPlaylistItems(id, trackIds) {
  return fetch(`/API/Music/Playlist/${id}/SetItems`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ trackIds }),
  });
}

function renameMusicPlaylist(id, name) {
  return fetch(`/API/Music/Playlist/${id}/Rename`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name }),
  });
}

// Sharing (music-plan.md §2.4). A share is collaborative — members edit; only the owner may
// delete the playlist or change who has access.
function getMusicPlaylistShares(id) {
  return fetch(`/API/Music/Playlist/${id}/Shares`, { method: "get" });
}

function shareMusicPlaylist(id, userIds) {
  return fetch(`/API/Music/Playlist/${id}/Share`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ userIds }),
  });
}

function unshareMusicPlaylist(id, userIds) {
  return fetch(`/API/Music/Playlist/${id}/Unshare`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ userIds }),
  });
}

// Accounts a playlist can be shared with (password-holders only — streaming is password-gated).
function getMusicShareTargets() {
  return fetch("/API/Music/ShareTargets", { method: "get" });
}

function deleteMusicPlaylist(id) {
  return fetch(`/API/Music/Playlist/${id}/Delete`, { method: "post" });
}

// ── Favorites ───────────────────────────────────────────────────────────────
// The heart in the play bar. Server-side it's an ordinary playlist carrying the IsFavorites flag,
// which is what makes it un-shareable, un-renameable and permanent.

// Every favorited track id: { playlistId, trackIds }. playlistId is null until the first heart —
// the list is created lazily, and "no list" means "nothing favorited".
function getMusicFavorites() {
  return fetch("/API/Music/Favorites", { method: "get" });
}

// Sends the state it WANTS, not a flip, so a double-tap can't invert the result.
function setMusicFavorite(trackId, favorite) {
  return fetch("/API/Music/Favorite", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ trackId, favorite }),
  });
}

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
// A stable id for THIS browser, sent with every stream call. Jellyfin derives a transcode's output
// directory from (media, params, device id) and runs one ffmpeg per directory — so when the whole site
// shared one device id, two screens on the same title fought over the same segment files: each start
// killed the other's ffmpeg and rewrote its init segment mid-playback, freezing the (copied) video
// while the re-encoded audio played on. One id per browser keeps them apart. Persisted so a reload
// doesn't strand the transcode it started under the previous id.
const DEVICE_TOKEN_KEY = "mt-device-token";
const fallbackDeviceToken = `t${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
let cachedDeviceToken = null;
function deviceToken() {
  if (cachedDeviceToken) return cachedDeviceToken;
  try {
    let token = window.localStorage.getItem(DEVICE_TOKEN_KEY);
    if (!token) {
      token = (window.crypto?.randomUUID?.() || fallbackDeviceToken).replace(/-/g, "");
      window.localStorage.setItem(DEVICE_TOKEN_KEY, token);
    }
    cachedDeviceToken = token;
  } catch {
    // Storage blocked (private mode): a per-tab token still separates this screen from every other.
    cachedDeviceToken = fallbackDeviceToken;
  }
  return cachedDeviceToken;
}

function startStream({ movieId = null, playableId = null, mediaFileId = null, maxBitrateBps = null, audioStreamIndex = null, subtitleStreamIndex = null, startSeconds = null, forceTranscode = false }) {
  // Negotiate the codec profile from this browser's real capabilities (§14.1) so
  // HEVC/AV1-capable clients avoid a needless H.264 re-encode.
  const caps = detectStreamCapabilities();
  return fetch("/API/Stream/Start", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ movieId, playableId, mediaFileId, maxBitrateBps, audioStreamIndex, subtitleStreamIndex, startSeconds, forceTranscode, deviceToken: deviceToken(), ...caps }),
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
    body: JSON.stringify({ playSessionId, movieId, playableId, mediaFileId, positionTicks, paused, passive, deviceToken: deviceToken() }),
  }).catch(() => {});
}

function stopStream({ playSessionId, movieId = null, playableId = null, mediaFileId = null }) {
  return fetch("/API/Stream/Stop", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    keepalive: true,
    body: JSON.stringify({ playSessionId, movieId, playableId, mediaFileId, deviceToken: deviceToken() }),
  }).catch(() => {});
}

// Fire-and-forget Stop for tab close — sendBeacon survives page teardown,
// which is what actually kills the server-side ffmpeg process promptly.
function beaconStopStream({ playSessionId, movieId = null, playableId = null, mediaFileId = null }) {
  const payload = JSON.stringify({ playSessionId, movieId, playableId, mediaFileId, deviceToken: deviceToken() });
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

// Single-channel metadata (name/category), age-gated but not filtered by shelf visibility — lets the
// player tune a channel it reached by id (e.g. a watch-party channel, hidden from the guide list).
function getChannelMeta(id) {
  return fetch(`/API/Channel/${id}/Meta`);
}

// ── User playlists & watch parties (docs/playlists-watchparty-plan.md) ────────
// A playlist is a private, user-owned channel whose lineup is an explicit ordered list of playables.
// A watch party is the same thing with `watchparty:true`, which returns a shareable token.

// Create a playlist (or watch party). items = playable ids in order. Returns { id, name, watchpartyToken, count }.
function createPlaylist(name, items, watchparty = false) {
  return fetch("/API/Channel/Playlist/Create", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name, items, watchparty }),
  });
}

// The caller's playlists: [{ id, name, count, watchpartyToken, posters[] }].
function getMyPlaylists() {
  return fetch("/API/Channel/Playlist/Mine");
}

// A playlist's full ordered lineup with titles/posters — for the manage view.
function getPlaylistItems(id) {
  return fetch(`/API/Channel/Playlist/${id}/Items`);
}

// Append playable ids to the end of a playlist.
function addPlaylistItems(id, items) {
  return fetch(`/API/Channel/Playlist/${id}/AddItems`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ items }),
  });
}

// Replace a playlist's whole ordered lineup (covers reorder + remove).
function setPlaylistItems(id, items) {
  return fetch(`/API/Channel/Playlist/${id}/SetItems`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ items }),
  });
}

function renamePlaylist(id, name) {
  return fetch(`/API/Channel/Playlist/${id}/Rename`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name }),
  });
}

function deletePlaylist(id) {
  return fetch(`/API/Channel/Playlist/${id}/Delete`, { method: "post" });
}

// Resolve a watch-party invite token → lobby state { channelId, name, started, amHost, itemCount, roster[] }.
function getWatchparty(token) {
  return fetch(`/API/Watchparty/${encodeURIComponent(token)}`);
}

// Lobby presence heartbeat (also returns the latest lobby state); the lobby polls it.
function watchpartyHeartbeat(token) {
  return fetch(`/API/Watchparty/${encodeURIComponent(token)}/Heartbeat`, { method: "post" });
}

// Toggle this member's ready flag; when everyone present is ready the party auto-begins.
function watchpartyReady(token, ready) {
  return fetch(`/API/Watchparty/${encodeURIComponent(token)}/Ready`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ ready }),
  });
}

// Host force-start (or start once everyone is ready).
function watchpartyBegin(token) {
  return fetch(`/API/Watchparty/${encodeURIComponent(token)}/Begin`, { method: "post" });
}

function leaveWatchparty(token) {
  return fetch(`/API/Watchparty/${encodeURIComponent(token)}/Leave`, { method: "post", keepalive: true }).catch(() => {});
}

// Fire-and-forget leave for tab close — sendBeacon survives page teardown (mirrors the arcade).
function beaconLeaveWatchparty(token) {
  const url = `/API/Watchparty/${encodeURIComponent(token)}/Leave`;
  if (navigator.sendBeacon) {
    navigator.sendBeacon(url, new Blob([""], { type: "application/json" }));
  } else {
    leaveWatchparty(token);
  }
}

// ── Arcade (docs/arcade-plan.md §6) — retro multiplayer control plane ─────────
// All same-origin, cookie-authed like the rest. The heavy lifting (WebRTC media +
// input) is NOT here — it rides the CloudRetro client shim straight to the gateway.

function arcadeQuery(params) {
  const q = new URLSearchParams();
  Object.entries(params).forEach(([k, v]) => {
    if (v != null && v !== "" && v !== "all") q.set(k, v);
  });
  const qs = q.toString();
  return qs ? `?${qs}` : "";
}

// Server-side filtered + paged (the catalog is ~17k cards). params: { system, region, maxPlayers,
// variant, genre, sort, search, skip | page, pageSize }. `skip` is an absolute catalog offset and
// wins over `page` — it's what lets the lobby's pager seek straight to a letter bucket, which starts
// mid-page. Response: { games, totalCount, page, pageSize, skip }.
function getArcadeGames(params = {}, signal) {
  return fetch("/API/Arcade/Games" + arcadeQuery(params), { signal });
}

// A–Z bucket sizes + offsets for the SAME filtered catalog, in alphabetical order:
// { total, letters: [{ letter, count, offset }] }. The lobby pager turns an offset into a jump.
function getArcadeGameLetters(params = {}) {
  // Letters are a property of the filter set, not of the sort or the paging window.
  const filters = { ...params };
  delete filters.sort;
  delete filters.page;
  delete filters.pageSize;
  delete filters.skip;
  return fetch("/API/Arcade/GameLetters" + arcadeQuery(filters));
}

// Facets for the lobby filter controls: { total, multiplayer, systems[], regions[], variants[] } (each
// { value, count }). Faceted — pass the CURRENT filter scope (system, region, maxPlayers, variant, genre,
// search) so each dropdown reflects what the grid would actually show; e.g. a Japan-only system isn't
// offered under the default English region. Sort/paging are irrelevant to facets and stripped.
function getArcadeFilters(params = {}) {
  const filters = { ...params };
  delete filters.sort;
  delete filters.page;
  delete filters.pageSize;
  delete filters.skip;
  return fetch("/API/Arcade/Filters" + arcadeQuery(filters));
}

function getArcadeRooms() {
  return fetch("/API/Arcade/Rooms");
}

// Create a room for a game → returns the creator's join descriptor (empty room_id, isCreator).
// paceMs: null = no deliberate Network pick (server applies lane defaults — capture 8ms, GL 0);
// an explicit 0 means "pacing off" and is honored even on capture. Do NOT default it to 0.
function createArcadeRoom(gameId, { newGame = false, seedSlot = 0, videoBitrateKbps = 0, audioFec = 0, paceMs = null, cheats = [], videoCodec = "", hwContext = "", renderProfile = "", controllerScheme = "", competitive = false } = {}) {
  return fetch("/API/Arcade/Room", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ gameId, newGame, seedSlot, videoBitrateKbps, audioFec, paceMs, cheats, videoCodec, hwContext, renderProfile, controllerScheme, competitive }),
  });
}

// ── RetroAchievements account link + boards ──────────────────────────────────────────────────────
// Each user links their OWN retroachievements.org account (RA ToS). Status drives the settings panel.
function getRetroAchievementsStatus() {
  return fetch("/API/Arcade/RetroAchievements/Status").then((r) => (r.ok ? r.json() : { linked: false })).catch(() => ({ linked: false }));
}
function linkRetroAchievements(username, password) {
  return fetch("/API/Arcade/RetroAchievements/Link", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password }),
  });
}
function unlinkRetroAchievements() {
  return fetch("/API/Arcade/RetroAchievements/Link", { method: "delete" });
}
// Friends-only leaderboards for a game card (our mirror of RA submissions), grouped by RA board.
function getArcadeLeaderboards(gameId) {
  return fetch(`/API/Arcade/Game/${gameId}/Leaderboards`).then((r) => (r.ok ? r.json() : { boards: [] })).catch(() => ({ boards: [] }));
}
// A user's mirrored RA achievement unlocks, paged newest-first.
function getArcadeUserAchievements(userId, params = {}) {
  return fetch(`/API/Arcade/Users/${userId}/Achievements` + arcadeQuery(params))
    .then((r) => (r.ok ? r.json() : { rows: [], totalCount: 0, totalPoints: 0 }))
    .catch(() => ({ rows: [], totalCount: 0, totalPoints: 0 }));
}
// A user's real RA profile pulled from retroachievements.org (points, rank, recent). Omit userId for self.
// { configured, linked, available, raUser, totalPoints, rank, recent:[...] }.
function getRetroAchievementsProfile(userId) {
  const q = userId ? `?userId=${userId}` : "";
  return fetch(`/API/Arcade/RetroAchievements/Profile${q}`)
    .then((r) => (r.ok ? r.json() : { configured: false, linked: false }))
    .catch(() => ({ configured: false, linked: false }));
}
// Every achievement that EXISTS for a game (RA), with the signed-in user's earned overlay + badges.
// { configured, available, raGameId, title, imageIcon, achievements:[{ id,title,description,points,badgeUrl,earned,earnedCompetitive,earnedUtc,legit,... }], earnedCount, pointsEarned, pointsTotal }.
// `legit` = OBSERVED clean (no cheat/savescum/timeplay); `earnedCompetitive` = room mode, provenance only.
function getArcadeGameAchievements(gameId) {
  return fetch(`/API/Arcade/Game/${gameId}/Achievements`)
    .then((r) => (r.ok ? r.json() : { available: false, achievements: [] }))
    .catch(() => ({ available: false, achievements: [] }));
}
// The trophy-room summary: games the user has earned achievements in, collapsed across versions.
// { userId, totalPoints, totalEarned, gameCount, games:[{ gameId,title,system,earnedCount,points,competitiveCount,legitCount,lastUnlockedUtc }] }.
function getArcadeUserTrophies(userId) {
  return fetch(`/API/Arcade/Users/${userId}/Trophies`)
    .then((r) => (r.ok ? r.json() : { games: [], totalPoints: 0, totalEarned: 0, gameCount: 0 }))
    .catch(() => ({ games: [], totalPoints: 0, totalEarned: 0, gameCount: 0 }));
}
// The signed-in user's OWN trophy room (self-scoped via the auth cookie — no user id needed).
function getMyArcadeTrophies() {
  return fetch(`/API/Arcade/Trophies/Mine`)
    .then((r) => (r.ok ? r.json() : { games: [], totalPoints: 0, totalEarned: 0, gameCount: 0 }))
    .catch(() => ({ games: [], totalPoints: 0, totalEarned: 0, gameCount: 0 }));
}

// The render profiles (core-and-renderer combinations) offered per system, for the play-button
// launch menu: { "n64": [{ id, label, isDefault }], "ps1": [...], ... }. Static; fetched once.
function getArcadeRenderers() {
  return fetch("/API/Arcade/Renderers").then((r) => (r.ok ? r.json() : {})).catch(() => ({}));
}

// The signed-in user's durable saves for a game (arcade-saves-plan) — drives the resume picker.
function listArcadeSaves(gameId) {
  return fetch(`/API/Arcade/Games/${gameId}/Saves`).then((r) => (r.ok ? r.json() : [])).catch(() => []);
}

// Games the signed-in user has recently played (derived from save activity), newest first:
// [{ game, lastPlayedUtc, saveCount, playedVersionId }]. `game` is the SAME full card payload
// /API/Arcade/Games returns, because a recent tile opens the same game modal as a grid card.
function getArcadeRecentlyPlayed(take = 12) {
  return fetch(`/API/Arcade/RecentlyPlayed?take=${take}`).then((r) => (r.ok ? r.json() : [])).catch(() => []);
}

// The signed-in user's saves across EVERY game (the saves vault) — paged/searchable/filterable.
// params: { search, system, skip, take }. Response: { rows, totalCount, totalSizeBytes, skip, take }.
function getAllArcadeSaves(params = {}) {
  return fetch("/API/Arcade/Saves/Mine" + arcadeQuery(params));
}

// Arcade host health: is Ziggy's desktop session on its physical console, or is a remote desktop
// holding it off (which halves capture/render rate with no error anywhere)? In-memory server-side,
// so this is cheap enough to poll from the lobby and from inside a room.
function getArcadeHostStatus() {
  return fetch("/API/Arcade/HostStatus").then((r) => (r.ok ? r.json() : null)).catch(() => null);
}

// ── Heavy lane (Moonlight-streamed titles, docs/arcade-heavy-lane-plan.md §7) ────────────────────
// Lane status: who holds the one heavy session + per-app staging state for the heavy cards.
function getArcadeHeavyStatus() {
  return fetch("/API/Arcade/Heavy/Status");
}
// Advance ONE staging chunk; the caller loops until state === "done" (bounded, resumable, chunked).
function stageArcadeHeavy(gameId) {
  return fetch(`/API/Arcade/Heavy/Stage/${gameId}`, { method: "post" });
}
// Complete a Moonlight pairing PIN (editor-gated server-side).
function pairArcadeHeavy(pin, deviceName) {
  return fetch("/API/Arcade/Heavy/Pair", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ pin, deviceName }),
  });
}

// Cheats offered for ONE version (ROM) of a game — lazy-loaded when the card's picker opens, because a
// popular title carries hundreds of community codes.
function getArcadeCheats(gameId) {
  return fetch(`/API/Arcade/Game/${gameId}/Cheats`)
    .then((r) => (r.ok ? r.json() : null))
    .then((d) => (d && Array.isArray(d.cheats) ? d.cheats : []))
    .catch(() => []);
}

// Per-game config tool (editor-only). GET returns { system, hwToggleSupported, renderer, notes,
// options:[{key,label,category,note,values:[{token,label}],default,value,isRange,rangeMin,rangeMax}],
// advanced:{key:value} } — each option carries its current effective value. Returns the Response so the
// caller can distinguish 403 (not an editor). Save takes { coreOptions:{k:v}, renderer, notes }.
function getArcadeGameConfig(gameId, profile) {
  const q = profile ? `?profile=${encodeURIComponent(profile)}` : "";
  return fetch(`/API/Arcade/Game/${gameId}/Config${q}`);
}
function saveArcadeGameConfig(gameId, body) {
  return fetch(`/API/Arcade/Game/${gameId}/Config`, {
    method: "put",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

// My Saves management (arcade-saves-plan S3).
function deleteArcadeSave(id) {
  return fetch(`/API/Arcade/Saves/${id}`, { method: "delete" });
}
function renameArcadeSave(id, label) {
  return fetch(`/API/Arcade/Saves/${id}`, {
    method: "put",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ label }),
  });
}
function arcadeSaveDownloadUrl(id) {
  return `/API/Arcade/Saves/${id}/download`;
}
function importArcadeSave(gameId, file, { kind = "state", label = "" } = {}) {
  const fd = new FormData();
  fd.append("file", file);
  fd.append("kind", kind);
  if (label) fd.append("label", label);
  return fetch(`/API/Arcade/Games/${gameId}/Saves/import`, { method: "post", body: fd });
}

// Report the CloudRetro room id the creator's browser got back from GAME_START (§8 step 3).
function bindArcadeRoom(code, cloudRetroRoomId) {
  return fetch(`/API/Arcade/Room/${encodeURIComponent(code)}/Bind`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ cloudRetroRoomId }),
  });
}

// Join an existing room → returns a join descriptor with the bound room_id and an assigned seat.
function joinArcadeRoom(code) {
  return fetch(`/API/Arcade/Room/${encodeURIComponent(code)}/Join`, { method: "post" });
}

// Presence heartbeat + room status ({ bound, maxPlayers, yourSlot, players[] }); the room page polls it.
// Local multiplayer: claim an extra controller port in a room you're already playing in (one per
// extra local pad); release it when that local player leaves.
function claimArcadeSeat(code) {
  return fetch(`/API/Arcade/Room/${encodeURIComponent(code)}/ClaimSeat`, { method: "post" });
}

function releaseArcadeSeat(code, slot) {
  return fetch(`/API/Arcade/Room/${encodeURIComponent(code)}/ReleaseSeat`, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ slot }),
  }).catch(() => {});
}

function arcadeHeartbeat(code) {
  return fetch(`/API/Arcade/Room/${encodeURIComponent(code)}/Heartbeat`, { method: "post" }).catch(() => {});
}

function leaveArcadeRoom(code) {
  return fetch(`/API/Arcade/Room/${encodeURIComponent(code)}/Leave`, { method: "post", keepalive: true }).catch(() => {});
}

// Fire-and-forget leave for tab close — sendBeacon survives page teardown (mirrors beaconStopStream).
function beaconLeaveArcadeRoom(code) {
  const url = `/API/Arcade/Room/${encodeURIComponent(code)}/Leave`;
  if (navigator.sendBeacon) {
    navigator.sendBeacon(url, new Blob([""], { type: "application/json" }));
  } else {
    leaveArcadeRoom(code);
  }
}

// ── User administration (admin-only; gated by AdminUsernames config + a
// password-verified session) ────────────────────────────────────────────────

function adminGetUsers() {
  return fetch("/API/Admin/Users");
}

// Are our PATCHED binaries still patched? (hand-built/byte-patched arcade cores, nightly-pinned
// cores, the 3 patched Jellyfin DLLs). Reported by Ziggy's arcade watchdog every 30 min; a revert is
// otherwise SILENT — the worker's core sync reinstalls stock over any missing core, and a stock
// Jellyfin upgrade wipes its DLLs. Also returns staleness, because a dead watchdog is its own alarm.
function adminGetPatchedArtifacts() {
  return fetch("/API/Admin/PatchedArtifacts");
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

// ── Family photo album (docs/photos-plan.md §2.1) ─────────────────────────────
// Every /API/Photos route sits behind the RequireFamilyAlbum policy, so a non-member gets 403 and an
// anonymous caller 401 — callers must handle both rather than assuming the nav already filtered.
function getPhotosStatus() {
  return fetch("/API/Photos/Status", { cache: "no-store" });
}

// Keyset-paged (TakenAt DESC, Id DESC). `undated` is a SEPARATE shelf, not a filter on the same
// stream — date-unknown items are never interleaved (§2.7), so the two modes carry their own cursors.
function getPhotosTimeline({ beforeTakenAt, beforeId, take, undated, includeHidden } = {}) {
  const params = new URLSearchParams();
  if (beforeTakenAt) params.set("beforeTakenAt", beforeTakenAt);
  // beforeId 0 is a REAL cursor, not an absent one: a year jump seeds (Jan 1 of year+1, id 0) so the
  // server's tie-break predicate degenerates to a clean strictly-before seek. A truthiness test here
  // silently dropped that cursor and every jump landed back at the newest page.
  if (beforeId != null) params.set("beforeId", beforeId);
  if (take) params.set("take", take);
  if (undated) params.set("undated", "true");
  // The timeline excludes hidden items by default (§2.9); a member can ask to see them.
  if (includeHidden) params.set("includeHidden", "true");
  return fetch("/API/Photos/Timeline?" + params.toString(), { cache: "no-store" });
}

// The year index behind the timeline's scrubber rail: which years hold photographs (post-exclusion
// counts, so the rail's promises match what the page shows) and the size of the date-unknown shelf.
function getPhotosTimelineYears({ includeHidden } = {}) {
  const params = new URLSearchParams();
  if (includeHidden) params.set("includeHidden", "true");
  return fetch("/API/Photos/TimelineYears?" + params.toString(), { cache: "no-store" });
}

// includeHidden is honoured only for an admin (Phase 4 addendum); a member's request is ignored
// rather than refused, so a stale tab gets the curated album instead of a probe-able 403.
function getPhotosFolder({ path, skip, take, includeHidden } = {}) {
  const params = new URLSearchParams();
  if (path) params.set("path", path);
  if (skip) params.set("skip", skip);
  if (take) params.set("take", take);
  if (includeHidden) params.set("includeHidden", "true");
  return fetch("/API/Photos/Folders?" + params.toString(), { cache: "no-store" });
}

function getPhotoAsset(id) {
  return fetch("/API/Photos/Asset/" + id, { cache: "no-store" });
}

function getPhotosIngestStatus() {
  return fetch("/API/Photos/IngestStatus", { cache: "no-store" });
}

// ── Curation + albums (docs/photos-plan.md §2.9, §2.5) ────────────────────────
// Curation is rows and flags only. Hiding excludes a photo from the timeline and from albums and
// leaves it in the folder view; NOTHING here ever touches a file on disk.

function photosPost(url, body) {
  return fetch(url, {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body ?? {}),
  });
}

function setPhotosHidden(ids, hidden) {
  return photosPost("/API/Photos/Hide", { ids, hidden });
}

// §2.12: which SHELF photos are filed on — the family timeline, or the Gallery of art and memes.
// Member-permitted, exactly like hiding: deciding a picture is art is ordinary curation. The move is
// group-coherent server-side (a settled duplicate group changes shelf as a unit), and the response
// reports how many extra rows came along.
function setPhotosShelf(ids, shelf) {
  return photosPost("/API/Photos/Shelf", { ids, shelf });
}

function getPhotosHideProposals(includeDecided) {
  return fetch("/API/Photos/HideProposals?includeDecided=" + (includeDecided ? "true" : "false"), { cache: "no-store" });
}

function getPhotosHideProposal(batchId, { skip, take } = {}) {
  const params = new URLSearchParams();
  if (skip) params.set("skip", skip);
  if (take) params.set("take", take);
  return fetch(`/API/Photos/HideProposal/${encodeURIComponent(batchId)}?` + params.toString(), { cache: "no-store" });
}

// One action for a whole batch — §2.9's "human-confirmed batch-wise". Rejecting writes nothing.
function decidePhotosHideProposal(batchId, decision) {
  return photosPost(`/API/Photos/HideProposal/${encodeURIComponent(batchId)}/${decision}`);
}

function getPhotosIngestBatches() {
  return fetch("/API/Photos/IngestBatches", { cache: "no-store" });
}

function approvePhotosIngestBatches({ groupKey, batchIds } = {}) {
  return photosPost("/API/Photos/IngestBatches/Approve", { groupKey, batchIds });
}

function getPhotoAlbums() {
  return fetch("/API/Photos/Albums", { cache: "no-store" });
}

// §2.12: the Gallery index — the SAME shape as getPhotoAlbums, over the archive shelf. Two calls
// rather than one with a parameter because they are two sections with two URLs, and a caller that
// forgot the parameter would silently render the wrong shelf.
function getPhotoGallery() {
  return fetch("/API/Photos/Gallery", { cache: "no-store" });
}

function getPhotoAlbum(slug, { skip, take } = {}) {
  const params = new URLSearchParams();
  if (skip) params.set("skip", skip);
  if (take) params.set("take", take);
  return fetch(`/API/Photos/Album/${encodeURIComponent(slug)}?` + params.toString(), { cache: "no-store" });
}

// The slug is minted server-side; assetIds creates from a selection and fromFolder seeds from a
// folder (which COPIES membership — the folder is never the album's identity).
// `shelf` (§2.12) lets a selection become a GALLERY collection in one action instead of a create
// followed by a trip through the album's Edit panel.
function createPhotoAlbum({ title, description, assetIds, fromFolder, shelf, artistName } = {}) {
  return photosPost("/API/Photos/Albums", { title, description, assetIds, fromFolder, shelf, artistName });
}

function updatePhotoAlbum(id, body) {
  return photosPost(`/API/Photos/Album/${id}/Update`, body);
}

function addToPhotoAlbum(id, { assetIds, fromFolder } = {}) {
  return photosPost(`/API/Photos/Album/${id}/Add`, { assetIds, fromFolder });
}

function removeFromPhotoAlbum(id, assetIds) {
  return photosPost(`/API/Photos/Album/${id}/Remove`, { assetIds });
}

// A PARTIAL list is fine: the ids sent take the front, everything else keeps its order behind them.
function reorderPhotoAlbum(id, assetIds) {
  return photosPost(`/API/Photos/Album/${id}/Reorder`, { assetIds });
}

// Rows only — an album has never held a file. The confirm is because it is hand-built curation.
function deletePhotoAlbum(id) {
  return photosPost(`/API/Photos/Album/${id}/Delete`, { confirm: true });
}

function getPhotoAssetAlbums(id) {
  return fetch(`/API/Photos/Asset/${id}/Albums`, { cache: "no-store" });
}

// ── Duplicate review (docs/photos-plan.md §2.6) ───────────────────────────────
// Member-visible, like the rest of curation. Variant pairs (RAW+JPEG, motion photos, Live Photos)
// are settled by the grouping pass and are never listed here — they need no human, and asking which
// of a RAW and its JPEG is "better" would collapse the half a browser can render.
function getPhotoDupeGroups({ status, kind, skip, take } = {}) {
  const params = new URLSearchParams();
  if (status) params.set("status", status);
  if (kind) params.set("kind", kind);
  if (skip) params.set("skip", skip);
  if (take) params.set("take", take);
  return fetch("/API/Photos/DupeGroups?" + params.toString(), { cache: "no-store" });
}

// Picking a master collapses the other copies out of the timeline and albums immediately. Rows and
// flags only: every copy is still on disk and still in the folder view.
function resolvePhotoDupeGroup(id, masterAssetId) {
  return photosPost(`/API/Photos/DupeGroup/${id}/Resolve`, { masterAssetId });
}

// "Not the same photo." The group survives as a tombstone so the grouping pass never re-proposes it.
function rejectPhotoDupeGroup(id) {
  return photosPost(`/API/Photos/DupeGroup/${id}/Reject`);
}

// ── People, tagging and the tag queue (docs/photos-plan.md §2.7, §2.8) ────────
// People live in DB rows and nowhere else. Tags attach to a duplicate group's MASTER — the server
// redirects and REPORTS that it did, so "I tagged three copies and got one tag" has an answer.
// Suggestions never auto-confirm: confirm promotes the row, reject leaves a tombstone so a re-sync
// does not propose the identical face again.
function getPhotoPeople() {
  return fetch("/API/Photos/People", { cache: "no-store" });
}

function createPhotoPerson({ name, birthYear } = {}) {
  return photosPost("/API/Photos/People", { name, birthYear });
}

// Sending a name to a row whose name is EMPTY is what names an imported face cluster — the
// highest-leverage action in the feature, because its suggestions are already pointed at that row.
function updatePhotoPerson(id, body) {
  return photosPost(`/API/Photos/Person/${id}/Update`, body);
}

// Maps an unnamed cluster onto someone who already exists instead of creating a second row.
function mergePhotoPerson(id, intoPersonId) {
  return photosPost(`/API/Photos/Person/${id}/MergeInto`, { intoPersonId });
}

function deletePhotoPerson(id) {
  return photosPost(`/API/Photos/Person/${id}/Delete`, { confirm: true });
}

function getPhotoPerson(id) {
  return fetch(`/API/Photos/Person/${id}`, { cache: "no-store" });
}

function getPhotoPersonTimeline(id, { skip, take, includeHidden } = {}) {
  const params = new URLSearchParams();
  if (skip) params.set("skip", skip);
  if (take) params.set("take", take);
  if (includeHidden) params.set("includeHidden", "true");
  return fetch(`/API/Photos/Person/${id}/Timeline?` + params.toString(), { cache: "no-store" });
}

function getPhotoAssetTags(id) {
  return fetch(`/API/Photos/Asset/${id}/Tags`, { cache: "no-store" });
}

// One endpoint for the lightbox picker and the batch action, because they are the same write. A
// name instead of an id creates the person — the type-ahead's "add …" in one round trip.
function addPhotoTags({ assetIds, familyPersonId, name } = {}) {
  return photosPost("/API/Photos/Tags/Add", { assetIds, familyPersonId, name });
}

// An untag DELETES the row rather than leaving a tombstone: "wrong person" must not permanently bar
// the right one from ever being suggested.
function removePhotoTags({ assetIds, familyPersonId } = {}) {
  return photosPost("/API/Photos/Tags/Remove", { assetIds, familyPersonId });
}

function confirmPhotoTag(tagId) {
  return photosPost(`/API/Photos/Tag/${tagId}/Confirm`);
}

function rejectPhotoTag(tagId) {
  return photosPost(`/API/Photos/Tag/${tagId}/Reject`);
}

// Wall-clock as typed (§2.7). A circa range never writes an exact date: a year is not a wall clock.
function setPhotoAssetDate(id, body) {
  return photosPost(`/API/Photos/Asset/${id}/Date`, body);
}

// mode: "suggested" (rows a sync proposed) or "untagged" (the manual lane, which works with no
// sidecar deployed at all). Keyset-paged by id so accepting under the reviewer shifts nothing.
function getPhotoTagQueue({ mode, afterId, take } = {}) {
  const params = new URLSearchParams();
  if (mode) params.set("mode", mode);
  if (afterId) params.set("afterId", afterId);
  if (take) params.set("take", take);
  return fetch("/API/Photos/TagQueue?" + params.toString(), { cache: "no-store" });
}

// ── Family video playback (docs/photos-plan.md §2.3) ──────────────────────────
// The server names the Jellyfin item from the ASSET id — the browser never sends one, so this stays
// a family-gated endpoint rather than a media-server proxy. A 409 means "not synced yet": the file is
// safe on disk and everything else about it works, it just cannot play.

function startPhotoVideo(assetId, options = {}) {
  return photosPost("/API/Photos/Video/Start", { assetId, ...options });
}

/** Admin: which family videos sit in folders Jellyfin RESERVES for extras and therefore drops. */
function getPhotosJellyfinAudit() {
  return fetch("/API/Photos/JellyfinAudit", { cache: "no-store" });
}

// ── Google mesh (docs/photos-plan.md §2.10) ───────────────────────────────────
// Member-visible curation, like the hide proposals and the dupe review. The mesh itself runs as a
// CLI pass over a Takeout archive; these only read what it decided and record one human answer.

/** Match stats, per-rung counts, and what Google disagrees with the library about. */
function getPhotosGoogleMesh() {
  return fetch("/API/Photos/GoogleMesh", { cache: "no-store" });
}

/** The Google-only list: what the archive has and the library does not, with archive thumbnails. */
function getPhotosGoogleOnly({ skip, take, includeIgnored } = {}) {
  const params = new URLSearchParams();
  if (skip) params.set("skip", skip);
  if (take) params.set("take", take);
  if (includeIgnored) params.set("includeIgnored", "true");
  return fetch("/API/Photos/GoogleOnly?" + params.toString(), { cache: "no-store" });
}

/** Ignore Google-only items (excluding them from the download lane), or take that back. */
function ignorePhotosGoogleItems(ids, ignored) {
  return photosPost("/API/Photos/GoogleItems/Ignore", { ids, ignored });
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

// ── Sync-scan candidates (untracked files a sync classified: upgrades / new titles) ──
function syncCandidatesList() {
  return fetch("/API/Admin/IngestReview/SyncCandidates");
}
// Approve one upgrade: re-points the target movie's file in place (nothing deleted).
function syncCandidateApplyUpgrade(id) {
  return fetch("/API/Admin/IngestReview/SyncCandidates/ApplyUpgrade", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Id: id }),
  });
}
function syncCandidatesReject(ids) {
  return fetch("/API/Admin/IngestReview/SyncCandidates/Reject", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Ids: ids || [] }),
  });
}
// Correct a pending candidate: retitle, pin an IMDb id, or reclassify
// (kind: "new" | "upgrade" | "unclassified" | "series"). applyToGroup fans the edit out over every
// pending candidate sharing this one's series folder — a correction to a SHOW, not to one file.
function syncCandidateUpdate({ id, kind, title, year, imdbId, targetMovieId, targetSeriesId, applyToGroup }) {
  return fetch("/API/Admin/IngestReview/SyncCandidates/Update", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      Id: id,
      Kind: kind ?? null,
      Title: title ?? null,
      Year: year ?? null,
      ImdbId: imdbId ?? null,
      TargetMovieId: targetMovieId ?? null,
      TargetSeriesId: targetSeriesId ?? null,
      ApplyToGroup: !!applyToGroup,
    }),
  });
}
// One chunk of new-title resolution (a few folders per call — the caller loops on { done, remaining }).
// Each resolved folder becomes a quarantined ReviewBatch movie with its file attached, shown in the
// normal open-batch review flow.
function syncCandidatesResolve(limit = 3) {
  return fetch(`/API/Admin/IngestReview/SyncCandidates/Resolve?limit=${limit}`, { method: "post" });
}
// One chunk of SERIES resolution. A "unit" is identify-the-show / enumerate-one-season /
// map-the-files, and a call does at most `limit` of them — the caller loops on { done, remaining }.
// Every unit's result is durable, so an interrupted loop resumes where it stopped.
function syncCandidatesResolveSeries(limit = 4) {
  return fetch(`/API/Admin/IngestReview/SyncCandidates/ResolveSeries?limit=${limit}`, { method: "post" });
}
// The reviewer's answer to a season-boundary disagreement: map nth file to nth catalogued episode.
// Never automatic — it overrides what the file names say.
function syncCandidatesMapSeriesAbsolute(id) {
  return fetch("/API/Admin/IngestReview/SyncCandidates/MapSeriesAbsolute", {
    method: "post",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Id: id }),
  });
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
// 3) START the sync as a server-side background job ({ started } | { alreadyRunning }) — the run's
//    outcome lives on the server, so a dropped browser connection can't lose it.
// Starts the WHOLE operation (scan → wait → sync) as one server-side job and returns immediately.
// scan=false skips the library scan, for when one has just finished.
function jellyfinRunSync(scan = true) {
  return fetch(`/API/Admin/Jellyfin/RunSync?scan=${scan ? "true" : "false"}`, { method: "post" });
}
// 4) poll the background sync ({ running } → { done, summary } | { done, error }).
function jellyfinSyncRunStatus() {
  return fetch("/API/Admin/Jellyfin/SyncStatus");
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
  getChannelMeta,
  setFavoriteChannels,
  createPlaylist,
  getMyPlaylists,
  getPlaylistItems,
  addPlaylistItems,
  setPlaylistItems,
  renamePlaylist,
  deletePlaylist,
  getWatchparty,
  watchpartyHeartbeat,
  watchpartyReady,
  watchpartyBegin,
  leaveWatchparty,
  beaconLeaveWatchparty,
  getArcadeGames,
  getArcadeGameLetters,
  getArcadeFilters,
  getArcadeRenderers,
  getArcadeRooms,
  createArcadeRoom,
  getRetroAchievementsStatus,
  linkRetroAchievements,
  unlinkRetroAchievements,
  getRetroAchievementsProfile,
  getArcadeLeaderboards,
  getArcadeUserAchievements,
  getArcadeGameAchievements,
  getArcadeUserTrophies,
  getMyArcadeTrophies,
  listArcadeSaves,
  getArcadeRecentlyPlayed,
  getAllArcadeSaves,
  getArcadeCheats,
  getArcadeGameConfig,
  saveArcadeGameConfig,
  getArcadeHostStatus,
  getArcadeHeavyStatus,
  stageArcadeHeavy,
  pairArcadeHeavy,
  deleteArcadeSave,
  renameArcadeSave,
  arcadeSaveDownloadUrl,
  importArcadeSave,
  bindArcadeRoom,
  joinArcadeRoom,
  claimArcadeSeat,
  releaseArcadeSeat,
  arcadeHeartbeat,
  leaveArcadeRoom,
  beaconLeaveArcadeRoom,
  adminGetUsers,
  adminGetPatchedArtifacts,
  adminSetUserPassword,
  adminSetUserSetting,
  getPhotosStatus,
  getPhotosTimeline,
  getPhotosTimelineYears,
  getPhotosFolder,
  getPhotoAsset,
  getPhotosIngestStatus,
  setPhotosHidden,
  setPhotosShelf,
  getPhotosHideProposals,
  getPhotosHideProposal,
  decidePhotosHideProposal,
  getPhotosIngestBatches,
  approvePhotosIngestBatches,
  getPhotoAlbums,
  getPhotoGallery,
  getPhotoAlbum,
  createPhotoAlbum,
  updatePhotoAlbum,
  addToPhotoAlbum,
  removeFromPhotoAlbum,
  reorderPhotoAlbum,
  deletePhotoAlbum,
  getPhotoDupeGroups,
  resolvePhotoDupeGroup,
  rejectPhotoDupeGroup,
  getPhotoAssetAlbums,
  getPhotoPeople,
  createPhotoPerson,
  updatePhotoPerson,
  mergePhotoPerson,
  deletePhotoPerson,
  getPhotoPerson,
  getPhotoPersonTimeline,
  getPhotoAssetTags,
  addPhotoTags,
  removePhotoTags,
  confirmPhotoTag,
  rejectPhotoTag,
  setPhotoAssetDate,
  getPhotoTagQueue,
  startPhotoVideo,
  getPhotosJellyfinAudit,
  getPhotosGoogleMesh,
  getPhotosGoogleOnly,
  ignorePhotosGoogleItems,
  ingestReviewList,
  ingestReviewDetail,
  ingestReviewApprove,
  ingestReviewReject,
  ingestReviewUpdate,
  ingestReviewReclassify,
  ingestReviewBackfillPosters,
  ingestReviewMigrateSeriesPosters,
  ingestReviewBackfillThumbnails,
  syncCandidatesList,
  syncCandidateApplyUpgrade,
  syncCandidatesReject,
  syncCandidateUpdate,
  syncCandidatesResolve,
  syncCandidatesResolveSeries,
  syncCandidatesMapSeriesAbsolute,
  jellyfinTriggerScan,
  jellyfinScanStatus,
  jellyfinRunSync,
  jellyfinSyncRunStatus,
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
  getMusicCapabilities,
  getMusicArtists,
  getMusicArtist,
  getMusicAlbums,
  getMusicAlbum,
  searchMusicTracks,
  startMusicTrack,
  startMusicTracks,
  getMusicProbeCandidates,
  getMusicTrackLyrics,
  getMusicAlbumArt,
  getMusicAlbumArtThumb,
  createMusicPlaylist,
  getMyMusicPlaylists,
  getMusicPlaylistItems,
  addMusicPlaylistItems,
  setMusicPlaylistItems,
  renameMusicPlaylist,
  deleteMusicPlaylist,
  getMusicPlaylistShares,
  shareMusicPlaylist,
  unshareMusicPlaylist,
  getMusicShareTargets,
  getMusicFavorites,
  setMusicFavorite,
};

export { MovieAPI };
