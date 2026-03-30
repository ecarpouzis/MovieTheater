import { MovieAPI } from "../../MovieAPI";
import { useState, useEffect } from "react";
import { Spin } from "antd";

const overlayStyle = {
  position: "fixed",
  top: 0,
  left: 0,
  right: 0,
  bottom: 0,
  backgroundColor: "rgba(0, 0, 0, 0.6)",
  zIndex: 1000,
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  padding: "15px",
  overflowY: "auto",
  pointerEvents: "auto",
};

const modalContainerStyle = {
  position: "relative",
  width: "100%",
  maxWidth: "700px",
  backgroundColor: "white",
  borderRadius: "8px",
  boxShadow: "0 4px 20px rgba(0, 0, 0, 0.3)",
  maxHeight: "90vh",
  overflowY: "auto",
  margin: "auto",
};

const closeButtonStyle = {
  position: "fixed",
  top: "48px",
  right: "50px",
  zIndex: 1001,
  background: "white",
  border: "2px solid #ddd",
  borderRadius: "50%",
  fontSize: "16px",
  cursor: "pointer",
  width: "28px",
  height: "28px",
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  color: "#dc143c",
  transition: "all 0.2s ease",
  boxShadow: "0 2px 8px rgba(0,0,0,0.2)",
  lineHeight: "1",
  padding: "0",
};

const contentStyle = {
  padding: "8px",
  paddingTop: "50px",
};

const titleStyle = {
  fontSize: "24px",
  fontWeight: "bold",
  marginBottom: "6px",
  textAlign: "center",
  color: "#333",
  paddingRight: "40px",
  paddingLeft: "40px",
  wordBreak: "break-word",
};

const moviePageWrapperStyle = {
  display: "flex",
  flexDirection: "column",
  alignItems: "center",
  gap: "6px",
  width: "100%",
};

const posterContainerStyle = {
  display: "flex",
  justifyContent: "center",
  flexShrink: 0,
  marginBottom: "6px",
};

const posterStyle = {
  width: "180px",
  height: "auto",
  borderRadius: "6px",
  boxShadow: "0 2px 8px rgba(0,0,0,0.15)",
  objectFit: "contain",
};

const detailContainerStyle = {
  flex: 1,
  display: "flex",
  flexDirection: "column",
  gap: "3px",
  width: "100%",
  textAlign: "center",
};

const detailStyle = {
  padding: "4px",
  marginBottom: "2px",
  backgroundColor: "#f9f9f9",
  border: "1px solid #ddd",
  borderRadius: "4px",
  fontSize: "13px",
  lineHeight: "1.3",
  textAlign: "center",
};

const filmIcon = {
  fontSize: "16px",
  width: "18px",
  verticalAlign: "middle",
  marginRight: "6px",
};

const heartIcon = {
  fontSize: "16px",
  width: "18px",
  verticalAlign: "middle",
  marginRight: "6px",
};

const buttonContainerStyle = {
  display: "flex",
  justifyContent: "center",
  gap: "15px",
  margin: "6px 0 4px 0",
  padding: "6px",
  backgroundColor: "#f5f5f5",
  borderRadius: "6px",
};

const buttonStyle = (isActive, isHovered, activeType) => ({
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  padding: "8px 16px",
  fontSize: "14px",
  fontWeight: "bold",
  cursor: "pointer",
  borderRadius: "4px",
  border: "2px solid transparent",
  transition: "all 0.3s ease",
  backgroundColor: isActive ? (activeType === "seen" ? "#e6f4ff" : "#ffe6e6") : "white",
  color: isActive ? (activeType === "seen" ? "#22c55e" : "#dc143c") : isHovered ? "#1890ff" : "#a9a9a9",
  borderColor: isActive ? (activeType === "seen" ? "#22c55e" : "#dc143c") : "#ddd",
});

