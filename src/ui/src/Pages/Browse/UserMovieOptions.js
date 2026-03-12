import { MovieAPI } from "../../MovieAPI";
import { useState, useEffect } from "react";

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

function useIsMobile(breakpoint = 768) {
  const [isMobile, setIsMobile] = useState(() => window.innerWidth <= breakpoint);
  useEffect(() => {
    const handler = () => setIsMobile(window.innerWidth <= breakpoint);
    window.addEventListener("resize", handler);
    return () => window.removeEventListener("resize", handler);
  }, [breakpoint]);
  return isMobile;
}

function UserMovieOptions({ userData, id, setUserData, inline = false, onToggleViewing }) {
  const [hoveredSeenButton, setHoveredSeenButton] = useState(false);
  const [hoveredWantButton, setHoveredWantButton] = useState(false);
  const isMobile = useIsMobile();
  const compact = inline || isMobile;

  if (userData) {
    const isWatched = userData.moviesSeen.includes(id);
    const isWanted = userData.moviesToWatch.includes(id);
    return (
      <>
        {!compact && <br style={{ clear: "both" }} />}
        <div
          style={{
            display: "flex",
            justifyContent: "center",
            alignItems: "center",
            gap: compact ? "10px" : "18px",
            width: compact ? "auto" : "100%",
            paddingTop: isMobile ? "3px" : "0",
          }}
        >
          <div
            onClick={() => {
              const newIsWatched = !isWatched;
              if (!isWatched) {
                let newUserData = { ...userData, moviesSeen: [...userData.moviesSeen, id] };
                setUserData(newUserData);
              } else {
                let newUserData = { ...userData, moviesSeen: userData.moviesSeen.filter((x) => x !== id) };
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
              width: compact ? "100px" : "160px",
              padding: compact ? "0" : "8px 12px",
              cursor: "pointer",
              color: isWatched ? "#4169e3" : hoveredSeenButton ? "#52c41a" : "#a9a9a9",
            }}
          >
            <span style={filmIcon} className="fas fa-film"></span>
            <span style={{ ...buttonLabelStyle, fontSize: compact ? "inherit" : "16px" }}>SEEN</span>
          </div>
          <div
            onClick={() => {
              const newIsWanted = !isWanted;
              if (!isWanted) {
                let newUserData = { ...userData, moviesToWatch: [...userData.moviesToWatch, id] };
                setUserData(newUserData);
              } else {
                let newUserData = { ...userData, moviesToWatch: userData.moviesToWatch.filter((x) => x !== id) };
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
              width: compact ? "100px" : "160px",
              padding: compact ? "0" : "8px 12px",
              cursor: "pointer",
              color: isWanted ? "#dc143c" : hoveredWantButton ? "#52c41a" : "#a9a9a9",
            }}
          >
            <span style={heartIcon} className="fas fa-heart"></span>
            <span style={{ ...buttonLabelStyle, fontSize: compact ? "inherit" : "16px" }}>WANT</span>
          </div>
        </div>
      </>
    );
  }
  return <></>;
}

export default UserMovieOptions;
