const MOVIE_API = "http://localhost:65272";

function getMoviePoster(id) {
  return MOVIE_API + "/Image/" + id;
}

function getPosterThumbnail(id) {
  return MOVIE_API + "/ImageThumb/" + id;
}

function getMovies(search) {
  const url = MOVIE_API + "/API/API_Movies";

  return fetch(url, {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(search),
  });
}

function getUsers() {
  const url = MOVIE_API + "/API/API_UserList";
  return fetch(url);
}

function loginUser(username) {
  const url = MOVIE_API + "/API/Login?username=" + username;
  return fetch(url, { method: "post" });
}

function setWatchedState(username, movieID, isActive) {
  const url = MOVIE_API + "/API/SetViewingState";

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
  const url = MOVIE_API + "/API/SetViewingState";

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

const MovieAPI = {
  getMoviePoster,
  getPosterThumbnail,
  getMovies,
  getUsers,
  loginUser,
  setWatchedState,
  setWantToWatchState,
};

export { MovieAPI };
