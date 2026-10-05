import type { CardItem } from "../types";
import { ExploreCard } from "./CardRow";
import { isNarrow, useColumns } from "./useColumns";

/**
 * The same card as the strip, in a grid CLAMPED to whole rows: the column count is measured and only
 * `columns × rows` cards are drawn, so the grid never ends on an orphan row of one or two covers
 * whatever the window's width. "See all" is where the rest lives.
 */
export default function CardGrid({ items, onOpen, rows = 2 }: { items: CardItem[]; onOpen: (item: CardItem) => void; rows?: number }) {
  const narrow = isNarrow();
  const gap = narrow ? 12 : 18;
  const [ref, cols] = useColumns<HTMLDivElement>(narrow ? 104 : 146, gap);
  const shown = items.slice(0, cols * (narrow ? rows + 1 : rows));
  return (
    <div ref={ref} className="xp-grid" style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, columnGap: gap }}>
      {shown.map((it) => <ExploreCard key={it.key} item={it} onOpen={onOpen} fluid />)}
    </div>
  );
}
