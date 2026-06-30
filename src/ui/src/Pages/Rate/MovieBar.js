import { useState } from "react";
import { MovieAPI } from "../../MovieAPI";

// Poster thumb with the same graceful 404 fallback the browse cards use (Misc/posterless titles 404).
function BarPoster({ card }) {
  const [failed, setFailed] = useState(false);
  if (failed) {
    return (
      <div className="rate-bar-poster rate-bar-poster--placeholder" aria-hidden="true">
        🎞
      </div>
    );
  }
  return (
    <img
      className="rate-bar-poster"
      alt=""
      loading="lazy"
      decoding="async"
      src={MovieAPI.getPosterThumbnail(card.id, card.posterVersion, card.kind)}
      onError={() => setFailed(true)}
    />
  );
}

// One ranked title. The horizontal fill reflects its current extrapolated 0–100 score; the row is dragged
// (by the handle) to change its rank. ✕ removes it from the ranking (clears the rating).
export default function MovieBar({ item, score, dragHandle, onUnrank }) {
  const card = item.card;
  const title = card.title || card.simpleTitle || "Untitled";
  const year = card.releaseDate ? new Date(card.releaseDate).getFullYear() : null;
  const pct = Math.max(0, Math.min(100, score ?? 0));
  return (
    <div className="rate-bar rate-bar--movie">
      <button
        type="button"
        className="rate-drag-handle"
        aria-label="Drag to reorder"
        {...dragHandle.attributes}
        {...dragHandle.listeners}
      >
        ⠿
      </button>
      <div className="rate-bar-fill" style={{ width: `${pct}%` }} aria-hidden="true" />
      <BarPoster card={card} />
      <div className="rate-bar-body">
        <span className="rate-bar-title">{title}</span>
        {year ? <span className="rate-bar-year">{year}</span> : null}
        {card.kind && card.kind !== "movie" ? <span className="rate-bar-kind">{card.kind}</span> : null}
      </div>
      <span className="rate-bar-score">{pct}</span>
      <button type="button" className="rate-bar-remove" title="Remove from ranking" onClick={() => onUnrank(item)}>
        ✕
      </button>
    </div>
  );
}
