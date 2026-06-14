import { useState, useEffect, useRef } from "react";
import { Modal, Spin, Input, Button, Checkbox, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import UserMovieOptions from "./UserMovieOptions";
import WatchButton from "../Watch/WatchButton";
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

// Format whole minutes as e.g. "2h 16m" / "47m", matching IMDB's normalized runtime.
function formatRuntime(minutes) {
  if (!minutes || minutes <= 0) return null;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return h > 0 ? `${h}h${m ? " " + m + "m" : ""}` : `${m}m`;
}

// Parse a stored "#RRGGBB" dominant poster color into an "r, g, b" triple for use
// in rgba() ambient tints. Falls back to a neutral slate when unavailable.
function posterRgb(hex) {
  const m = /^#?([0-9a-fA-F]{6})$/.exec((hex || "").trim());
  if (!m) return "90, 95, 110";
  const int = parseInt(m[1], 16);
  return `${(int >> 16) & 255}, ${(int >> 8) & 255}, ${int & 255}`;
}

function MovieModal({ movieId, open, onClose, actorSearch, userData, setUserData, onToggleViewing, onMovieUpdated }) {
  const [movie, setMovie] = useState(null);
  const [normalized, setNormalized] = useState(null);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState(false);
  const [editState, setEditState] = useState({});
  const [saving, setSaving] = useState(false);
  const [plotExpanded, setPlotExpanded] = useState(false);
  const [plotOverflows, setPlotOverflows] = useState(false);
  const [synopsisOpen, setSynopsisOpen] = useState(false);
  const plotRef = useRef(null);

  useEffect(() => {
    if (open && movieId) {
      setLoading(true);
      setEditing(false);
      setPlotExpanded(false);
      setPlotOverflows(false);
      setSynopsisOpen(false);
      MovieAPI.getMovie(movieId)
        .then((response) => response.json())
        .then((responseData) => {
          setMovie(responseData.data);
          setNormalized(responseData.normalized || null);
          setLoading(false);
        })
        .catch((error) => {
          console.error("Error fetching movie:", error);
          setLoading(false);
        });
    }
  }, [movieId, open]);

  useEffect(() => {
    const el = plotRef.current;
    if (!el) return;
    const check = () => setPlotOverflows(el.scrollHeight > 120);
    check();
    const observer = new ResizeObserver(check);
    observer.observe(el);
    return () => observer.disconnect();
  }, [movie]);

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
        } catch {
          /* response wasn't JSON */
        }
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

  // Prefer the normalized IMDB data; fall back to the legacy comma-separated
  // columns for movies the scrape hasn't reached yet.
  const n = normalized || {};
  const displayRuntime = formatRuntime(n.runtimeMinutes) || movie?.runtime;
  const genreList =
    Array.isArray(n.genres) && n.genres.length > 0
      ? n.genres
      : movie?.genre
      ? movie.genre.split(",").map((s) => s.trim()).filter(Boolean)
      : [];
  const displayDirectors =
    Array.isArray(n.directors) && n.directors.length > 0 ? n.directors.map((p) => p.name).join(", ") : movie?.director;
  const displayWriters =
    Array.isArray(n.writers) && n.writers.length > 0 ? n.writers.map((p) => p.name).join(", ") : movie?.writer;
  const displayPlot = n.plotFull || movie?.plot;
  const castList =
    Array.isArray(n.cast) && n.cast.length > 0
      ? n.cast
      : movie?.actors
      ? movie.actors.split(",").map((a) => ({ name: a.trim(), character: null })).filter((a) => a.name)
      : [];
  const hasSynopsis = !!(n.plotSynopsis || (Array.isArray(n.summaries) && n.summaries.length > 0));

  const searchPerson = (name) => {
    if (!name) return;
    onClose();
    actorSearch(name);
  };

  // Render a list of people as subtle, comma-separated search links. Prefers the
  // normalized array ([{name}]); falls back to a legacy comma-separated string.
  const renderPeopleLinks = (people, legacy) => {
    const names =
      Array.isArray(people) && people.length > 0
        ? people.map((p) => p.name).filter(Boolean)
        : legacy
        ? legacy.split(",").map((s) => s.trim()).filter(Boolean)
        : [];
    return names.map((name, i) => (
      <span key={i}>
        {i > 0 ? ", " : ""}
        <button type="button" className="person-link" onClick={() => searchPerson(name)}>
          {name}
        </button>
      </span>
    ));
  };

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={960}
      wrapClassName="movie-modal"
      style={{ "--poster-rgb": posterRgb(movie?.posterDetails?.dominantColor) }}
    >
      {loading ? (
        <Spin />
      ) : movie ? (
        <div className="modal-body-wrapper">
          <div className="modal-poster-column">
            <div className="modal-poster-frame">
              <img className="modal-poster" alt={movie.title + " poster"} src={MovieAPI.getMoviePoster(movie.id, movie.posterVersion)} />
            </div>
          </div>
          <div className="modal-info-panel">
            {!editing ? (
              <>
                <h2 className="modal-movie-title">{movie.title}</h2>

                <div className="modal-meta-row">
                  <span>{new Date(movie.releaseDate).getFullYear()}</span>
                  {movie.rating && (
                    <>
                      <span className="modal-dot">·</span>
                      <span>{movie.rating}</span>
                    </>
                  )}
                  {displayRuntime && (
                    <>
                      <span className="modal-dot">·</span>
                      <span>{displayRuntime}</span>
                    </>
                  )}
                </div>

                {genreList.length > 0 && (
                  <div className="modal-genre-chips">
                    {genreList.map((g, i) => (
                      <span className="modal-genre-chip" key={i}>
                        {g}
                      </span>
                    ))}
                  </div>
                )}

                <div className="modal-crew-grid">
                  {displayDirectors && (
                    <div className="modal-crew-item">
                      <span className="modal-label">Director</span>
                      <span>{renderPeopleLinks(n.directors, movie?.director)}</span>
                    </div>
                  )}
                  {displayWriters && (
                    <div className="modal-crew-item">
                      <span className="modal-label">Writer</span>
                      <span>{renderPeopleLinks(n.writers, movie?.writer)}</span>
                    </div>
                  )}
                </div>

                {displayPlot && (
                  <>
                    <div className={`modal-plot-wrap${plotOverflows && !plotExpanded ? " modal-plot-wrap--collapsed" : ""}`}>
                      <p ref={plotRef} className="modal-plot">{displayPlot}</p>
                    </div>
                    {plotOverflows && (
                      <button className="modal-desc-toggle" onClick={() => setPlotExpanded((v) => !v)}>
                        {plotExpanded ? "Show less ↑" : "Show more ↓"}
                      </button>
                    )}
                  </>
                )}

                {hasSynopsis && (
                  <div className="modal-synopsis">
                    <button className="modal-desc-toggle" onClick={() => setSynopsisOpen((v) => !v)}>
                      {synopsisOpen ? "Hide full synopsis ↑" : "Read full synopsis ↓"}
                    </button>
                    {synopsisOpen && (
                      <div className="modal-synopsis-body">
                        {n.plotSynopsis && <p className="modal-synopsis-text">{n.plotSynopsis}</p>}
                        {Array.isArray(n.summaries) &&
                          n.summaries.map((s, i) => (
                            <p key={i} className="modal-summary-text">
                              {s.text}
                              {s.author ? <span className="modal-summary-author"> — {s.author}</span> : null}
                            </p>
                          ))}
                      </div>
                    )}
                  </div>
                )}

                {castList.length > 0 && (
                  <div className="modal-cast">
                    {castList.map((c, index) => (
                      <span className="modal-cast-item" key={index}>
                        <button type="button" className="person-link" onClick={() => searchPerson(c.name)}>
                          {c.name}
                        </button>
                        {c.character ? <span className="modal-cast-character"> as {c.character}</span> : null}
                      </span>
                    ))}
                  </div>
                )}

                <div className="modal-ratings-row">
                  <a className="modal-rating-link" target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID}>
                    <span className="modal-label">IMDb</span>
                    <span className="modal-rating-score">
                      {movie.imdbRating}
                      <span className="modal-rating-denom"> / 10</span>
                    </span>
                  </a>
                  <a
                    className="modal-rating-link"
                    target="_blank"
                    rel="noreferrer"
                    href={movie.rtUrl || "https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)}
                  >
                    <span className="modal-label">Tomatometer</span>
                    <span className="modal-rating-score">
                      {movie.rtTomatometer != null ? movie.rtTomatometer : movie.tomatoRating}
                      <span className="modal-rating-denom"> / 100</span>
                    </span>
                  </a>
                  <a
                    className="modal-rating-link"
                    target="_blank"
                    rel="noreferrer"
                    href={movie.rtUrl || "https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)}
                  >
                    <span className="modal-label">Popcornmeter</span>
                    <span className="modal-rating-score">
                      {movie.rtPopcornmeter}
                      <span className="modal-rating-denom"> / 100</span>
                    </span>
                  </a>
                </div>

                <div className="modal-actions-row">
                  <div className="modal-watch-row">
                    <WatchButton movie={movie} userData={userData} onBeforeNavigate={onClose} />
                  </div>
                  <UserMovieOptions userData={userData} id={movie.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />
                </div>

                {userData?.canEditMovies && (
                  <div className="modal-edit-row">
                    <Button type="default" onClick={startEditing}>
                      <span className="fas fa-pen" style={{ marginRight: 6 }} />
                      Edit
                    </Button>
                  </div>
                )}

                <div className="modal-movie-id">id #{movie.id}</div>
              </>
            ) : (
              <div className="modal-edit-form">
                <EditField label="Title" value={editState.title} onChange={(v) => updateField("title", v)} />
                <EditField label="Simple Title" value={editState.simpleTitle} onChange={(v) => updateField("simpleTitle", v)} />
                <EditField label="Rating" value={editState.rating} onChange={(v) => updateField("rating", v)} />
                <EditField
                  label="Release Date"
                  value={editState.releaseDate ? editState.releaseDate.substring(0, 10) : ""}
                  onChange={(v) => updateField("releaseDate", v)}
                />
                <EditField label="Runtime" value={editState.runtime} onChange={(v) => updateField("runtime", v)} />
                <EditField label="Genre" value={editState.genre} onChange={(v) => updateField("genre", v)} />
                <EditField label="Director" value={editState.director} onChange={(v) => updateField("director", v)} />
                <EditField label="Writer" value={editState.writer} onChange={(v) => updateField("writer", v)} />
                <EditField label="Actors" value={editState.actors} onChange={(v) => updateField("actors", v)} />
                <EditField label="Plot" value={editState.plot} onChange={(v) => updateField("plot", v)} multiline />
                <EditField label="Poster Link" value={editState.posterLink} onChange={(v) => updateField("posterLink", v)} />
                <EditField label="IMDB Rating" value={editState.imdbRating} onChange={(v) => updateField("imdbRating", v)} />
                <EditField label="IMDB ID" value={editState.imdbID} onChange={(v) => updateField("imdbID", v)} />
                <EditField label="Tomato Rating (legacy)" value={editState.tomatoRating} onChange={(v) => updateField("tomatoRating", v)} />
                <EditField label="RT Tomatometer" value={editState.rtTomatometer} onChange={(v) => updateField("rtTomatometer", v)} />
                <EditField label="RT Popcornmeter" value={editState.rtPopcornmeter} onChange={(v) => updateField("rtPopcornmeter", v)} />
                <div className="edit-field">
                  <Checkbox checked={editState.removeFromRandom || false} onChange={(e) => updateField("removeFromRandom", e.target.checked)}>
                    Remove from Random
                  </Checkbox>
                </div>

                <div className="modal-edit-actions">
                  <Button type="primary" onClick={saveChanges} loading={saving}>
                    Save
                  </Button>
                  <Button className="btn-cancel" onClick={cancelEditing}>
                    Cancel
                  </Button>
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
