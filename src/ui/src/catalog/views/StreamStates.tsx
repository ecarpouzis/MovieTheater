import LoadFailure from "../../Components/LoadFailure";
import type { CatalogSource } from "../types";

/** The three non-content states every view shows the same way. */
export function StreamLoading() {
  return (
    <div className="bx-empty" role="status" aria-live="polite">
      <div className="bx-spinner" aria-hidden="true" />
      <div>Loading…</div>
    </div>
  );
}

/**
 * The line an empty result shows. A section that says it in its own words wins ("No games here
 * yet." vs "No games match those filters."); everything else falls back to the noun sentence.
 */
export function emptyLine(source: Pick<CatalogSource, "emptyLabel" | "filtered" | "itemNoun">): string {
  const label = source.emptyLabel;
  if (label) return source.filtered ? label.filtered : label.empty;
  return `No ${source.itemNoun ?? "item"}s match.`;
}

export function StreamEmpty({ noun = "item", source }: { noun?: string; source?: Pick<CatalogSource, "emptyLabel" | "filtered" | "itemNoun"> }) {
  return (
    <div className="bx-empty" role="status">
      <div className="bx-empty-mark" aria-hidden="true">∅</div>
      <div>{source ? emptyLine(source) : `No ${noun}s match.`}</div>
    </div>
  );
}

export function StreamFailed({ onRetry }: { onRetry: () => void }) {
  return <LoadFailure message="Couldn't load this list." onRetry={onRetry} />;
}
