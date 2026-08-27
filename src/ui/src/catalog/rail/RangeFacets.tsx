/** The small facets: the year range (two sliders + decade pills), the fixed-scale range (two thumbs over stops), the rating floor (presets), the personal flags. */
import type { FacetFlagDef, FacetOptionRow, FacetRange, RangeFacetDef } from "./facetSpec";

/** The stop index a value sits at (nearest), or the open end when the side is unset. */
export function stopIndexOf(def: RangeFacetDef, value: number | null | undefined, side: "min" | "max"): number {
  const last = def.stops.length - 1;
  if (value == null) return side === "min" ? 0 : last;
  let best = 0;
  let bestDist = Infinity;
  def.stops.forEach((s, i) => { const d = Math.abs(s - value); if (d < bestDist) { bestDist = d; best = i; } });
  return best;
}

/** The value a thumb position means: the first stop is an open bottom, the last an open top. */
export function stopValueOf(def: RangeFacetDef, index: number, side: "min" | "max"): number | null {
  const last = def.stops.length - 1;
  if (side === "min") return index <= 0 ? null : def.stops[Math.min(index, last)];
  return index >= last ? null : def.stops[Math.max(index, 0)];
}

/**
 * Two thumbs over a fixed scale (the Boardgames age slider): each thumb walks the stops, the
 * read-outs name the stops under them, a thumb parked at either end opens that side. The lower
 * thumb is a real filter — sliding it to 12 hides everything rated for younger players.
 */
export function StopsRangeFacet({ def, range, onChange }: { def: RangeFacetDef; range: FacetRange | undefined; onChange: (min: number | null, max: number | null) => void }) {
  const last = def.stops.length - 1;
  const f = def.format ?? ((v: number) => String(v));
  const lo = stopIndexOf(def, range?.min, "min");
  const hi = stopIndexOf(def, range?.max, "max");
  const tick = (i: number) => `${f(def.stops[i])}${i === last && def.openTop ? "+" : ""}`;
  const commit = (a: number, b: number) => onChange(stopValueOf(def, a, "min"), stopValueOf(def, b, "max"));
  return (
    <div className="bx-date bx-stops">
      <div className="bx-date-vals"><span>{tick(lo)}</span><span>{tick(hi)}</span></div>
      <div className="bx-date-sliders">
        <input type="range" min={0} max={last} step={1} value={lo} aria-label={`From ${def.label.toLowerCase()}`} aria-valuetext={tick(lo)} onChange={(e) => commit(Math.min(+e.target.value, hi), hi)} />
        <input type="range" min={0} max={last} step={1} value={hi} aria-label={`To ${def.label.toLowerCase()}`} aria-valuetext={tick(hi)} onChange={(e) => commit(lo, Math.max(+e.target.value, lo))} />
      </div>
      {def.stops.length <= 12 && (
        <div className="bx-stops-ticks" aria-hidden="true">
          {def.stops.map((_, i) => <span key={i} className={i >= lo && i <= hi ? "in" : undefined}>{tick(i)}</span>)}
        </div>
      )}
    </div>
  );
}

export function DateFacet({ yearMin, yearMax, decades, onChange, showDecades = true }: {
  yearMin: number | null; yearMax: number | null; decades: FacetOptionRow[]; onChange: (min: number | null, max: number | null) => void;
  /** The decade shortcut row under the sliders (Books); Movies runs the range alone (Eric, canvas). */
  showDecades?: boolean;
}) {
  const years = decades.flatMap((d) => { const y = parseInt(String(d.value), 10); return Number.isNaN(y) ? [] : [y, y + 9]; });
  const lo = years.length ? Math.min(...years) : 1940;
  const hi = years.length ? Math.max(...years) : new Date().getFullYear();
  const mn = yearMin ?? lo;
  const mx = yearMax ?? hi;
  return (
    <div className="bx-date">
      <div className="bx-date-vals"><span>{mn}</span><span>{mx}</span></div>
      <div className="bx-date-sliders">
        <input type="range" min={lo} max={hi} value={mn} aria-label="From year" onChange={(e) => onChange(Math.min(+e.target.value, mx), mx)} />
        <input type="range" min={lo} max={hi} value={mx} aria-label="To year" onChange={(e) => onChange(mn, Math.max(+e.target.value, mn))} />
      </div>
      {showDecades && <div className="bx-date-decades">
        {decades.map((d) => {
          const dy = parseInt(String(d.value), 10);
          if (Number.isNaN(dy)) return null;
          const on = yearMin === dy && yearMax === dy + 9;
          return (
            <button key={String(d.value)} type="button" className={`bx-mini${on ? " on" : ""}`} aria-pressed={on} onClick={() => (on ? onChange(null, null) : onChange(dy, dy + 9))}>
              {d.label}
            </button>
          );
        })}
      </div>}
    </div>
  );
}

export function RatingFacet({ value, presets, onChange }: { value: number; presets: { value: number; label: string }[]; onChange: (min: number) => void }) {
  return (
    <div className="bx-rating">
      {presets.map((p) => (
        <button key={p.value} type="button" className={`bx-mini${value === p.value ? " on" : ""}`} aria-pressed={value === p.value} onClick={() => onChange(value === p.value ? 0 : p.value)}>
          {p.label}
        </button>
      ))}
    </div>
  );
}

export function FlagFacet({ flags, state, onChange }: { flags: FacetFlagDef[]; state: Record<string, boolean>; onChange: (key: string, on: boolean) => void }) {
  return (
    <div className="bx-rating">
      {flags.map((f) => (
        <button key={f.key} type="button" className={`bx-mini${state[f.key] ? " on" : ""}`} aria-pressed={!!state[f.key]} title={f.title} onClick={() => onChange(f.key, !state[f.key])}>
          {f.label}
        </button>
      ))}
    </div>
  );
}
