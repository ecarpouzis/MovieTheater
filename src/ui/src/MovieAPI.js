function getMoviePoster(id) {
  return "/Image/" + id;
}

function getPosterThumbnail(id) {
  return "/ImageThumb/" + id;
}

function getMovies(search) {
  const url = "/API/API_Movies";

  return fetch(url, {
    method: "post",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(search),
  });
}

function getMovie(id) {
  const url = "/API/GetMovie?id=" + id;

  return fetch(url, {
    method: "get",
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

const MovieAPI = {
  getMoviePoster,
  getPosterThumbnail,
  getMovies,
  getMovie,
  getUsers,
  loginUser,
  setWatchedState,
  setWantToWatchState,
};

export { MovieAPI };
