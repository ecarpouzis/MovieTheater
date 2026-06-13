import { useHistory } from "react-router-dom";
import "./WatchButton.css";

/**
 * The ticket stub that opens the screening room. Rendered only when the movie
 * has a mapped file; passwordless sessions get the hint instead of the button
 * (UI courtesy — the server enforces the real policy either way).
 */
function WatchButton({ movie, userData, onBeforeNavigate }) {
  const history = useHistory();

  if (!movie?.hasFile) return null;

  if (!userData?.hasPassword) {
    return (
      <div className="watch-hint" title="Streaming is for password-protected accounts">
        <span className="watch-hint-glyph">▸</span>
        Set a password (user menu) to stream this movie
      </div>
    );
  }

  return (
    <button
      type="button"
      className="watch-stub"
      onClick={() => {
        onBeforeNavigate?.();
        history.push(`/watch/${movie.id}`);
      }}
    >
      <span className="watch-stub-tri" aria-hidden="true" />
      <span className="watch-stub-text">Watch</span>
      <span className="watch-stub-perf" aria-hidden="true" />
      <span className="watch-stub-tail" aria-hidden="true">ADMIT ONE</span>
    </button>
  );
}

export default WatchButton;
