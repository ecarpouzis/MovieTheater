const MOVIE_API = "http://localhost:65272";

function getMoviePoster(id) {
  const rootUrl = MOVIE_API;
  return rootUrl + "/Image/" + id;
}

function getPosterThumbnail(id) {
  const rootUrl = MOVIE_API;
  return rootUrl + "/ImageThumb/" + id;
}

function getMovies(num, startsWith) {
  const rootUrl = MOVIE_API;
  const url =
    rootUrl +
    "/API/API_Movies?num=" +
    (num || "") +
    "&startsWith=" +
    (startsWith ? encodeURIComponent(startsWith) : "");

  return fetch(url);
}

function getUsers() {
  const rootUrl = MOVIE_API;
  const url = rootUrl + "/API/API_UserList";
  return fetch(url);
}

const MovieAPI = {
  getMoviePoster,
  getPosterThumbnail,
  getMovies,
  getUsers,
};

export { MovieAPI };
