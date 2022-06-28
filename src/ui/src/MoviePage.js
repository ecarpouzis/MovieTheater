import { MovieAPI } from "./MovieAPI";
import { useState, useEffect } from "react";
import { Link, useParams } from "react-router-dom";

function MoviePage() {
  const { id } = useParams();
  const [movie, setMovie] = useState(null);

  useEffect(() => {
    MovieAPI.getMovie(id)
      .then((response) => response.json())
      .then((responseData) => {
        setMovie(responseData.data);
        console.log(movie);
      });
  });

  if (!movie) {
    return <div>Loading</div>;
  }
  return (
    <div>
      <img
        alt={movie.title + " poster"}
        src={MovieAPI.getMoviePoster(movie.id)}
      />
      <br />
      <span>{movie.title}</span>
      <br />
      <span>{new Date(movie.releaseDate).getFullYear()}</span>
      <br />
      <span>{movie.rating}</span>
      <br />
      <span>{movie.runtime}</span>
      <br />
      <span>{movie.genre}</span>
      <br />
      <span>{movie.director}</span>
      <br />
      <span>{movie.writer}</span>
      <br />
      <span>{movie.plot}</span>
      <br />
      {movie.actors
        ? movie.actors.split(",").map((actor) => <Link>{actor}</Link>)
        : null}
      <br />
      <a target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID}>
        IMDB {movie.imdbRating}
      </a>
      <br />
      <a target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID}>
        RottenTomatoes {movie.tomatoRating}
      </a>
    </div>
  );
}

export default MoviePage;