function UserMovieOptions({ userData, id, setUserData, onToggleViewing }) {
  const [hoveredSeenButton, setHoveredSeenButton] = useState(false);
  const [hoveredWantButton, setHoveredWantButton] = useState(false);

  if (!userData) {
    return null;
  }

  const isWatched = userData.moviesSeen.includes(id);
  const isWanted = userData.moviesToWatch.includes(id);

  const handleSeenClick = (e) => {
    e.stopPropagation();
    const newIsWatched = !isWatched;
    const newUserData = {
      ...userData,
      moviesSeen: newIsWatched
        ? [...userData.moviesSeen, id]
        : userData.moviesSeen.filter((x) => x !== id),
    };
    setUserData(newUserData);

    if (typeof onToggleViewing === "function") {
      onToggleViewing(id, "SetWatched", newIsWatched);
    }

    MovieAPI.setWatchedState(userData.username, id, newIsWatched)
      .then((response) => response.json())
      .then((response) => {
        if (!response.success) {
          alert(response.message);
        }
      });
  };

  const handleWantClick = (e) => {
    e.stopPropagation();
    const newIsWanted = !isWanted;
    const newUserData = {
      ...userData,
      moviesToWatch: newIsWanted
        ? [...userData.moviesToWatch, id]
        : userData.moviesToWatch.filter((x) => x !== id),
    };
    setUserData(newUserData);

    if (typeof onToggleViewing === "function") {
      onToggleViewing(id, "SetWantToWatch", newIsWanted);
    }

    MovieAPI.setWantToWatchState(userData.username, id, newIsWanted)
      .then((response) => response.json())
      .then((response) => {
        if (!response.success) {
          alert(response.message);
        }
      });
  };

  return (
    <div style={buttonContainerStyle} className="mobile-button-container">
      <button
        onClick={handleSeenClick}
        onMouseEnter={() => setHoveredSeenButton(true)}
        onMouseLeave={() => setHoveredSeenButton(false)}
        onTouchStart={(e) => {
          e.stopPropagation();
          setHoveredSeenButton(true);
        }}
        onTouchEnd={(e) => {
          e.stopPropagation();
          setHoveredSeenButton(false);
        }}
        style={buttonStyle(isWatched, hoveredSeenButton, "seen")}
      >
              <span style={filmIcon}>✓</span>
        <span>SEEN</span>
      </button>
      <button
        onClick={handleWantClick}
        onMouseEnter={() => setHoveredWantButton(true)}
        onMouseLeave={() => setHoveredWantButton(false)}
        onTouchStart={(e) => {
          e.stopPropagation();
          setHoveredWantButton(true);
        }}
        onTouchEnd={(e) => {
          e.stopPropagation();
          setHoveredWantButton(false);
        }}
        style={buttonStyle(isWanted, hoveredWantButton, "want")}
      >
        <span style={heartIcon}>+</span>
        <span>WANT</span>
      </button>
    </div>
  );
}

