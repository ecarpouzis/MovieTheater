import { MovieAPI } from "../../MovieAPI";
import { Card, List } from "antd";
import { useState } from "react";

const listStyle = {
  width: "100%",
  height: "100%",
  padding: "5px",
};

const cardPosterStyle = {
  height: "100%",
  width: "100%",
  objectFit: "contain",
};

const cardTitleStyle = {
  fontWeight: "bold",
  fontFamily: "Arial Black",
  color: "#5E5E5E",
  width: "100%",
  textAlign: "center",
  fontSize: "13px",
  marginTop: "0px",
  marginBottom: "0px",
  display: "-webkit-box",
  WebkitLineClamp: "2",
  WebkitBoxOrient: "vertical",
  lineHeight: "1.3",
  overflow: "hidden",
  textOverflow: "ellipsis",
  wordBreak: "break-word",
  maxHeight: "34px",
  flex: "0 0 34px",
};

const cardMetaStyle = {
  textAlign: "center",
  fontSize: "11px",
  color: "#888",
  marginTop: "2px",
  marginBottom: "0px",
  height: "16px",
  overflow: "hidden",
  textOverflow: "ellipsis",
  whiteSpace: "nowrap",
  flex: "0 0 16px",
};

const baseCardBodyStyle = {
  padding: "4px",
  display: "flex",
  flexDirection: "column",
  userSelect: "none",
  height: "100%",
  overflow: "hidden",
  gap: "0",
};

const posterContainer = {
  width: "100%",
  height: "200px",
  overflow: "hidden",
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  backgroundColor: "#f0f0f0",
  flex: "0 0 auto",
};

const buttonContainerStyle = {
  display: "flex",
  justifyContent: "center",
  gap: "6px",
  marginTop: "auto",
  padding: "4px",
  backgroundColor: "#f5f5f5",
  borderRadius: "6px",
  flex: "0 0 auto",
};

const filmIcon = {
  fontSize: "12px",
  width: "14px",
  verticalAlign: "middle",
  marginRight: "4px",
};

const heartIcon = {
  fontSize: "12px",
  width: "14px",
  verticalAlign: "middle",
  marginRight: "4px",
};

const buttonStyle = (isActive, isHovered) => ({
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  padding: "5px 8px",
  fontSize: "12px",
  fontWeight: "bold",
  cursor: "pointer",
  borderRadius: "4px",
  border: "2px solid transparent",
  transition: "all 0.2s ease",
  backgroundColor: isActive ? (isActive === "seen" ? "#e6f4ff" : "#ffe6e6") : "white",
  color: isActive ? (isActive === "seen" ? "#22c55e" : "#dc143c") : isHovered ? "#1890ff" : "#a9a9a9",
  borderColor: isActive ? (isActive === "seen" ? "#22c55e" : "#dc143c") : "#ddd",
});

function UserMovieOptions({ userData, id, setUserData, onToggleViewing }) {
  const [hoveredSeenButton, setHoveredSeenButton] = useState(false);
  const [hoveredWantButton, setHoveredWantButton] = useState(false);

  if (!userData) return null;

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
    if (typeof onToggleViewing === "function") onToggleViewing(id, "SetWatched", newIsWatched);
    MovieAPI.setWatchedState(userData.username, id, newIsWatched)
      .then((r) => r.json())
      .then((r) => { if (!r.success) alert(r.message); });
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
    if (typeof onToggleViewing === "function") onToggleViewing(id, "SetWantToWatch", newIsWanted);
    MovieAPI.setWantToWatchState(userData.username, id, newIsWanted)
      .then((r) => r.json())
      .then((r) => { if (!r.success) alert(r.message); });
  };

  return (
    <div style={buttonContainerStyle} className="mobile-button-container">
      <button
        onClick={handleSeenClick}
        onMouseEnter={() => setHoveredSeenButton(true)}
        onMouseLeave={() => setHoveredSeenButton(false)}
        onTouchStart={() => setHoveredSeenButton(true)}
        onTouchEnd={() => setHoveredSeenButton(false)}
        style={buttonStyle(isWatched ? "seen" : null, hoveredSeenButton)}
      >
              <span style={filmIcon}>✓</span>
        <span>SEEN</span>
      </button>
      <button
        onClick={handleWantClick}
        onMouseEnter={() => setHoveredWantButton(true)}
        onMouseLeave={() => setHoveredWantButton(false)}
        onTouchStart={() => setHoveredWantButton(true)}
        onTouchEnd={() => setHoveredWantButton(false)}
        style={buttonStyle(isWanted ? "want" : null, hoveredWantButton)}
      >
        <span style={heartIcon}>+</span>
        <span>WANT</span>
      </button>
    </div>
  );
}

function SimpleCardList({ movieDataArray, userData, setUserData, onMovieClick, onToggleViewing }) {
  const [hoveredMovieId, setHoveredMovieId] = useState(null);

  return (
    <List
      style={listStyle}
      grid={{ gutter: 4, column: 2 }}
      dataSource={movieDataArray}
      renderItem={(item) => {
        const thumbUrl = MovieAPI.getPosterThumbnail(item.id);
        return (
          <List.Item>
            <Card
              className="mobile-movie-card"
              bodyStyle={baseCardBodyStyle}
              style={{
                border: "1px solid #d9d9d9",
                width: "100%",
                height: "320px",
                display: "flex",
                flexDirection: "column",
                overflow: "hidden",
              }}
            >
              <div style={posterContainer}>
                <img style={cardPosterStyle} alt={item.title} src={thumbUrl} loading="lazy" />
              </div>
              <div
                onClick={() => onMovieClick(item.id)}
                onMouseEnter={() => setHoveredMovieId(item.id)}
                onMouseLeave={() => setHoveredMovieId(null)}
                style={{ cursor: "pointer", display: "flex", flexDirection: "column", flex: "0 0 auto" }}
              >
                <div style={{ ...cardTitleStyle, color: hoveredMovieId === item.id ? "#1890ff" : "#5E5E5E" }}>
                  {item.title}
                </div>
                <div style={cardMetaStyle}>
                  {new Date(item.releaseDate).getFullYear()} • {item.rating} • {item.runtime}
                </div>
              </div>
              <UserMovieOptions
                userData={userData}
                id={item.id}
                setUserData={setUserData}
                onToggleViewing={onToggleViewing}
              />
            </Card>
          </List.Item>
        );
      }}
    />
  );
}

export default SimpleCardList;
