import { MovieAPI } from "../../MovieAPI";
import { useState, useEffect } from "react";
import { Modal, Spin } from "antd";

const filmIcon = {
  fontSize: "30px",
  width: "35px",
  verticalAlign: "middle",
  paddingRight: "30px",
};

const heartIcon = {
  fontSize: "25px",
  width: "30px",
  verticalAlign: "middle",
  paddingRight: "5px",
};

const buttonLabelStyle = {
  fontWeight: "bold",
  verticalAlign: "middle",
};

function UserMovieOptions({ userData, id, setUserData, inline = false, onToggleViewing }) {
  const [hoveredSeenButton, setHoveredSeenButton] = useState(false);
  const [hoveredWantButton, setHoveredWantButton] = useState(false);

  if (userData) {
    const isWatched = userData.moviesSeen.includes(id);

    const isWanted = userData.moviesToWatch.includes(id);
    return (
      <>
        <div
          style={{
            display: "flex",
            justifyContent: "center",
            alignItems: "center",
            gap: inline ? "10px" : "18px",
            marginTop: inline ? "0" : "0px",
            width: inline ? "auto" : "100%",
          }}
        >
          <div
            onClick={() => {
              const newIsWatched = !isWatched;
              if (!isWatched) {
                let newUserData = {
                  ...userData,
                  moviesSeen: [...userData.moviesSeen, id],
                };
                setUserData(newUserData);
              } else {
                let newUserData = {
                  ...userData,
                  moviesSeen: userData.moviesSeen.filter((x) => x !== id),
                };
                setUserData(newUserData);
              }

              if (typeof onToggleViewing === "function") onToggleViewing(id, "SetWatched", newIsWatched);

              MovieAPI.setWatchedState(userData.username, id, newIsWatched)
                .then((response) => response.json())
                .then((response) => {
                  if (!response.success) {
                    alert(response.message);
                  }
                });
            }}
            onMouseEnter={() => setHoveredSeenButton(true)}
            onMouseLeave={() => setHoveredSeenButton(false)}
            className="zoom-on-hover"
            style={{
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              width: inline ? "100px" : "160px",
              padding: inline ? "0" : "8px 12px",
              cursor: "pointer",
              color: isWatched ? "#4169e3" : hoveredSeenButton ? "#52c41a" : "#a9a9a9",
            }}
          >
            <span style={filmIcon} className="fas fa-film"></span>
            <span style={{ ...buttonLabelStyle, fontSize: inline ? "inherit" : "16px" }}>SEEN</span>
          </div>
          <div
            onClick={() => {
              const newIsWanted = !isWanted;
              if (!isWanted) {
                let newUserData = {
                  ...userData,
                  moviesToWatch: [...userData.moviesToWatch, id],
                };
                setUserData(newUserData);
              } else {
                let newUserData = {
                  ...userData,
                  moviesToWatch: userData.moviesToWatch.filter((x) => x !== id),
                };
                setUserData(newUserData);
              }

              if (typeof onToggleViewing === "function") onToggleViewing(id, "SetWantToWatch", newIsWanted);

              MovieAPI.setWantToWatchState(userData.username, id, newIsWanted)
                .then((response) => response.json())
                .then((response) => {
                  if (!response.success) {
                    alert(response.message);
                  }
                });
            }}
            onMouseEnter={() => setHoveredWantButton(true)}
            onMouseLeave={() => setHoveredWantButton(false)}
            className="zoom-on-hover"
            style={{
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              width: inline ? "100px" : "160px",
              padding: inline ? "0" : "8px 12px",
              cursor: "pointer",
              color: isWanted ? "#dc143c" : hoveredWantButton ? "#52c41a" : "#a9a9a9",
            }}
          >
            <span style={heartIcon} className="fas fa-heart"></span>
            <span style={{ ...buttonLabelStyle, fontSize: inline ? "inherit" : "16px" }}>WANT</span>
          </div>
        </div>
      </>
    );
  }
  return <></>;
}

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
    <Modal open={open} onCancel={onClose} footer={null} width={1100} bodyStyle={{ maxHeight: "80vh", overflowY: "auto", padding: "24px" }}>
      {loading ? (
        <Spin />
      ) : movie ? (
        <div style={{ display: "flex", flexDirection: "column", width: "100%" }}>
          <h1 style={{ textAlign: "center", fontSize: "32px", fontWeight: "bold", margin: "0 0 20px 0" }}>{movie.title}</h1>
          <div className="movie-page-wrapper" style={{ width: "100%", maxWidth: "none", margin: "0" }}>
            <div style={{ display: "flex", flexDirection: "column", alignItems: "center", marginRight: "20px", width: "200px" }}>
              <img
                className="movie-page-poster"
                alt={movie.title + " poster"}
                src={MovieAPI.getMoviePoster(movie.id)}
                style={{ width: "200px", height: "auto", marginTop: "0" }}
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
              <div
                className="movie-detail2"
                style={{
                  marginTop: "4px",
                  fontSize: "13px",
                  backgroundColor: "white",
                  color: "gray",
                  marginBottom: "2px",
                  textAlign: "right",
                }}
              >
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
