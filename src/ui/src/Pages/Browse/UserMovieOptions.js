import { MovieAPI } from "../../MovieAPI";
import "./UserMovieOptions.css";

function UserMovieOptions({ userData, id, kind = "movie", setUserData, inline = false, onToggleViewing }) {
  if (userData) {
    const isWatched = userData.moviesSeen.includes(id);
    const isWanted = userData.moviesToWatch.includes(id);
    return (
      <>
        <div className={`viewing-options${inline ? " viewing-options--compact" : ""}`}>
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
              MovieAPI.setWatchedState(userData.username, id, newIsWatched, kind)
                .then((response) => response.json())
                .then((response) => {
                  if (!response.success) {
                    alert(response.message);
                  }
                });
            }}
            className={`viewing-btn zoom-on-hover${inline ? " viewing-btn--compact" : ""}${isWatched ? " viewing-btn-seen--watched" : ""}`}
          >
            <span className="film-icon fas fa-film"></span>
            <span className={`viewing-btn-label${inline ? " viewing-btn-label--compact" : ""}`}>SEEN</span>
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
              MovieAPI.setWantToWatchState(userData.username, id, newIsWanted, kind)
                .then((response) => response.json())
                .then((response) => {
                  if (!response.success) {
                    alert(response.message);
                  }
                });
            }}
            className={`viewing-btn zoom-on-hover${inline ? " viewing-btn--compact" : ""}${isWanted ? " viewing-btn-want--wanted" : ""}`}
          >
            <span className="heart-icon fas fa-heart"></span>
            <span className={`viewing-btn-label${inline ? " viewing-btn-label--compact" : ""}`}>WANT</span>
          </div>
        </div>
      </>
    );
  }
  return <></>;
}

export default UserMovieOptions;
