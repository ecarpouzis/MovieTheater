import { useState, useEffect, useRef } from "react";
import { useHistory } from "react-router-dom";
import { Modal, Spin, Input, Button, Checkbox, Slider, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import UserMovieOptions, { useViewingToggles } from "./UserMovieOptions";
import WatchButton from "../Watch/WatchButton";
import FileMappingEditor from "./FileMappingEditor";
import SubtitlePicker from "./SubtitlePicker";
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

function basename(p) {
  if (!p) return "";
  const parts = String(p).split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] || String(p);
}

// Human labels for TitleType (the enum name) and MovieFileRole.
const TYPE_LABEL = {
  Short: "Short", TvSeries: "TV Series", TvMiniSeries: "TV Mini-Series", TvMovie: "TV Movie",
  TvSpecial: "TV Special", TvShort: "TV Short", Video: "Video",
};
const ROLE_LABEL = { Primary: "Feature", Part: "Part", Variant: "Variant", Extra: "Extra" };

// The viewer's own 0–100 score — a bar with a draggable handle. 0 is a real score (the floor): once set,
// the rating exists; "Clear" removes it (unrated = no row). Optimistically updates userData.ratings (so the
// Rate page and any badges reflect it at once) and persists on release. Keyed by "{kind}:{id}" because
// MiscVideo shares an id range with movies.
function YourRating({ id, kind, userData, setUserData }) {
  const key = `${kind}:${id}`;
  const isRated = !!(userData && userData.ratings && key in userData.ratings);
  const stored = isRated ? userData.ratings[key] : 0;
  const [value, setValue] = useState(stored);

  // Re-seed when the modal switches titles or the stored score changes elsewhere.
  useEffect(() => {
    setValue(stored);
  }, [key, stored]);

  // v is a real 0–100 score, or null to clear (unrate).
  const persist = (v) => {
    const next = { ...(userData.ratings || {}) };
    if (v == null) delete next[key];
    else next[key] = v;
    setUserData({ ...userData, ratings: next });
    MovieAPI.setRating(id, v, kind)
      .then((r) => r.json())
      .then((d) => {
        if (!d || !d.success) throw new Error("save failed");
      })
      .catch(() => {
        message.error("Couldn't save your rating.");
        const revert = { ...(userData.ratings || {}) };
        if (isRated) revert[key] = stored;
        else delete revert[key];
        setUserData({ ...userData, ratings: revert });
        setValue(stored);
      });
  };

  return (
    <div className="modal-your-rating">
      <span className="modal-label">Your Rating</span>
      <Slider className="your-rating-slider" min={0} max={100} value={value} onChange={setValue} onChangeComplete={(v) => persist(v)} />
      <span className="your-rating-score">{isRated ? value : "—"}</span>
      {isRated && (
        <button type="button" className="your-rating-clear" title="Remove your rating" onClick={() => persist(null)}>
          Clear
        </button>
      )}
    </div>
  );
}

