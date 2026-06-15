import { MovieAPI } from "../MovieAPI";
import { useState, useEffect } from "react";
import { useParams } from "react-router-dom";
import WatchButton from "./Watch/WatchButton";

function MoviePage({ userData }) {
  const { id } = useParams();
  const [movie, setMovie] = useState(null);

  useEffect(() => {
    MovieAPI.getMovie(id)
      .then((response) => response.json())
      .then((responseData) => {
        setMovie(responseData.data);
      });
  }, [id]);

  if (!movie) {
    return <div>Loading</div>;
  }
  return (
    <div>
    <div className="movie-page-wrapper">
      <img src={MovieAPI.getMoviePoster(movie.id, movie.posterVersion)} alt={movie.title + " poster"} />
      <div className="movie-detail-container">
          <div className="movie-detail">
            <WatchButton movie={movie} userData={userData} />
          </div>
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
                  <span key={index} className="actor-box">
                    {actor}
                  </span>
                ))
              : null}
          </div>
          <div className="movie-detail">
            <u>IMDB Rating:</u>{" "}
            <a target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID}>
              {movie.imdbRating} / 10
            </a>
          </div>
          {movie.rtTomatometer != null && (
            <div className="movie-detail">
              <u>Tomatometer:</u>{" "}
              <a
                target="_blank"
                rel="noreferrer"
                href={movie.rtUrl || "https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)}
              >
                <span aria-hidden="true">🍅</span>{" "}
                <span style={{ color: movie.rtTomatometer >= 60 ? "#1a9e4b" : "#e0431a", fontWeight: 700 }}>
                  {movie.rtTomatometer}%
                </span>
              </a>
            </div>
          )}
          {movie.rtPopcornmeter != null && (
            <div className="movie-detail">
              <u>Popcornmeter:</u>{" "}
              <a
                target="_blank"
                rel="noreferrer"
                href={movie.rtUrl || "https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)}
              >
                <span aria-hidden="true">🍿</span>{" "}
                <span style={{ color: movie.rtPopcornmeter >= 60 ? "#1a9e4b" : "#e0431a", fontWeight: 700 }}>
                  {movie.rtPopcornmeter}%
                </span>
              </a>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export default MoviePage;
