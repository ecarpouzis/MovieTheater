import { MovieAPI } from "../../MovieAPI";
import { useState, useEffect } from "react";
import { Modal, Spin } from "antd";
import { Link } from "react-router-dom";

function MovieModal({ movieId, visible, onClose, actorSearch, movieDataArray }) {
  const [movie, setMovie] = useState(null);
  const [loading, setLoading] = useState(true);
  const [totalMovieCount, setTotalMovieCount] = useState(0);
  const [movieIndex, setMovieIndex] = useState(0);

  useEffect(() => {
    if (visible && movieId) {
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
  }, [movieId, visible]);

  useEffect(() => {
    if (visible) {
      MovieAPI.getTotalMovieCount()
        .then((response) => {
          console.log("Response status:", response.status);
          console.log("Response ok:", response.ok);
          return response.json();
        })
        .then((data) => {
          console.log("Total movie count response:", data);
          if (data.success !== false) {
            setTotalMovieCount(data.totalCount || 0);
          } else {
            console.error("Backend error:", data.error);
            setTotalMovieCount(0);
          }
        })
        .catch((error) => {
          console.error("Error fetching total movie count:", error);
          setTotalMovieCount(0);
        });
    }
  }, [visible]);

  return (
    <Modal visible={visible} onCancel={onClose} footer={null} width={1100} bodyStyle={{ maxHeight: "80vh", overflowY: "auto", padding: "24px" }}>
      {loading ? (
        <Spin />
      ) : movie ? (
        <div style={{ display: "flex", flexDirection: "column", width: "100%" }}>
          <h1 style={{ textAlign: "center", fontSize: "32px", fontWeight: "bold", margin: "0 0 20px 0" }}>{movie.title}</h1>
          <div className="movie-page-wrapper" style={{ width: "100%", maxWidth: "none", margin: "0" }}>
            <img
              className="movie-page-poster"
              alt={movie.title + " poster"}
              src={MovieAPI.getMoviePoster(movie.id)}
              style={{ width: "200px", height: "auto", marginRight: "20px", marginTop: "0" }}
            />
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
                  ? movie.actors.split(",").map((actor, index) => (
                      <Link
                        key={index}
                        className="actor-box"
                        onClick={() => {
                          actorSearch(actor);
                          onClose();
                        }}
                      >
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
              <div
                className="movie-detail2"
                style={{ marginTop: "10px", textAlign: "left", fontSize: "13px", backgroundColor: "white", color: "gray" }}
              >
                id #{movie.id}
              </div>
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
