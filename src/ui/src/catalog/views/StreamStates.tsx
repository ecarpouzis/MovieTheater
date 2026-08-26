import LoadFailure from "../../Components/LoadFailure";

/** The three non-content states every view shows the same way. */
export function StreamLoading() {
  return (
    <div className="bx-empty" role="status" aria-live="polite">
      <div className="bx-spinner" aria-hidden="true" />
      <div>Loading…</div>
    </div>
  );
}

export function StreamEmpty({ noun = "item" }: { noun?: string }) {
  return (
    <div className="bx-empty" role="status">
      <div className="bx-empty-mark" aria-hidden="true">∅</div>
      <div>No {noun}s match.</div>
    </div>
  );
}

export function StreamFailed({ onRetry }: { onRetry: () => void }) {
  return <LoadFailure message="Couldn't load this list." onRetry={onRetry} />;
}
