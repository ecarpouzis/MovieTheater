/** The shelf's compact, always-interactive 10-star control (0–100 stored, shown as 10). Clicking the current star clears. */
export default function ShelfStars({ value, onSet, className, disabled }: { value: number | null; onSet: (rating: number | null) => void; className?: string; disabled?: boolean }) {
  const stars = value != null ? Math.round(value / 10) : 0;
  return (
    <div className={`bs-stars${className ? ` ${className}` : ""}`} role="group" aria-label={stars > 0 ? `Your rating: ${stars} of 10` : "Not rated"}>
      {[1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map((star) => (
        <button
          key={star}
          type="button"
          className={`bs-star${star <= stars ? " on" : ""}`}
          aria-label={`${star} of 10`}
          disabled={disabled}
          onClick={(e) => { e.stopPropagation(); onSet(star === stars ? null : star * 10); }}
        >★</button>
      ))}
    </div>
  );
}
