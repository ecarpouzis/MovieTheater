/** A horizontal strip of small covers ("More in <series>", "More by <writer>") — the standalone's CMRelated. */
import { hueSvg } from "../../catalog/cards/CardImage";
import { hueOf } from "../../catalog/sources/hue";
import type { ItemSummary } from "./booksApi";
import { clampAspect, dateLabel } from "./booksFormat";
import { thumbUrl } from "./booksMedia";

export function CoverThumb({ item, className }: { item: ItemSummary; className?: string }) {
  const hue = hueOf(item.series ?? item.title ?? String(item.id));
  const src = thumbUrl(item.id) ?? hueSvg(hue, 84, 120);
  return <img src={src} alt="" loading="lazy" className={className} style={{ position: "absolute", inset: 0, width: "100%", height: "100%", objectFit: "cover" }} />;
}

export default function RelatedStrip({ title, items, onOpen }: { title: string; items: ItemSummary[]; onOpen: (item: ItemSummary) => void }) {
  if (items.length === 0) return null;
  return (
    <section className="cm-relsec">
      <h3 className="cm-h3">{title}</h3>
      <div className="cm-rel">
        {items.map((r) => (
          <button key={r.id} type="button" className="cm-rel-card" style={{ "--aspect": clampAspect(r.coverAspect) } as React.CSSProperties} onClick={() => onOpen(r)} title={r.title ?? undefined}>
            <div className="cm-rel-cover"><CoverThumb item={r} /></div>
            <div className="cm-rel-title">{r.title}</div>
            <div className="cm-rel-date">{dateLabel(r.year, r.month, r.datePrecision)}</div>
          </button>
        ))}
      </div>
    </section>
  );
}
