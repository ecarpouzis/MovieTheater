import { MovieAPI } from "../../MovieAPI";
import { useCallback, useRef } from "react";
import { EyeOutlined, EyeFilled, HeartOutlined, HeartFilled } from "@ant-design/icons";
import "./UserMovieOptions.css";

// Stable seen/want toggle callbacks. The latest userData is kept in a ref so the returned
// callbacks never change identity across renders — essential for the memoized cards, where a
// fresh closure per render would defeat React.memo. Each toggle optimistically updates userData,
// notifies the parent, then persists to the API.
export function useViewingToggles(userData, setUserData, onToggleViewing) {
  const userDataRef = useRef(userData);
  userDataRef.current = userData;

  const toggleSeen = useCallback(
    (id, kind = "movie") => {
      const ud = userDataRef.current;
      if (!ud) return;
      const newIsWatched = !ud.moviesSeen.includes(id);
      setUserData(
        newIsWatched
          ? { ...ud, moviesSeen: [...ud.moviesSeen, id] }
          : { ...ud, moviesSeen: ud.moviesSeen.filter((x) => x !== id) }
      );
      if (typeof onToggleViewing === "function") onToggleViewing(id, "SetWatched", newIsWatched);
      MovieAPI.setWatchedState(ud.username, id, newIsWatched, kind)
        .then((response) => response.json())
        .then((response) => {
          if (!response.success) alert(response.message);
        });
    },
    [setUserData, onToggleViewing]
  );

  const toggleWant = useCallback(
    (id, kind = "movie") => {
      const ud = userDataRef.current;
      if (!ud) return;
      const newIsWanted = !ud.moviesToWatch.includes(id);
      setUserData(
        newIsWanted
          ? { ...ud, moviesToWatch: [...ud.moviesToWatch, id] }
          : { ...ud, moviesToWatch: ud.moviesToWatch.filter((x) => x !== id) }
      );
      if (typeof onToggleViewing === "function") onToggleViewing(id, "SetWantToWatch", newIsWanted);
      MovieAPI.setWantToWatchState(ud.username, id, newIsWanted, kind)
        .then((response) => response.json())
        .then((response) => {
          if (!response.success) alert(response.message);
        });
    },
    [setUserData, onToggleViewing]
  );

  return { toggleSeen, toggleWant };
}

// Presentational Seen/Want buttons. Receives resolved booleans + id/kind + stable toggle callbacks
// (from useViewingToggles) rather than the whole userData object, so it can live inside a memoized
// card without pulling userData through and defeating the memo.
function UserMovieOptions({ id, kind = "movie", isWatched, isWanted, onToggleSeen, onToggleWant, inline = false }) {
  return (
    <div className={`viewing-options${inline ? " viewing-options--compact" : ""}`}>
      <div
        onClick={() => onToggleSeen(id, kind)}
        className={`viewing-btn zoom-on-hover${inline ? " viewing-btn--compact" : ""}${isWatched ? " viewing-btn-seen--watched" : ""}`}
      >
        {isWatched ? <EyeFilled className="viewing-btn-icon" /> : <EyeOutlined className="viewing-btn-icon" />}
        <span className={`viewing-btn-label${inline ? " viewing-btn-label--compact" : ""}`}>Seen</span>
      </div>
      <div
        onClick={() => onToggleWant(id, kind)}
        className={`viewing-btn zoom-on-hover${inline ? " viewing-btn--compact" : ""}${isWanted ? " viewing-btn-want--wanted" : ""}`}
      >
        {isWanted ? <HeartFilled className="viewing-btn-icon" /> : <HeartOutlined className="viewing-btn-icon" />}
        <span className={`viewing-btn-label${inline ? " viewing-btn-label--compact" : ""}`}>Want</span>
      </div>
    </div>
  );
}

export default UserMovieOptions;
