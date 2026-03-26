import { useState, useEffect } from "react";
import { Modal, Spin, Input, Button, Checkbox, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import UserMovieOptions from "./UserMovieOptions";
import "./MovieModal.css";

const { TextArea } = Input;

function EditField({ label, value, onChange, multiline = false }) {
  return (
    <div className="edit-field">
      <label className="edit-field-label">{label}</label>
      {multiline ? (
        <TextArea rows={3} value={value || ""} onChange={(e) => onChange(e.target.value)} />
      ) : (
        <Input value={value || ""} onChange={(e) => onChange(e.target.value)} />
      )}
    </div>
  );
}

function MovieModal({ movieId, open, onClose, actorSearch, userData, setUserData, onToggleViewing, onMovieUpdated }) {
  const [movie, setMovie] = useState(null);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState(false);
  const [editState, setEditState] = useState({});
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (open && movieId) {
      setLoading(true);
      setEditing(false);
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

  function startEditing() {
    setEditState({ ...movie });
    setEditing(true);
  }

  function cancelEditing() {
    setEditing(false);
    setEditState({});
  }

  function updateField(field, value) {
    setEditState((prev) => ({ ...prev, [field]: value }));
  }

  async function saveChanges() {
    setSaving(true);
    try {
      const response = await MovieAPI.updateMovie(editState);
      if (!response.ok) {
        let errorMsg = `Server error (${response.status})`;
        try {
          const errBody = await response.json();
          errorMsg = errBody.message || errorMsg;
        } catch { /* response wasn't JSON */ }
        console.error("UpdateMovie failed:", errorMsg);
        message.error(errorMsg);
        setSaving(false);
        return;
      }
      const result = await response.json();
      if (result.success) {
        setMovie(result.data);
        setEditing(false);
        message.success("Movie updated");
        if (onMovieUpdated) onMovieUpdated(result.data);
      } else {
        message.error(result.message || "Save failed");
      }
    } catch (error) {
      console.error("Error saving movie:", error);
      message.error("Error saving movie");
    }
    setSaving(false);
  }

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={960} wrapClassName="movie-modal">
      {loading ? (
        <Spin />
      ) : movie ? (
        <div className="modal-body-wrapper">
          <div className="modal-poster-column">
            <img
              className="modal-poster"
              alt={movie.title + " poster"}
              src={MovieAPI.getMoviePoster(movie.id)}
            />
          </div>
          <div className="modal-info-panel">
            {!editing ? (
              <>
                <h2 className="modal-movie-title">{movie.title}</h2>

                <div className="modal-meta-row">
                  <span>{new Date(movie.releaseDate).getFullYear()}</span>
                  {movie.rating && <><span className="modal-dot">·</span><span>{movie.rating}</span></>}
                  {movie.runtime && <><span className="modal-dot">·</span><span>{movie.runtime}</span></>}
                </div>

                {movie.genre && <div className="modal-genre">{movie.genre}</div>}

                <div className="modal-crew-grid">
                  {movie.director && (
                    <div className="modal-crew-item">
                      <span className="modal-label">Director</span>
                      <span>{movie.director}</span>
                    </div>
                  )}
                  {movie.writer && (
                    <div className="modal-crew-item">
                      <span className="modal-label">Writer</span>
                      <span>{movie.writer}</span>
                    </div>
                  )}
                </div>

                {movie.plot && <p className="modal-plot">{movie.plot}</p>}

                {movie.actors && (
                  <div className="modal-actors">
                    {movie.actors.split(",").map((actorName, index) => {
                      const actor = actorName.trim();
                      if (!actor) return null;
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
                    })}
                  </div>
                )}

                <div className="modal-ratings-row">
                  <a className="modal-rating-link" target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID}>
                    <span className="modal-label">IMDb</span>
                    <span className="modal-rating-score">{movie.imdbRating}<span className="modal-rating-denom"> / 10</span></span>
                  </a>
                  <a className="modal-rating-link" target="_blank" rel="noreferrer" href={"https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)}>
                    <span className="modal-label">Rotten Tomatoes</span>
                    <span className="modal-rating-score">{movie.tomatoRating}<span className="modal-rating-denom"> / 100</span></span>
                  </a>
                </div>

                <UserMovieOptions userData={userData} id={movie.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />

                <div className="modal-edit-row">
                  <Button type="default" onClick={startEditing}>
                    <span className="fas fa-pen" style={{ marginRight: 6 }} />Edit
                  </Button>
                </div>

                <div className="modal-movie-id">id #{movie.id}</div>
              </>
            ) : (
              <div className="modal-edit-form">
                <EditField label="Title" value={editState.title} onChange={(v) => updateField("title", v)} />
                <EditField label="Simple Title" value={editState.simpleTitle} onChange={(v) => updateField("simpleTitle", v)} />
                <EditField label="Rating" value={editState.rating} onChange={(v) => updateField("rating", v)} />
                <EditField label="Release Date" value={editState.releaseDate ? editState.releaseDate.substring(0, 10) : ""} onChange={(v) => updateField("releaseDate", v)} />
                <EditField label="Runtime" value={editState.runtime} onChange={(v) => updateField("runtime", v)} />
                <EditField label="Genre" value={editState.genre} onChange={(v) => updateField("genre", v)} />
                <EditField label="Director" value={editState.director} onChange={(v) => updateField("director", v)} />
                <EditField label="Writer" value={editState.writer} onChange={(v) => updateField("writer", v)} />
                <EditField label="Actors" value={editState.actors} onChange={(v) => updateField("actors", v)} />
                <EditField label="Plot" value={editState.plot} onChange={(v) => updateField("plot", v)} multiline />
                <EditField label="Poster Link" value={editState.posterLink} onChange={(v) => updateField("posterLink", v)} />
                <EditField label="IMDB Rating" value={editState.imdbRating} onChange={(v) => updateField("imdbRating", v)} />
                <EditField label="IMDB ID" value={editState.imdbID} onChange={(v) => updateField("imdbID", v)} />
                <EditField label="Tomato Rating" value={editState.tomatoRating} onChange={(v) => updateField("tomatoRating", v)} />
                <div className="edit-field">
                  <Checkbox checked={editState.removeFromRandom || false} onChange={(e) => updateField("removeFromRandom", e.target.checked)}>
                    Remove from Random
                  </Checkbox>
                </div>

                <div className="modal-edit-actions">
                  <Button type="primary" onClick={saveChanges} loading={saving}>Save</Button>
                  <Button onClick={cancelEditing}>Cancel</Button>
                </div>
              </div>
            )}
          </div>
        </div>
      ) : (
        <div>Error loading movie</div>
      )}
    </Modal>
  );
}

export default MovieModal;
