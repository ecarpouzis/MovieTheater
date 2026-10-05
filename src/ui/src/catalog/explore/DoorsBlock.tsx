import { useHistory } from "react-router-dom";
import CardImage from "../cards/CardImage";
import type { ExploreDoor, ExploreDoorAxis } from "../types";
import { isNarrow, useColumns } from "./useColumns";

/**
 * The ways into the library. A row of tabs names the axes the section can be cut along (genre, mood,
 * decade, console, players…); under it, one tile per value — three of its covers fanned like a hand
 * of cards, the name, and how many titles stand behind it. A tile is a link to the browse with that
 * facet already applied, so the chip is on the page the moment it opens.
 *
 * The section loads only the ACTIVE axis (`onAxis` tells it which); a tab whose doors have not
 * arrived draws skeleton tiles of the same size, so switching never jumps the page.
 */
const SKELETON = Array.from({ length: 16 }, (_, i) => i);

/** The `take` doors with the most behind them, in their original order. */
export function keepLargest(doors: readonly ExploreDoor[], take: number): ExploreDoor[] {
  const keep = new Set(doors.map((d, i) => ({ i, c: d.count ?? 0 })).sort((a, b) => b.c - a.c || a.i - b.i).slice(0, take).map((x) => x.i));
  return doors.filter((_, i) => keep.has(i));
}

function Door({ door }: { door: ExploreDoor }) {
  const history = useHistory();
  const covers = door.covers.slice(0, 3);
  return (
    <button type="button" className="xp-door" onClick={() => history.push(door.href)} aria-label={`${door.label}${door.count ? `, ${door.count} titles` : ""}`}>
      <span className="xp-door-fan" data-n={covers.length} aria-hidden="true">
        {covers.map((c, i) => (
          <span key={`${c.src}-${i}`} className="xp-door-card"><CardImage src={c.src} hue={c.hue} /></span>
        ))}
      </span>
      <span className="xp-door-text">
        <span className="xp-door-label">{door.label}</span>
        {door.count != null && <span className="xp-door-count">{door.count.toLocaleString()}</span>}
      </span>
    </button>
  );
}

export default function DoorsBlock({ axes, active, onAxis }: { axes: ExploreDoorAxis[]; active?: string; onAxis?: (axis: string) => void }) {
  const cur = axes.find((a) => a.key === active) ?? axes[0];
  // Two full rows of doors at any width (three rows of two on a phone) — measured, never a ragged row.
  const narrow = isNarrow();
  const gap = narrow ? 10 : 14;
  const [ref, measured] = useColumns<HTMLDivElement>(narrow ? 140 : 176, gap);
  const cols = narrow ? 2 : measured;
  const rowsMax = narrow ? 3 : 2;
  const n = cur.doors?.length ?? cols * rowsMax;
  // Whole rows only: past the first row, the doors that would make a ragged last row are left to
  // the browse (one short row is fine — it is the whole axis, not a cut-off).
  const take = n <= cols ? n : Math.min(cols * rowsMax, Math.floor(n / cols) * cols);
  // Trim the SMALLEST doors, keeping the axis's own order — a chronological axis (decades) must not
  // lose its newest end just because it is last. On a biggest-first axis this is a plain slice.
  const shown = cur.doors && take < cur.doors.length ? keepLargest(cur.doors, take) : cur.doors;
  return (
    <div className="xp-doors">
      {axes.length > 1 && (
        <div className="xp-doors-tabs" role="tablist" aria-label="Browse by">
          {axes.map((a) => (
            <button
              key={a.key}
              type="button"
              role="tab"
              aria-selected={a.key === cur.key}
              className={`xp-doors-tab${a.key === cur.key ? " on" : ""}`}
              onClick={() => onAxis?.(a.key)}
            >
              {a.label}
            </button>
          ))}
        </div>
      )}
      <div ref={ref} className="xp-doors-grid" role="tabpanel" aria-busy={cur.doors ? undefined : "true"} style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, gap }}>
        {shown
          ? shown.map((d) => <Door key={d.key} door={d} />)
          : SKELETON.slice(0, Math.min(SKELETON.length, take)).map((i) => <span key={i} className="xp-door xp-door-skel skeleton-block" />)}
      </div>
    </div>
  );
}
