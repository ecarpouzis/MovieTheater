import { MovieAPI } from "../MovieAPI";
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
  }, [id]);

  if (!movie) {
    return <div>Loading</div>;
  }
  return (
    <div>
      <div className="movie-page-wrapper">
        <img className="movie-page-poster" alt={movie.title + " poster"} src={MovieAPI.getMoviePoster(movie.id)} />
        <div className="movie-detail-container">
          <div className="movie-detail">
            <u>Title:</u> {movie.title}
          </div>
          <div className="movie-detail">
            <u>Release Date:</u> {new Date(movie.releaseDate).getFullYear()}
          </div>
          <div className="movie-detail">
            <u>Rating:</u> {movie.rating}
          </div>
          <div className="movie-detail">
            <u>Runtime:</u> {movie.runtime}
          </div>
          <div className="movie-detail">
            <u>Genre:</u> {movie.genre}
          </div>
          <div className="movie-detail">
            <u>Director:</u> {movie.director}
          </div>
          <div className="movie-detail">
            <u>Writer:</u> {movie.writer}
          </div>
          <div className="movie-detail">
            <u>Plot:</u> {movie.plot}
          </div>
          <div className="movie-detail actors-container">
            <u>Actors:</u>
            {movie.actors
              ? movie.actors.split(",").map((actor, index) => (
                  <Link key={index} className="actor-box">
                    {actor}
                  </Link>
                ))
              : null}
          </div>
          <div className="movie-detail">
            <u>IMDB Rating:</u>{" "}
            <a target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID}>
              {movie.imdbRating} / 10
            </a>
          </div>
          <div className="movie-detail">
            <u>RottenTomatoes Rating:</u>{" "}
            <a target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID}>
              {movie.tomatoRating} / 100
            </a>
          </div>
        </div>
      </div>
    </div>
  );
}

export default MoviePage;
