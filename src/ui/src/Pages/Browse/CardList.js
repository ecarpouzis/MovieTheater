import { MovieAPI } from "../../MovieAPI";
import { Card, List } from "antd";
import UserMovieOptions from "./UserMovieOptions";
import "./CardList.css";

function CardList({ movieDataArray, userData, setUserData, actorSearch, onMovieClick, onToggleViewing }) {
  return (
    <List
      className="card-list"
      grid={{ gutter: 8, xs: 1, sm: 1, md: 2, lg: 2, xl: 3, xxl: 4 }}
      dataSource={movieDataArray}
      renderItem={(item) => {
        const thumbUrl = MovieAPI.getPosterThumbnail(item.id);

        return (
          <List.Item>
            <Card hoverable className="movie-card">
              <div className="card-content-wrapper">
                <div className="card-poster-container">
                  <img className="card-poster-image" alt="" src={thumbUrl} loading="lazy" />
                </div>
                <div className="card-right-col">
                  <div onClick={() => onMovieClick(item.id)} className="card-title">
                    {item.title} ({new Date(item.releaseDate).getFullYear()})
                  </div>
                  <div className="card-meta-row">
                    {item.rating && <span className="badge-rating">{item.rating}</span>}
                    {item.runtime && <span className="badge-runtime">{item.runtime}</span>}
                    {item.imdbRating && <span className="badge-imdb">&#9733; {item.imdbRating}</span>}
                  </div>
                  <div className="card-actor-row">
                    {item.actors.split(",").map((actor, i) => (
                      <button key={i} type="button" className="actor-link" onClick={() => actorSearch(actor.trim())}>
                        {actor.trim()}
                      </button>
                    ))}
                  </div>
                  <p className="card-plot">{item.plot}</p>
                </div>
              </div>
              <UserMovieOptions userData={userData} id={item.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />
            </Card>
          </List.Item>
        );
      }}
    />
  );
}

export default CardList;
