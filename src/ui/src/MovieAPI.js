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

function loginUser(username) {
  const url = "/API/Login?username=" + username;
  return fetch(url, { method: "post" });
}

function setWatchedState(username, movieID, isActive) {
  const url = "/API/SetViewingState";

  return fetch(url, {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      Username: username,
      MovieID: movieID,
      SetActive: isActive,
      Action: "SetWatched",
    }),
  });
}

function setWantToWatchState(username, movieID, isActive) {
  const url = "/API/SetViewingState";

  return fetch(url, {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      Username: username,
      MovieID: movieID,
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

const MovieAPI = {
  getMoviePoster,
  getPosterThumbnail,
  getMovie,
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
  setWatchedState,
  setWantToWatchState,
  movieLookupFromNames,
  getMPARatings,
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
};

export { MovieAPI };
