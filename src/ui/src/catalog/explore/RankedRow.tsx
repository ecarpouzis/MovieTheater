import CardImage from "../cards/CardImage";
import type { CardItem } from "../types";
import ScoreBadge from "./ScoreBadge";

/**
 * A numbered top ten. The numerals are real ranks (the order IS the content), set large in the
 * section's display face as an outline the cover overlaps. Five across, two down on a wide screen;
 * one swipeable row on a phone.
 */
export default function RankedRow({ items, onOpen }: { items: CardItem[]; onOpen: (item: CardItem) => void }) {
  return (
    <ol className="xp-ranked">
      {items.slice(0, 10).map((it, i) => (
        <li key={it.key} className="xp-ranked-item">
          <button type="button" className="xp-ranked-btn" onClick={() => onOpen(it)} aria-label={`${i + 1}. ${it.title}`} title={it.title}>
            <span className="xp-ranked-art">
              <span className="xp-ranked-num" aria-hidden="true" data-wide={i >= 9 || undefined}>{i + 1}</span>
              <span className="xp-ranked-cover" style={{ aspectRatio: `${it.aspect || 0.66}` }}>
                <CardImage src={it.imageThumbUrl ?? it.imageUrl} hue={it.hue} />
              </span>
            </span>
            <span className="xp-ranked-text">
              <span className="xp-ranked-title">{it.title}</span>
              <span className="xp-ranked-sub">
                <ScoreBadge score={it.rating} />
                {it.label && <span>{it.label}</span>}
              </span>
            </span>
          </button>
        </li>
      ))}
    </ol>
  );
}
