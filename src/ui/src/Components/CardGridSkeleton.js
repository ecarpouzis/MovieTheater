import useIsMobile from "../hooks/useIsMobile";
import "../Pages/Browse/CardList.css";

// First-paint placeholder for the shared .card-list grids (movies, boardgames): the same grid
// layout as the real cards, so the page shows structure immediately (not a lone spinner) and
// anything above it can keep loading in parallel. Every catalog's first paint uses this shape —
// pass `count` only when a caller has a reason to differ from the default screenful.
export default function CardGridSkeleton({ count }) {
  const isMobile = useIsMobile();
  const n = count ?? (isMobile ? 6 : 12);
  return (
    <div className="card-list" aria-hidden="true">
      {Array.from({ length: n }).map((_, i) => (
        <div className="card-cell" key={i}>
          <div className="movie-card skeleton-card skeleton-block" />
        </div>
      ))}
    </div>
  );
}
