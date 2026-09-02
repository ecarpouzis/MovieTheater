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
 *
 * The root wears `bx-card--pkg` as well as `bx-card`: it is what tells the Grid's stylesheet that
 * THIS is the package's own tile and its cover/meta may be sized off `--cell × --aspect`. A SECTION
 * card (`CatalogSource.renderCard`) wears only `bx-card` — it brings its own geometry, and the
 * package must never impose a 0.66 poster box on a square album cover (R9 S3 parity fix).
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

/**
 * The widest cover a section's THUMB rendition still looks sharp at. Past it (a Wall scaled up, a big
 * Grid tile) the full image is asked for instead — a poster thumbnail stretched to 300px is the one
 * thing a cover wall must never show.
 */
export const THUMB_MAX_PX = 220;

export function coverSrc(item: CardItem, widthPx: number): string {
  return item.imageThumbUrl && widthPx <= THUMB_MAX_PX ? item.imageThumbUrl : item.imageUrl;
}

/** The meta strip's floor: a caption narrower than this is unreadable, so a narrow cover's card is this wide. */
export const CARD_META_MIN_W = 100;

/**
 * The card's laid-out width for a cell height — the cover's width, or the meta strip's floor when
 * the cover is narrower and the strip is shown. ONE geometry: the card sets these boxes inline from
 * the same numbers, and a strip's spacers reserve them before the card mounts
 * (`engine/horizontalWindow.ts`), so a windowed run never changes width.
 */
export function cardWidth(item: CardItem, cellH: number, opts: { uniformAspect?: number; metadata: MetadataMode; hoverMeta?: boolean }): number {
  const aspect = opts.uniformAspect ?? (item.aspect || 0.66);
  const w = Math.round(cellH * aspect);
  const showStrip = !opts.hoverMeta && opts.metadata !== "minimal";
  return showStrip ? Math.max(w, CARD_META_MIN_W) : w;
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
      className={`bx-card bx-card--pkg${hoverClass ? ` ${hoverClass}` : ""}`}
      style={{ "--aspect": aspect } as CSSProperties}
      data-kind={item.kind}
      onClick={() => onOpen(item)}
      role="button"
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(item); } }}
      aria-label={item.title}
    >
      <div className="bx-cover" style={{ height: cellH, width: w }}>
        <CardImage src={coverSrc(item, w)} alt="" hue={item.hue} eager={eager} />
        {item.count != null && item.count > 1 && <span className="bx-count">{item.count}</span>}
        {hoverMeta && <div className="bx-hover-meta">{caption}</div>}
      </div>
      {showStrip && <div className="bx-meta" style={{ width: w, minWidth: CARD_META_MIN_W }}>{caption}</div>}
    </div>
  );
}

const Card = memo(CardInner);
export default Card;
