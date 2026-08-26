import CardImage from "../cards/CardImage";
import type { CardItem } from "../types";

/** A mosaic of covers (the "fresh arrivals" wall): uniform cells, a glow ring on hover, no captions. */
export default function CoverWall({ items, onOpen }: { items: CardItem[]; onOpen: (item: CardItem) => void }) {
  return (
    <div className="xp-wall">
      {items.map((it) => (
        <button key={it.key} type="button" className="xp-wall-cell" onClick={() => onOpen(it)} title={it.label ? `${it.title} · ${it.label}` : it.title} aria-label={it.title}>
          <CardImage src={it.imageThumbUrl ?? it.imageUrl} hue={it.hue} />
          <span className="xp-wall-glow" />
        </button>
      ))}
    </div>
  );
}
