import { useState, useEffect } from "react";
import { Modal, Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";
import UserMovieOptions from "./UserMovieOptions";
import "./MovieModal.css";

function MovieModal({ movieId, open, onClose, actorSearch, movieDataArray, userData, setUserData, onToggleViewing }) {
  const [movie, setMovie] = useState(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (open && movieId) {
      setLoading(true);
      MovieAPI.getMovie(movieId)
        .then((response) => response.json())
        .then((responseData) => {
          setMovie(responseData.data);
          setLoading(false);
        })
        .catch((error) => {
          console.error("Error fetching movie:", error);
          setLoading(false);
        });
    }
  }, [movieId, open]);

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={1100} wrapClassName="movie-modal">
      {loading ? (
        <Spin />
      ) : movie ? (
        <div className="modal-content-outer">
          <h1 className="modal-movie-title">{movie.title}</h1>
          <div className="movie-page-wrapper modal-movie-wrapper">
            <div className="modal-poster-column">
              <img
                className="movie-page-poster modal-poster"
                alt={movie.title + " poster"}
                src={MovieAPI.getMoviePoster(movie.id)}
              />
            </div>
            <div className="movie-detail-container">
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
                  ? movie.actors.split(",").map((actorName, index) => {
                      const actor = actorName.trim();
                      if (!actor) {
                        return null;
                      }
                      return (
                        <button
                          key={index}
                          type="button"
                          className="actor-box actor-box-clickable"
                          onClick={() => {
                            onClose();
                            actorSearch(actor);
                          }}
                        >
                          {actor}
                        </button>
                      );
                    })
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
                <a target="_blank" rel="noreferrer" href={"https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)}>
                  {movie.tomatoRating} / 100
                </a>
              </div>
              <div className="movie-detail2 movie-id-label">
                <span>id #{movie.id}</span>
              </div>
              <UserMovieOptions userData={userData} id={movie.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />
            </div>
          </div>
        </div>
      ) : (
        <div>Error loading movie</div>
      )}
    </Modal>
  );
}

export default MovieModal;