function MovieModal({ movieId, open, onClose, actorSearch, onBrowse, onOpenTitle, userData, setUserData, onToggleViewing, onMovieUpdated, onAddToPlaylist, kind = "movie" }) {
  const history = useHistory();
  const { toggleSeen, toggleWant } = useViewingToggles(userData, setUserData, onToggleViewing);
  const [openSeasons, setOpenSeasons] = useState({});
  const [openEps, setOpenEps] = useState({});
  const isSeries = kind === "series";
  const [movie, setMovie] = useState(null);
  const [normalized, setNormalized] = useState(null);
  // Franchise rail: { defaultFranchise, franchises:[{value,count,items}] } + which franchise is shown.
  const [franchiseRail, setFranchiseRail] = useState(null);
  const [activeFranchise, setActiveFranchise] = useState(null);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState(false);
  const [editState, setEditState] = useState({});
  const [saving, setSaving] = useState(false);
  const [plotExpanded, setPlotExpanded] = useState(false);
  const [plotOverflows, setPlotOverflows] = useState(false);
  const [synopsisOpen, setSynopsisOpen] = useState(false);
  const [thumbMissing, setThumbMissing] = useState(false);
  const [genThumb, setGenThumb] = useState(false);
  const plotRef = useRef(null);

  // Detect a title that has a full poster but no thumbnail (so the card shows a broken placeholder):
  // probe the thumbnail route and, on error, surface the "Generate thumbnail" button in edit mode.
  useEffect(() => {
    if (!movie?.id) { setThumbMissing(false); return; }
    let cancelled = false;
    const probe = new Image();
    probe.onload = () => { if (!cancelled) setThumbMissing(false); };
    probe.onerror = () => { if (!cancelled) setThumbMissing(true); };
    probe.src = `${isSeries ? "/SeriesImageThumb" : "/ImageThumb"}/${movie.id}?probe=1`;
    return () => { cancelled = true; };
  }, [movie?.id, isSeries]);

  async function generateThumbnail() {
    setGenThumb(true);
    try {
      const res = await MovieAPI.generateThumbnail(movie.id, isSeries);
      const b = await res.json().catch(() => ({}));
      if (!res.ok || !b.success) { message.error(b.message || "Thumbnail generation failed"); return; }
      message.success("Thumbnail generated");
      setThumbMissing(false);
    } catch {
      message.error("Thumbnail generation failed");
    } finally {
      setGenThumb(false);
    }
  }

  useEffect(() => {
    if (open && movieId) {
      setLoading(true);
      setEditing(false);
      setPlotExpanded(false);
      setPlotOverflows(false);
      setSynopsisOpen(false);
      setOpenSeasons({});
      MovieAPI.getTitle(movieId, kind)
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
  }, [movieId, open, kind]);

  // Franchise rail — fetched on its own so the modal opens fast; the endpoint returns an empty set
  // when the title has no franchise, so we just hide the section then.
  useEffect(() => {
    if (!open || !movieId) { setFranchiseRail(null); setActiveFranchise(null); return; }
    let cancelled = false;
    MovieAPI.getFranchiseRail(movieId, kind)
      .then((r) => r.json())
      .then((data) => {
        if (cancelled) return;
        const fr = data && Array.isArray(data.franchises) ? data : null;
        setFranchiseRail(fr && fr.franchises.length ? fr : null);
        setActiveFranchise(fr && fr.franchises.length ? fr.defaultFranchise : null);
      })
      .catch(() => { if (!cancelled) { setFranchiseRail(null); setActiveFranchise(null); } });
    return () => { cancelled = true; };
  }, [movieId, open, kind]);

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

  async function refetchFromImdb() {
    setSaving(true);
    try {
      const res = await MovieAPI.refetchTitle(movie.id, kind);
      if (!res.ok) {
        const b = await res.json().catch(() => ({}));
        message.error(b.message || "Re-fetch failed");
        return;
      }
      const fresh = await MovieAPI.getTitle(movie.id, kind).then((r) => r.json());
      setMovie(fresh.data);
      setNormalized(fresh.normalized || null);
      message.success("Re-fetched from IMDb");
      if (onMovieUpdated) onMovieUpdated(fresh.data);
    } catch {
      message.error("Re-fetch failed");
    } finally {
      setSaving(false);
    }
  }

  // The movie's file was replaced on disk (new rip / renamed folder)? Re-scan just this title's shelf in
  // Jellyfin, then re-point the existing file row to the new file (+ any new extras) in place — keeping
  // every rating/viewing/poster/tag (those live on the movie, not the file). No full-library scan.
  async function relinkFromDisk() {
    setSaving(true);
    const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
    let hide = message.loading("Locating this title's folder in Jellyfin…", 0);
    try {
      const trg = await MovieAPI.relinkRefresh(movie.id);
      const td = await trg.json().catch(() => ({}));
      if (!trg.ok) {
        hide();
        message.error(td.message || "Couldn't start the re-scan.");
        return;
      }
      // Poll the idempotent probe until Jellyfin has indexed the new file (bounded ~60s). The shelf folder
      // id from the trigger scopes each poll to just that folder.
      let done = null;
      let lastMsg = "";
      for (let i = 0; i < 20; i++) {
        hide();
        hide = message.loading(`Waiting for Jellyfin to index the new file… (${i + 1}/20)`, 0);
        await sleep(3000);
        const ap = await MovieAPI.relinkApply(movie.id, td.shelfItemId);
        const r = await ap.json().catch(() => ({}));
        if (!ap.ok) {
          hide();
          message.error(r.message || "Re-link failed.");
          return;
        }
        if (r.message) lastMsg = r.message;
        if (r.done) {
          done = r;
          break;
        }
      }
      hide();
      if (!done) {
        message.warning(lastMsg ? `Still indexing — ${lastMsg}` : "Still indexing — give it a moment and click again.");
        return;
      }
      const fresh = await MovieAPI.getTitle(movie.id, kind).then((r) => r.json());
      setMovie(fresh.data);
      setNormalized(fresh.normalized || null);
      if (onMovieUpdated) onMovieUpdated(fresh.data);
      const extras = (done.extrasAdded || []).length;
      const extraNote = extras ? ` (+${extras} extra${extras > 1 ? "s" : ""})` : "";
      message.success(
        done.primaryRepointed
          ? `Re-linked to the new file${extraNote}. Watch is fixed.`
          : `Already linked to the current file${extraNote}.`
      );
    } catch {
      hide();
      message.error("Re-link failed.");
    } finally {
      setSaving(false);
    }
  }

  async function saveChanges() {
    setSaving(true);
    try {
      const response = await (isSeries ? MovieAPI.updateSeries(editState) : MovieAPI.updateMovie(editState));
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
  // The franchise whose rail is currently shown (toggled via the chips when a title is in several).
  const activeRail =
    franchiseRail && activeFranchise
      ? franchiseRail.franchises.find((f) => f.value === activeFranchise) || franchiseRail.franchises[0]
      : null;
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
  // Cards read ImdbRatingScraped (exposed as normalized.imdbRating); the modal must too, or ingested
  // titles (legacy imdbRating NULL) show a rating on the card but a blank one here.
  const displayImdbRating = n.imdbRating ?? movie?.imdbRating;
  const castList =
    Array.isArray(n.cast) && n.cast.length > 0
      ? n.cast
      : movie?.actors
      ? movie.actors.split(",").map((a) => ({ name: a.trim(), character: null })).filter((a) => a.name)
      : [];
  const hasSynopsis = !!(n.plotSynopsis || (Array.isArray(n.summaries) && n.summaries.length > 0));
  // TMDB-sourced YouTube trailer key (Movie/Series share the column). Embedded on demand below.
  const trailerKey = movie?.trailerKey;

  // Phase-7 surfaces: multi-file list + (for series) episodes by season.
  const toggleSeason = (s) => setOpenSeasons((prev) => ({ ...prev, [s]: !prev[s] }));
  const toggleEp = (s, e) => setOpenEps((prev) => ({ ...prev, [`${s}-${e}`]: !prev[`${s}-${e}`] }));
  // Label an episode file's role for the modal (an episode's main file is "Main", not a movie "Feature").
  const epRoleLabel = (f) => (f.role === "Primary" ? "Main" : (ROLE_LABEL[f.role] || f.role)) + (f.partNumber ? " " + f.partNumber : "");
  const files = Array.isArray(n.files) ? n.files : [];
  const showFiles = files.length > 1; // a single Feature isn't worth a section
  const seasons = n.isSeries && Array.isArray(n.seasons) ? n.seasons : [];
  const seriesExtras = Array.isArray(n.seriesExtras) ? n.seriesExtras : [];
  const relatedMisc = Array.isArray(n.relatedMisc) ? n.relatedMisc : [];
  const totalEps = seasons.reduce((acc, s) => acc + s.episodes.length, 0);
  const epsWithFile = seasons.reduce((acc, s) => acc + s.episodes.filter((e) => e.hasFile).length, 0);
  const typeBadge = TYPE_LABEL[n.titleType];

  // Open the screening room for a specific episode / file. Gated on a password the
  // same way WatchButton is — streaming isn't advertised to accounts that can't use it.
  const canStream = !!userData?.hasPassword;
  const goWatch = (qs) => {
    onClose();
    history.push(`/watch/${movie.id}${qs}`);
  };

  // ── Playlists & watch parties ──
  // A movie contributes its own playable; a series contributes each streamable episode (with a labelled
  // title), so "add this season" / "add the whole show" pull in many at once. Items are {playableId, title}.
  const seasonItems = (s) => (s.episodes || [])
    .filter((e) => e.isPlayable && e.playableId != null)
    .map((e) => ({ playableId: e.playableId, title: `${movie.title} — S${s.season}E${e.episode}${e.title ? " " + e.title : ""}` }));
  const allSeriesItems = () => seasons.flatMap(seasonItems);
  const movieItem = () => (movie?.playableId != null ? [{ playableId: movie.playableId, title: movie.title }] : []);

  const addToPlaylist = (items, name) => {
    if (!items.length || !onAddToPlaylist) return;
    onClose();
    onAddToPlaylist(items, name || movie.title);
  };

  // Create a watch party from a set of titles and jump straight to its shareable lobby.
  const startWatchTogether = async (items, name) => {
    if (!items.length) return;
    try {
      const r = await MovieAPI.createPlaylist(name || movie.title, items.map((i) => i.playableId), true);
      if (!r.ok) throw new Error();
      const res = await r.json();
      onClose();
      if (res.watchpartyToken) history.push(`/watch-together/${res.watchpartyToken}`);
    } catch {
      message.error("Couldn't start the watch party.");
    }
  };

  const searchPerson = (name) => {
    if (!name) return;
    onClose();
    actorSearch(name);
  };

  // Insight chips jump to a browse search and close the modal. Franchise → the franchise grid;
  // a "watch if you liked" comp title → a title search (so a library match surfaces, if we have it).
  const searchFranchise = (f) => {
    if (!f || !onBrowse) return;
    onClose();
    onBrowse("franchise", f);
  };
  const searchComp = (title) => {
    if (!title || !onBrowse) return;
    onClose();
    onBrowse("title", title);
  };
  // Franchise tags are stored normalized/lowercase ("studio-ghibli", "mcu"); prettify for display
  // but pass the raw value to the franchise browse.
  const prettifyTag = (v) =>
    (v || "").split(/[-_\s]+/).map((w) => (w ? w[0].toUpperCase() + w.slice(1) : w)).join(" ");

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
      // Above the nav bar (z-index 1300) so the full-screen mobile modal — and its pinned
      // close button — render over it instead of being trapped beneath it.
      zIndex={1500}
      wrapClassName="movie-modal"
      style={{ "--poster-rgb": posterRgb(movie?.posterDetails?.dominantColor) }}
    >
      {loading ? (
        <Spin />
      ) : movie ? (
        <div className="modal-body-wrapper">
          <div className="modal-poster-column">
            <div className="modal-poster-frame">
              <img className="modal-poster" alt={movie.title + " poster"} src={MovieAPI.getMoviePoster(movie.id, movie.posterVersion, kind)} />
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
                  {typeBadge && (
                    <>
                      <span className="modal-dot">·</span>
                      <span className="modal-type-badge">{typeBadge}</span>
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

                {trailerKey && (
                  <div className="modal-trailer">
                    {/* Show the trailer player inline (IMDB-style) — the YouTube embed
                        renders the trailer thumbnail with a large play button, so it
                        reads as a trailer at a glance without autoplaying on open. */}
                    <div className="modal-trailer-player">
                      <iframe
                        src={`https://www.youtube-nocookie.com/embed/${trailerKey}?rel=0&modestbranding=1`}
                        title={`${movie.title} — trailer`}
                        allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
                        allowFullScreen
                      />
                    </div>
                  </div>
                )}

                {n.insight && (n.insight.watchIfYouLiked ||
                  (n.insight.compTitles || []).length > 0 ||
                  (n.insight.franchises || []).length > 0) ? (
                  <div className="modal-insight">
                    {(n.insight.watchIfYouLiked || (n.insight.compTitles || []).length > 0) && (
                      <div className="modal-insight-block">
                        <span className="modal-label">Watch if you liked</span>
                        {n.insight.watchIfYouLiked && (
                          <p className="modal-insight-text">{n.insight.watchIfYouLiked}</p>
                        )}
                        {(n.insight.compTitles || []).length > 0 && (
                          <div className="modal-genre-chips">
                            {n.insight.compTitles.map((c, i) => (
                              <button type="button" className="modal-genre-chip modal-chip-link" key={i} onClick={() => searchComp(c)}>
                                {c}
                              </button>
                            ))}
                          </div>
                        )}
                      </div>
                    )}

                    {(n.insight.franchises || []).length > 0 && (
                      <div className="modal-insight-block">
                        <span className="modal-label">Franchise</span>
                        {activeRail ? (
                          <>
                            {/* Several franchises? Chips toggle which rail is shown. */}
                            {franchiseRail.franchises.length > 1 && (
                              <div className="modal-genre-chips">
                                {franchiseRail.franchises.map((f) => (
                                  <button
                                    type="button"
                                    key={f.value}
                                    className={`modal-genre-chip modal-chip-link${f.value === activeRail.value ? " modal-chip-active" : ""}`}
                                    onClick={() => setActiveFranchise(f.value)}
                                  >
                                    {prettifyTag(f.value)}
                                  </button>
                                ))}
                              </div>
                            )}
                            {/* The rail: members in release order, current one highlighted. */}
                            <div className="modal-franchise-rail">
                              {activeRail.items.map((it) => (
                                <button
                                  type="button"
                                  key={`${it.kind}-${it.id}`}
                                  className={`modal-franchise-item${it.isCurrent ? " is-current" : ""}${it.streamable ? "" : " is-unstreamable"}`}
                                  onClick={() => { if (!it.isCurrent && onOpenTitle) onOpenTitle(it.id, it.kind); }}
                                  disabled={it.isCurrent}
                                  title={it.year ? `${it.title} (${it.year})` : it.title}
                                >
                                  <img
                                    className="modal-franchise-poster"
                                    alt={`${it.title} poster`}
                                    loading="lazy"
                                    src={MovieAPI.getMoviePoster(it.id, it.posterVersion, it.kind)}
                                  />
                                  {it.isCurrent && <span className="modal-franchise-here">You’re here</span>}
                                  <span className="modal-franchise-year">{it.year || ""}</span>
                                </button>
                              ))}
                            </div>
                            <button
                              type="button"
                              className="modal-franchise-browseall person-link"
                              onClick={() => searchFranchise(activeRail.value)}
                            >
                              Browse all {prettifyTag(activeRail.value)}
                            </button>
                          </>
                        ) : (
                          // No rail (e.g. only one member on disk) — keep the clickable chips.
                          <div className="modal-genre-chips">
                            {n.insight.franchises.map((f, i) => (
                              <button type="button" className="modal-genre-chip modal-chip-link" key={i} onClick={() => searchFranchise(f)}>
                                {prettifyTag(f)}
                              </button>
                            ))}
                          </div>
                        )}
                      </div>
                    )}
                  </div>
                ) : null}

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

                {/* "Why it's interesting" can give away plot, so it lives below the full synopsis and
                    is hidden behind a hover/focus-to-reveal spoiler. */}
                {n.insight && n.insight.whyInteresting && (
                  <div className="modal-insight-block">
                    <span className="modal-label">Why it's interesting</span>
                    <span
                      className="modal-spoiler"
                      tabIndex={0}
                      role="button"
                      aria-label="Reveal spoiler: why it's interesting"
                    >
                      <span className="modal-spoiler-content modal-insight-text">{n.insight.whyInteresting}</span>
                      <span className="modal-spoiler-hint" aria-hidden="true">Spoiler — hover to reveal</span>
                    </span>
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

                {showFiles && (
                  <div className="modal-files">
                    <span className="modal-label">Files ({files.length})</span>
                    {files.map((f, i) => (
                      <div className="modal-file-row" key={i}>
                        <span className={"modal-file-role modal-file-role--" + (f.role || "").toLowerCase()}>
                          {(ROLE_LABEL[f.role] || f.role)}
                          {f.partNumber ? " " + f.partNumber : ""}
                        </span>
                        {f.label ? <span className="modal-file-label">{f.label}</span> : null}
                        <span className="modal-file-name" title={f.path}>{basename(f.path)}</span>
                        {f.isPlayable && canStream && (
                          <button className="modal-play-btn" title="Watch this file" onClick={() => goWatch(`?mediaFileId=${f.mediaFileId}`)}>
                            ▶
                          </button>
                        )}
                      </div>
                    ))}
                  </div>
                )}

                <div className="modal-ratings-row">
                  {displayImdbRating != null && displayImdbRating !== "" && (
                    <a className="modal-rating-link" target="_blank" rel="noreferrer" href={"http://www.imdb.com/title/" + movie.imdbID}>
                      <span className="modal-label">IMDb</span>
                      <span className="modal-rating-score">
                        {displayImdbRating}
                        <span className="modal-rating-denom"> / 10</span>
                      </span>
                    </a>
                  )}
                  {movie.rtTomatometer != null && (
                    <a
                      className="modal-rating-link"
                      target="_blank"
                      rel="noreferrer"
                      href={movie.rtUrl || "https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)}
                    >
                      <span className="rt-icon" aria-hidden="true">🍅</span>
                      <span className="modal-label">Tomatometer</span>
                      <span className={"modal-rating-score " + (movie.rtTomatometer >= 60 ? "rt-fresh" : "rt-rotten")}>
                        {movie.rtTomatometer}
                        <span className="modal-rating-denom">%</span>
                      </span>
                    </a>
                  )}
                  {movie.rtPopcornmeter != null && (
                    <a
                      className="modal-rating-link"
                      target="_blank"
                      rel="noreferrer"
                      href={movie.rtUrl || "https://www.rottentomatoes.com/search?search=" + encodeURIComponent(movie.title)}
                    >
                      <span className="rt-icon" aria-hidden="true">🍿</span>
                      <span className="modal-label">Popcornmeter</span>
                      <span className={"modal-rating-score " + (movie.rtPopcornmeter >= 60 ? "rt-fresh" : "rt-rotten")}>
                        {movie.rtPopcornmeter}
                        <span className="modal-rating-denom">%</span>
                      </span>
                    </a>
                  )}
                </div>

                {userData && kind !== "misc" && <YourRating id={movie.id} kind={kind} userData={userData} setUserData={setUserData} />}

                <div className="modal-actions-row">
                  {!isSeries && (
                    <div className="modal-watch-row">
                      <WatchButton movie={movie} userData={userData} onBeforeNavigate={onClose} />
                    </div>
                  )}
                  {userData && (
                    <UserMovieOptions
                      id={movie.id}
                      kind={kind}
                      isWatched={userData.moviesSeen.includes(movie.id)}
                      isWanted={userData.moviesToWatch.includes(movie.id)}
                      onToggleSeen={toggleSeen}
                      onToggleWant={toggleWant}
                    />
                  )}
                </div>

                {/* Playlist / watch-party actions. For a movie: its own playable. For a series: all
                    streamable episodes (per-season adds live in the episode list below). */}
                {canStream && !isSeries && movie.playableId != null && (
                  <div className="modal-playlist-row">
                    <button className="modal-plbtn" onClick={() => addToPlaylist(movieItem(), movie.title)}>＋ Add to playlist</button>
                    <button className="modal-plbtn modal-plbtn--party" onClick={() => startWatchTogether(movieItem(), movie.title)}>👥 Watch together</button>
                  </div>
                )}
                {canStream && isSeries && allSeriesItems().length > 0 && (
                  <div className="modal-playlist-row">
                    <button className="modal-plbtn" onClick={() => addToPlaylist(allSeriesItems(), movie.title)}>＋ Add all episodes to playlist</button>
                    <button className="modal-plbtn modal-plbtn--party" onClick={() => startWatchTogether(allSeriesItems(), movie.title)}>👥 Watch together</button>
                  </div>
                )}

                {seasons.length > 0 && (
                  <div className="modal-episodes">
                    <span className="modal-label">
                      Episodes — {totalEps} across {seasons.length} season{seasons.length > 1 ? "s" : ""}
                      {epsWithFile > 0 ? ` · ${epsWithFile} with a file` : ""}
                    </span>
                    {seasons.map((s) => (
                      <div className="modal-season" key={s.season}>
                        <button className="modal-season-hd" onClick={() => toggleSeason(s.season)}>
                          <span className="modal-season-caret">{openSeasons[s.season] ? "▾" : "▸"}</span>
                          {s.season === 0 ? "Specials" : "Season " + s.season}
                          <span className="modal-season-count">
                            {s.episodes.filter((e) => e.hasFile).length}/{s.episodes.length}
                          </span>
                          {canStream && seasonItems(s).length > 0 && (
                            <span
                              className="modal-season-add"
                              role="button"
                              tabIndex={0}
                              title="Add this season to a playlist"
                              onClick={(ev) => { ev.stopPropagation(); addToPlaylist(seasonItems(s), `${movie.title} — ${s.season === 0 ? "Specials" : "Season " + s.season}`); }}
                              onKeyDown={(ev) => { if (ev.key === "Enter" || ev.key === " ") { ev.stopPropagation(); ev.preventDefault(); addToPlaylist(seasonItems(s), `${movie.title} — Season ${s.season}`); } }}
                            >＋ playlist</span>
                          )}
                        </button>
                        {openSeasons[s.season] && (
                          <div className="modal-season-eps">
                            {s.episodes.map((e) => {
                              const playable = e.isPlayable && canStream;
                              const epFiles = Array.isArray(e.files) ? e.files : [];
                              const multi = epFiles.length > 1; // segment parts / variants / extras under one episode
                              const epOpen = !!openEps[`${s.season}-${e.episode}`];
                              return (
                                <div className="modal-ep-wrap" key={e.episode}>
                                  <div
                                    className={"modal-ep" + (e.hasFile ? " modal-ep--hasfile" : "") + (playable ? " modal-ep--play" : "")}
                                    onClick={playable ? () => goWatch(`?kind=series&playableId=${e.playableId}`) : undefined}
                                    role={playable ? "button" : undefined}
                                    tabIndex={playable ? 0 : undefined}
                                    onKeyDown={playable ? (ev) => { if (ev.key === "Enter" || ev.key === " ") { ev.preventDefault(); goWatch(`?kind=series&playableId=${e.playableId}`); } } : undefined}
                                    title={playable ? "Play episode" : e.hasFile ? "File found — not yet streamable (run a Jellyfin sync)" : undefined}
                                  >
                                    <span className="modal-ep-num">E{e.episode}</span>
                                    <span className="modal-ep-title">{e.title || "—"}</span>
                                    {multi && (
                                      <button
                                        className="modal-ep-files-badge"
                                        onClick={(ev) => { ev.stopPropagation(); toggleEp(s.season, e.episode); }}
                                        title="This episode has multiple files (parts / variants / extras)"
                                      >
                                        {epOpen ? "▾" : "▸"} {epFiles.length} files
                                      </button>
                                    )}
                                    <span className="modal-ep-mark" aria-hidden="true">{playable ? "▶" : e.hasFile ? "●" : ""}</span>
                                  </div>
                                  {multi && epOpen && (
                                    <div className="modal-ep-files">
                                      {epFiles.map((f) => (
                                        <div className={"modal-file-row modal-ep-file" + (f.role && f.role !== "Primary" ? " modal-ep-file--extra" : "")} key={f.mediaFileId}>
                                          <span className={"modal-file-role modal-file-role--" + (f.role || "").toLowerCase()}>{epRoleLabel(f)}</span>
                                          {f.label ? <span className="modal-file-label">{f.label}</span> : null}
                                          <span className="modal-file-name" title={f.name}>{f.name}</span>
                                          {f.isPlayable && canStream && (
                                            <button className="modal-play-btn" title="Watch this file" onClick={() => goWatch(`?mediaFileId=${f.mediaFileId}`)}>▶</button>
                                          )}
                                        </div>
                                      ))}
                                    </div>
                                  )}
                                </div>
                              );
                            })}
                          </div>
                        )}
                      </div>
                    ))}
                  </div>
                )}

                {(seriesExtras.length > 0 || relatedMisc.length > 0) && (
                  <div className="modal-extras">
                    <span className="modal-label">Extras &amp; Specials</span>
                    {/* Series/season-level extra files (making-ofs, specials not tied to one episode). */}
                    {seriesExtras.map((f) => (
                      <div className="modal-file-row" key={`x${f.mediaFileId}`}>
                        <span className="modal-file-role modal-file-role--extra">{ROLE_LABEL[f.role] || f.role}</span>
                        {f.label ? <span className="modal-file-label">{f.label}</span> : null}
                        <span className="modal-file-name" title={f.name}>{f.name}</span>
                        {f.isPlayable && canStream && (
                          <button className="modal-play-btn" title="Watch this file" onClick={() => goWatch(`?mediaFileId=${f.mediaFileId}`)}>▶</button>
                        )}
                      </div>
                    ))}
                    {/* Related misc videos: workprints, featurettes, shorts attached to this title. */}
                    {relatedMisc.map((m, i) => {
                      const mf = (m.files || []).find((x) => x.isPlayable) || (m.files || [])[0];
                      return (
                        <div className="modal-file-row" key={`m${i}`}>
                          <span className="modal-file-role modal-file-role--extra">{m.category || "misc"}</span>
                          <span className="modal-file-name" title={m.title}>
                            {m.title}{m.year ? ` (${m.year})` : ""}{m.collectionName ? ` — ${m.collectionName}` : ""}
                          </span>
                          {mf && mf.isPlayable && canStream && (
                            <button className="modal-play-btn" title="Watch this extra" onClick={() => goWatch(`?mediaFileId=${mf.mediaFileId}`)}>▶</button>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}

                {userData?.canEditMovies && (
                  <div className="modal-edit-row">
                    <Button type="default" onClick={startEditing}>
                      <svg width="1em" height="1em" viewBox="0 0 512 512" fill="currentColor" aria-hidden="true" style={{ marginRight: 6 }}>
                        <path d="M362.7 19.3L314.3 67.7 444.3 197.7l48.4-48.4c25-25 25-65.5 0-90.5L453.3 19.3c-25-25-65.5-25-90.5 0zm-71 71L58.6 323.5c-10.4 10.4-18 23.3-22.2 37.4L1 481.2C-1.5 489.7 .8 498.8 7 505s15.3 8.5 23.7 6.1l120.3-35.4c14.1-4.2 27-11.8 37.4-22.2L421.7 220.3 291.7 90.3z" />
                      </svg>
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

                <FileMappingEditor id={movie.id} kind={isSeries ? "series" : "movie"} />

                {!isSeries && <SubtitlePicker movieId={movie.id} />}

                <div className="modal-edit-actions">
                  <Button type="primary" onClick={saveChanges} loading={saving}>
                    Save
                  </Button>
                  <Button className="btn-cancel" onClick={cancelEditing}>
                    Cancel
                  </Button>
                  <Button onClick={refetchFromImdb} loading={saving} title="Re-pull rating, year, plot & poster from IMDb for the current id">
                    ↻ Re-fetch from IMDb
                  </Button>
                  {!isSeries && (
                    <Button onClick={relinkFromDisk} loading={saving} title="Replaced this movie's file on disk (new rip / renamed folder)? Re-scan just this folder and re-point to the new file + extras — keeps all ratings, viewings, poster & tags.">
                      ⟳ Re-link file from disk
                    </Button>
                  )}
                  {thumbMissing && (
                    <Button onClick={generateThumbnail} loading={genThumb} title="This title has a poster but no thumbnail (its card shows a placeholder); generate the thumbnail from the existing poster">
                      Generate thumbnail
                    </Button>
                  )}
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
