const MOVIE_API = "http://localhost:65272";

function getMoviePoster(id) {
  const rootUrl = MOVIE_API;
  return rootUrl + "/Image/" + id;
}

function getPosterThumbnail(id) {
  const rootUrl = MOVIE_API;
  return rootUrl + "/ImageThumb/" + id;
}

function getMovies(num) {
  const rootUrl = MOVIE_API;
  return fetch(rootUrl + "/API/Movies?num=" + num);
}

const MovieAPI = {
  getMoviePoster,
  getPosterThumbnail,
  getMovies,
};

export { MovieAPI };
