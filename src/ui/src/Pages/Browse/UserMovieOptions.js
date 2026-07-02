import { MovieAPI } from "../../MovieAPI";
import { useCallback, useRef } from "react";
import "./UserMovieOptions.css";

// Font Awesome was previously pulled in as a render-blocking CDN stylesheet just for a handful of
// glyphs. These inline SVGs (fa-film / fa-heart, solid) replace those two icons so first paint isn't
// blocked on that request. They scale with font-size (1em) and inherit color (currentColor).
function FilmIcon({ className }) {
  return (
    <svg className={className} width="1em" height="1em" viewBox="0 0 512 512" fill="currentColor" aria-hidden="true">
      <path d="M0 96C0 60.7 28.7 32 64 32H448c35.3 0 64 28.7 64 64V416c0 35.3-28.7 64-64 64H64c-35.3 0-64-28.7-64-64V96zM48 368v32c0 8.8 7.2 16 16 16H96c8.8 0 16-7.2 16-16V368c0-8.8-7.2-16-16-16H64c-8.8 0-16 7.2-16 16zm368-16c-8.8 0-16 7.2-16 16v32c0 8.8 7.2 16 16 16h32c8.8 0 16-7.2 16-16V368c0-8.8-7.2-16-16-16H416zM48 240v32c0 8.8 7.2 16 16 16H96c8.8 0 16-7.2 16-16V240c0-8.8-7.2-16-16-16H64c-8.8 0-16 7.2-16 16zm368-16c-8.8 0-16 7.2-16 16v32c0 8.8 7.2 16 16 16h32c8.8 0 16-7.2 16-16V240c0-8.8-7.2-16-16-16H416zM48 112v32c0 8.8 7.2 16 16 16H96c8.8 0 16-7.2 16-16V112c0-8.8-7.2-16-16-16H64c-8.8 0-16 7.2-16 16zM416 96c-8.8 0-16 7.2-16 16v32c0 8.8 7.2 16 16 16h32c8.8 0 16-7.2 16-16V112c0-8.8-7.2-16-16-16H416zM160 128v96c0 17.7 14.3 32 32 32H320c17.7 0 32-14.3 32-32V128c0-17.7-14.3-32-32-32H192c-17.7 0-32 14.3-32 32zm0 160c-17.7 0-32 14.3-32 32v96c0 17.7 14.3 32 32 32H320c17.7 0 32-14.3 32-32V320c0-17.7-14.3-32-32-32H192z" />
    </svg>
  );
}

function HeartIcon({ className }) {
  return (
    <svg className={className} width="1em" height="1em" viewBox="0 0 512 512" fill="currentColor" aria-hidden="true">
      <path d="M47.6 300.4L228.3 469.1c7.5 7 17.4 10.9 27.7 10.9s20.2-3.9 27.7-10.9L464.4 300.4c30.4-28.3 47.6-68 47.6-109.5v-5.8c0-69.9-50.5-129.5-119.4-141C347 36.5 300.6 51.4 268 84L256 96 244 84c-32.6-32.6-79-47.5-124.6-39.9C50.5 55.6 0 115.2 0 185.1v5.8c0 41.5 17.2 81.2 47.6 109.5z" />
    </svg>
  );
}

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
        <FilmIcon className="film-icon" />
        <span className={`viewing-btn-label${inline ? " viewing-btn-label--compact" : ""}`}>SEEN</span>
      </div>
      <div
        onClick={() => onToggleWant(id, kind)}
        className={`viewing-btn zoom-on-hover${inline ? " viewing-btn--compact" : ""}${isWanted ? " viewing-btn-want--wanted" : ""}`}
      >
        <HeartIcon className="heart-icon" />
        <span className={`viewing-btn-label${inline ? " viewing-btn-label--compact" : ""}`}>WANT</span>
      </div>
    </div>
  );
}

export default UserMovieOptions;
