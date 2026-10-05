import CardImage from "../cards/CardImage";
import type { CardItem } from "../types";
import { isNarrow, useColumns } from "./useColumns";

/**
 * A mosaic of covers (what just arrived): uniform cells, no captions, CLAMPED to whole rows — a wall
 * that ends on a ragged last row reads as broken, not as "there is more". The column count is
 * measured, and only `columns × rows` cells are drawn.
 */
export default function CoverWall({ items, onOpen, rows = 2 }: { items: CardItem[]; onOpen: (item: CardItem) => void; rows?: number }) {
  const narrow = isNarrow();
  const gap = narrow ? 8 : 10;
  const [ref, cols] = useColumns<HTMLDivElement>(narrow ? 78 : 104, gap);
  const shown = items.slice(0, cols * (narrow ? rows + 1 : rows));
  // One cell shape per wall: the items' own when they agree (posters, square sleeves), a square
  // crop when they do not (photos mix portrait and landscape, and a row of both is ragged).
  const aspects = new Set(shown.map((it) => (it.aspect || 0.66).toFixed(2)));
  const cell = aspects.size <= 1 ? (shown[0]?.aspect || 0.66) : 1;
  return (
    <div ref={ref} className="xp-wall" style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, gap }}>
      {shown.map((it) => (
        <button key={it.key} type="button" className="xp-wall-cell" style={{ aspectRatio: `${cell}` }} onClick={() => onOpen(it)} title={it.label ? `${it.title} (${it.label})` : it.title} aria-label={it.title}>
          <CardImage src={it.imageThumbUrl ?? it.imageUrl} hue={it.hue} />
        </button>
      ))}
    </div>
  );
}
