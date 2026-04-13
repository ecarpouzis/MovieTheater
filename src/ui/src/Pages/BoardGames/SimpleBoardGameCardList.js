import { Card } from "antd";
import "../Browse/CardList.css";
import "../Browse/UserMovieOptions.css";

function UserBoardGameOptions({ userData, id }) {
  if (!userData) return null;

  const isPlayed = false;
  const isWanted = false;

  const handlePlayedClick = () => {
    console.log("Played clicked for game:", id);
  };

  const handleWantClick = () => {
    console.log("Want clicked for game:", id);
  };

  return (
    <div className="viewing-options">
      <div onClick={handlePlayedClick} className={`viewing-btn zoom-on-hover${isPlayed ? " viewing-btn-seen--watched" : ""}`}>
        <span className="film-icon fas fa-film"></span>
        <span className="viewing-btn-label">PLAYED</span>
      </div>
      <div onClick={handleWantClick} className={`viewing-btn zoom-on-hover${isWanted ? " viewing-btn-want--wanted" : ""}`}>
        <span className="heart-icon fas fa-heart"></span>
        <span className="viewing-btn-label">WANT</span>
      </div>
    </div>
  );
}

function SimpleBoardGameCardList({ gameDataArray, userData, setUserData, onGameClick }) {
  return (
    <div className="card-list">
      {gameDataArray.map((item) => {
        return (
          <div key={item.id}>
            <Card hoverable className="movie-card">
              <div className="card-content-wrapper">
                <div className="card-poster-container">
                  <img
                    className="card-poster-image"
                    alt={item.title}
                    src={item.thumbnailUrl || item.placeholderImage}
                    loading="lazy"
                    onError={(e) => {
                      if (item.placeholderImage && e.currentTarget.src !== item.placeholderImage) {
                        e.currentTarget.onerror = null;
                        e.currentTarget.src = item.placeholderImage;
                      }
                    }}
                  />
                </div>
                <div className="card-right-col">
                  <div onClick={() => onGameClick(item.id)} className="card-title">
                    {item.title} ({new Date(item.releaseDate).getFullYear()})
                  </div>
                  <div className="card-meta-row">
                    {item.players && <span className="badge-rating">{item.players} Players</span>}
                    <span className="badge-rating">Ages {item.ageRange || "0-3"}</span>
                    {item.playtime && <span className="badge-runtime">{item.playtime}</span>}
                    {item.complexity && <span className="badge-imdb">{item.complexity}</span>}
                  </div>
                  <p className="card-plot">{item.description || "Description coming soon."}</p>
                </div>
              </div>
              <UserBoardGameOptions userData={userData} id={item.id} />
            </Card>
          </div>
        );
      })}
    </div>
  );
}

export default SimpleBoardGameCardList;
