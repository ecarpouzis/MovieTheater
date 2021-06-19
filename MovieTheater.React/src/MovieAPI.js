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
    "/API/Movies?num=" +
    (num || "") +
    "&startsWith=" +
    (startsWith ? encodeURIComponent(startsWith) : "");

  console.log(url);

  return fetch(url);
}

const MovieAPI = {
  getMoviePoster,
  getPosterThumbnail,
  getMovies,
};

export { MovieAPI };
