/** The read-only five stars (with halves) and the ten-star "your rating" control, as the standalone drew them. */

export function StarRating({ value }: { value: number }) {
  const stars = [];
  for (let i = 1; i <= 5; i += 1) {
    const diff = value - (i - 1);
    stars.push(<span key={i} className={diff >= 1 ? "star star-full" : diff >= 0.5 ? "star star-half" : "star"}>★</span>);
  }
  return (
    <span className="stars">
      {stars}
      <span className="stars-num">{value.toFixed(1)}</span>
    </span>
  );
}

/** 0–100 stored; shown as 10 stars. Clicking the current star clears. */
export function RateTen({ value, onChange, disabled }: { value: number | null; onChange: (next: number | null) => void; disabled?: boolean }) {
  const stars = value != null ? Math.round(value / 10) : 0;
  return (
    <div className="cm-rate">
      {[1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map((star) => (
        <button
          key={star}
          type="button"
          className={`cm-rate-star${star <= stars ? " on" : ""}`}
          onClick={() => onChange(star === stars ? null : star * 10)}
          aria-label={`${star} of 10`}
          disabled={disabled}
        >★</button>
      ))}
      <span className="cm-rate-num">{stars > 0 ? `${stars}/10` : "Not rated"}</span>
    </div>
  );
}
