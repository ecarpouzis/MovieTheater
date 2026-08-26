import { memo, type CSSProperties } from "react";
import { CardBadges } from "../CardBadge";
import type { CardItem } from "../types";
import type { MetadataMode } from "../tweaks/useTweaks";
import CardImage from "./CardImage";

/**
 * The one card every dense view renders (Grid tiles, Wall covers, strips): a cover box sized by
 * the view's cell height and the item's aspect, then the metadata strip — label · subtitle on one
 * line, the title under it, badges when the section supplies them. The Wall asks for `hoverMeta`
 * instead: covers stay silent at rest and a caption fades in on hover.
 *
 * Hover effects are NOT applied here — the results root's `data-hover` and the shared hoverClass
 * decide them in one place (the view-drift rule).
 */
export interface CardProps {
  item: CardItem;
  cellH: number;
  /** Force one aspect for every tile (the Grid) instead of the item's own. */
  uniformAspect?: number;
  metadata: MetadataMode;
  /** Wall mode: the caption is an overlay revealed on hover, not a strip below. */
  hoverMeta?: boolean;
  hoverClass: string;
  eager?: boolean;
  onOpen: (item: CardItem) => void;
}

function CardInner({ item, cellH, uniformAspect, metadata, hoverMeta, hoverClass, eager, onOpen }: CardProps) {
  const aspect = uniformAspect ?? (item.aspect || 0.66);
  const w = Math.round(cellH * aspect);
  const showStrip = !hoverMeta && metadata !== "minimal";
  const caption = (
    <>
      <div className="bx-meta-row">
        {item.label != null && <span className="bx-meta-a">{item.label}</span>}
        {item.subtitle != null && <span className="bx-meta-b">{item.subtitle}</span>}
      </div>
      <div className="bx-meta-title">{item.title}</div>
      {item.badges && item.badges.length > 0 && <div className="bx-meta-badges"><CardBadges badges={item.badges} /></div>}
    </>
  );
  return (
    <div
      className={`bx-card${hoverClass ? ` ${hoverClass}` : ""}`}
      style={{ "--aspect": aspect } as CSSProperties}
      data-kind={item.kind}
      onClick={() => onOpen(item)}
      role="button"
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(item); } }}
      aria-label={item.title}
    >
      <div className="bx-cover" style={{ height: cellH, width: w }}>
        <CardImage src={item.imageThumbUrl ?? item.imageUrl} alt="" hue={item.hue} eager={eager} />
        {item.count != null && item.count > 1 && <span className="bx-count">{item.count}</span>}
        {hoverMeta && <div className="bx-hover-meta">{caption}</div>}
      </div>
      {showStrip && <div className="bx-meta" style={{ width: w, minWidth: 100 }}>{caption}</div>}
    </div>
  );
}

const Card = memo(CardInner);
export default Card;
