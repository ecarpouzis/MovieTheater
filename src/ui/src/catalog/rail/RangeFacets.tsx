/** The small facets: the year range (two sliders + decade pills), the rating floor (presets), the personal flags. */
import type { FacetFlagDef, FacetOptionRow } from "./facetSpec";

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