function SimpleMovieModal({ movieId, open, onClose, actorSearch, userData, setUserData, onToggleViewing }) {
  const [movie, setMovie] = useState(null);
  const [loading, setLoading] = useState(true);
  const [touchStartX, setTouchStartX] = useState(0);
  const [touchEndX, setTouchEndX] = useState(0);

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

  useEffect(() => {
    if (open) {
      document.body.style.overflow = "hidden";
    } else {
      document.body.style.overflow = "unset";
    }
    return () => {
      document.body.style.overflow = "unset";
    };
  }, [open]);

  const handleTouchStart = (e) => {
    setTouchStartX(e.touches[0].clientX);
  };

  const handleTouchMove = (e) => {
    setTouchEndX(e.touches[0].clientX);
  };

  const handleTouchEnd = () => {
    if (touchStartX - touchEndX > 100) onClose();
    if (touchEndX - touchStartX > 100) onClose();
  };

  if (!open) return null;

  return (
    <div
      style={overlayStyle}
      onClick={(e) => { e.preventDefault(); e.stopPropagation(); onClose(); }}
      onTouchStart={(e) => { e.preventDefault(); e.stopPropagation(); }}
      onTouchEnd={(e) => { e.preventDefault(); e.stopPropagation(); }}
    >
      <div
        style={modalContainerStyle}
        onClick={(e) => e.stopPropagation()}
        onTouchStart={handleTouchStart}
        onTouchMove={handleTouchMove}
        onTouchEnd={handleTouchEnd}
      >
        <button
          style={closeButtonStyle}
          onClick={(e) => { e.stopPropagation(); onClose(); }}
          onMouseEnter={(e) => { e.target.style.color = "#dc3545"; e.target.style.borderColor = "#dc3545"; }}
          onMouseLeave={(e) => { e.target.style.color = "#666"; e.target.style.borderColor = "#ddd"; }}
        >
          ×
        </button>
        {loading ? (
          <div style={{ display: "flex", justifyContent: "center", padding: "50px" }}>
            <Spin size="large" />
          </div>
        ) : movie ? (
          <div style={contentStyle}>
            <h1 style={titleStyle}>{movie.title}</h1>
            <div style={moviePageWrapperStyle}>
              <div style={posterContainerStyle}>
                <img src={MovieAPI.getMoviePoster(movie.id, movie.posterVersion)} alt={movie.title + " poster"} style={posterStyle} />
              </div>
              <div style={detailContainerStyle}>
                <div style={detailStyle}><strong>Release Date:</strong> {new Date(movie.releaseDate).getFullYear()}</div>
                <div style={detailStyle}><strong>Rating:</strong> {movie.rating}</div>
                <div style={detailStyle}><strong>Runtime:</strong> {movie.runtime}</div>
                <div style={detailStyle}><strong>Genre:</strong> {movie.genre}</div>
                <div style={detailStyle}><strong>Director:</strong> {movie.director}</div>
                <div style={detailStyle}><strong>Writer:</strong> {movie.writer}</div>
                <div style={detailStyle}><strong>Plot:</strong> {movie.plot}</div>
                <div style={{ ...detailStyle, display: "flex", flexWrap: "wrap", gap: "8px" }}>
                  <strong style={{ width: "100%", marginBottom: "8px" }}>Actors:</strong>
                  {movie.actors
                    ? movie.actors.split(",").map((actorName, index) => {
                        const actor = actorName.trim();
                        if (!actor) return null;
                        return (
                          <button
                            key={index}
                            type="button"
                            className="actor-box actor-box-clickable"
                            onClick={(e) => { e.stopPropagation(); actorSearch(actor); onClose(); }}
                            style={{ padding: "6px 12px", backgroundColor: "#f0f0f0", border: "1px solid #ccc", borderRadius: "5px", fontSize: "14px", cursor: "pointer" }}
                          >
                            {actor}
                          </button>
                        );
                      })
                    : null}
                </div>
                <div style={detailStyle}>
                  <strong>IMDB Rating:</strong>{" "}
                  <a target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID} style={{ color: "#007bff", textDecoration: "none" }}>
                    {movie.imdbRating} / 10
                  </a>
                </div>
                <div style={detailStyle}>
                  <strong>RottenTomatoes Rating:</strong>{" "}
                  <a target="_blank" rel="noreferrer" href={"https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)} style={{ color: "#007bff", textDecoration: "none" }}>
                    {movie.tomatoRating} / 100
                  </a>
                </div>
                <div style={{ padding: "8px", fontSize: "12px", color: "gray", textAlign: "right" }}>
                  <span>id #{movie.id}</span>
                </div>
              </div>
            </div>
            <UserMovieOptions userData={userData} id={movie.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />
          </div>
        ) : (
          <div style={{ padding: "20px", textAlign: "center", color: "#ff4d4f" }}>Error loading movie</div>
        )}
      </div>
    </div>
  );
}

export default SimpleMovieModal;
