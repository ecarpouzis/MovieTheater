import { useState, useEffect } from "react";
import { Modal, Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";
import UserMovieOptions from "./UserMovieOptions";
import "./MovieModal.css";

function MovieModal({ movieId, open, onClose, actorSearch, userData, setUserData, onToggleViewing }) {
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
    <Modal open={open} onCancel={onClose} footer={null} width={960} wrapClassName="movie-modal">
      {loading ? (
        <Spin />
      ) : movie ? (
        <div className="modal-body-wrapper">
          <div className="modal-poster-column">
            <img
              className="modal-poster"
              alt={movie.title + " poster"}
              src={MovieAPI.getMoviePoster(movie.id)}
            />
          </div>
          <div className="modal-info-panel">
            <h2 className="modal-movie-title">{movie.title}</h2>

            <div className="modal-meta-row">
              <span>{new Date(movie.releaseDate).getFullYear()}</span>
              {movie.rating && <><span className="modal-dot">·</span><span>{movie.rating}</span></>}
              {movie.runtime && <><span className="modal-dot">·</span><span>{movie.runtime}</span></>}
            </div>

            {movie.genre && <div className="modal-genre">{movie.genre}</div>}

            <div className="modal-crew-grid">
              {movie.director && (
                <div className="modal-crew-item">
                  <span className="modal-label">Director</span>
                  <span>{movie.director}</span>
                </div>
              )}
              {movie.writer && (
                <div className="modal-crew-item">
                  <span className="modal-label">Writer</span>
                  <span>{movie.writer}</span>
                </div>
              )}
            </div>

            {movie.plot && <p className="modal-plot">{movie.plot}</p>}

            {movie.actors && (
              <div className="modal-actors">
                {movie.actors.split(",").map((actorName, index) => {
                  const actor = actorName.trim();
                  if (!actor) return null;
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
                })}
              </div>
            )}

            <div className="modal-ratings-row">
              <a className="modal-rating-link" target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID}>
                <span className="modal-label">IMDb</span>
                <span className="modal-rating-score">{movie.imdbRating}<span className="modal-rating-denom"> / 10</span></span>
              </a>
              <a className="modal-rating-link" target="_blank" rel="noreferrer" href={"https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)}>
                <span className="modal-label">Rotten Tomatoes</span>
                <span className="modal-rating-score">{movie.tomatoRating}<span className="modal-rating-denom"> / 100</span></span>
              </a>
            </div>

            <UserMovieOptions userData={userData} id={movie.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />

            <div className="modal-movie-id">id #{movie.id}</div>
          </div>
        </div>
      ) : (
        <div>Error loading movie</div>
      )}
    </Modal>
  );
}

export default MovieModal;
