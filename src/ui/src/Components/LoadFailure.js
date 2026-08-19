import "./LoadFailure.css";

// The shared "the fetch failed" surface — a failed request must never render as an empty library
// (the grid blaming the user's filters for its own broken fetch) or as a skeleton that sits there
// forever. One message + one Try again, token-colored so it reads on every section and both
// themes. Sections whose failure copy is genuinely their own (the TV room's status→prose "No
// signal" card, the photos denied-plate) keep their custom surfaces and don't use this.
export default function LoadFailure({ message = "Couldn't load this page.", onRetry, retryLabel = "Try again" }) {
  return (
    <div className="load-failure" role="alert">
      <span className="load-failure-message">{message}</span>
      {onRetry && (
        <button type="button" className="load-failure-retry" onClick={onRetry}>
          {retryLabel}
        </button>
      )}
    </div>
  );
}
