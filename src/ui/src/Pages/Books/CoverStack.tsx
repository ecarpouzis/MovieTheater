/**
 * The series modal's fanned cover stack you can riffle through — the front card is the first issue in
 * reading order, the two behind it preview what follows; clicking the front advances and wraps.
 * `aspect` is pinned to the first cover so the box never resizes as you flip.
 */
import { useState, type CSSProperties } from "react";
import type { ItemSummary } from "./booksApi";
import { CoverThumb } from "./RelatedStrip";

export default function CoverStack({ items, count, aspect }: { items: ItemSummary[]; count: number; aspect: number }) {
  const [idx, setIdx] = useState(0);
  const n = items.length;
  if (n === 0) return null;
  const cur = idx % n;
  const canFlip = n > 1;
  const layers = Array.from({ length: Math.min(3, n) }, (_, slot) => ({ item: items[(cur + slot) % n], depth: slot }));
  return (
    <div className="cm-stack" style={{ "--aspect": aspect } as CSSProperties}>
      {[...layers].reverse().map(({ item, depth }) =>
        depth === 0 && canFlip ? (
          <button key={depth} type="button" className="cm-stack-card cm-stack-flip" data-d={depth} onClick={() => setIdx((i) => (i + 1) % n)} title={item.title ?? undefined} aria-label={`Show next cover (${cur + 1} of ${n})`}>
            <CoverThumb key={item.id} item={item} />
          </button>
        ) : (
          <div key={depth} className="cm-stack-card" data-d={depth}>
            <CoverThumb key={item.id} item={item} />
          </div>
        ),
      )}
      <span className="cm-stack-count">{count}</span>
    </div>
  );
}
