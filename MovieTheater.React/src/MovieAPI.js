const MOVIE_API = "https://theater.carpouzis.com";

function getMoviePoster(id) {
  const rootUrl = MOVIE_API;
  return rootUrl + "/Image/" + id;
}

function getPosterThumbnail(id) {
  const rootUrl = MOVIE_API;
  return rootUrl + "/ImageThumb/" + id;
}

const MovieAPI = {
  getMoviePoster,
  getPosterThumbnail,
};

export { MovieAPI };
