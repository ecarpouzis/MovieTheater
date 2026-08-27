/**
 * One facet's option list: a filter box once the list is long, a "+" (include) and "−" (exclude) per
 * row, active rows sorted to the top and always shown (even when they fell below the server's cut),
 * and — for a `dynamic` facet — a debounced server search plus scroll-to-load paging through the
 * spec's `loadOptions`. Publishers draw a swatch, collections a square cover tile with a hue fallback.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { hueOf } from "../sources/hue";
import type { FacetDef, FacetOptionRow, FacetSpec, FacetValue } from "./facetSpec";
import { hasFacetValue } from "./facetSpec";
import type { FacetMode } from "./useFacetState";

const PAGE = 50;
const SEARCH_DEBOUNCE_MS = 300;

export interface FacetOptionsProps {
  def: FacetDef;
  options: FacetOptionRow[];
  selected: FacetValue[];
  excluded: FacetValue[];
  onToggle: (key: string, value: FacetValue, mode: FacetMode) => void;
  loadOptions?: FacetSpec["loadOptions"];
  /** Show the filter box above this many options. */
  max?: number;
}

function TileImage({ src, hue, alt }: { src?: string | null; hue: number; alt: string }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => { setFailed(false); }, [src]);
  if (!src || failed) return <span className="bx-opt-cover" style={{ background: `oklch(0.78 0.14 ${hue})` }} aria-hidden="true" />;
  return <img className="bx-opt-cover" src={src} alt={alt} loading="lazy" onError={() => setFailed(true)} />;
}

export default function FacetOptions({ def, options, selected, excluded, onToggle, loadOptions, max = 9 }: FacetOptionsProps) {
  const dynamic = !!def.dynamic && !!loadOptions;
  const filterable = def.filterable !== false;
  const excludable = def.excludable !== false;
  const [q, setQ] = useState("");
  const [moreItems, setMoreItems] = useState<FacetOptionRow[]>([]);
  const [hasMore, setHasMore] = useState(dynamic);
  const [loading, setLoading] = useState(false);
  const [searchResults, setSearchResults] = useState<FacetOptionRow[] | null>(null);
  const searchId = useRef(0);

  useEffect(() => {
    setMoreItems([]);
    setHasMore(dynamic);
    setSearchResults(null);
  }, [options, dynamic]);

  useEffect(() => {
    if (!dynamic || !loadOptions) return;
    if (!q.trim()) { setSearchResults(null); return; }
    const id = ++searchId.current;
    const timer = setTimeout(async () => {
      setLoading(true);
      try {
        const r = await loadOptions(def.key, q.trim(), 0, PAGE);
        if (id !== searchId.current) return;
        setSearchResults(r.items);
      } catch {
        if (id === searchId.current) setSearchResults([]);
      } finally {
        if (id === searchId.current) setLoading(false);
      }
    }, SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [q, dynamic, loadOptions, def.key]);

  const loadMore = useCallback(async () => {
    if (!dynamic || !loadOptions || loading || !hasMore || q.trim()) return;
    setLoading(true);
    try {
      const skip = options.length + moreItems.length;
      const r = await loadOptions(def.key, "", skip, PAGE);
      setMoreItems((prev) => [...prev, ...r.items]);
      if (r.items.length < PAGE) setHasMore(false);
    } catch {
      setHasMore(false);
    } finally {
      setLoading(false);
    }
  }, [dynamic, loadOptions, loading, hasMore, q, def.key, options.length, moreItems.length]);

  const isOn = (v: FacetValue) => hasFacetValue(selected, v);
  const isEx = (v: FacetValue) => hasFacetValue(excluded, v);
  const isActive = (v: FacetValue) => isOn(v) || isEx(v);

  const base: FacetOptionRow[] = dynamic && q.trim() && searchResults != null
    ? searchResults
    : dynamic
      ? [...options, ...moreItems]
      : q.trim()
        ? options.filter((o) => o.label.toLowerCase().includes(q.toLowerCase()))
        : options;

  const extras: FacetOptionRow[] = [...selected, ...excluded]
    .filter((v) => !base.some((o) => hasFacetValue([o.value], v)))
    .map((v) => ({ value: v, label: def.labelOf ? def.labelOf(v) : String(v), count: 0 }));

  const shown = [...extras, ...base].sort((a, b) => (isActive(a.value) ? 0 : 1) - (isActive(b.value) ? 0 : 1));
  const showSearch = dynamic || options.length > max;

  return (
    <div className="bx-facet">
      {showSearch && (
        <input className="bx-facet-search" value={q} onChange={(e) => setQ(e.target.value)} placeholder={`Filter ${def.label.toLowerCase()}…`} aria-label={`Filter ${def.label.toLowerCase()}`} />
      )}
      <div
        className={`bx-facet-opts${def.render === "pill" ? " bx-facet-opts--pills" : ""}`}
        onScroll={dynamic ? (e) => { const el = e.currentTarget; if (el.scrollHeight - el.scrollTop - el.clientHeight < 40) void loadMore(); } : undefined}
      >
        {shown.map((o) => {
          const on = isOn(o.value);
          const ex = isEx(o.value);
          const hue = o.hue ?? hueOf(o.label);
          return (
            <div
              key={String(o.value)}
              className={`bx-opt${on ? " on" : ""}${ex ? " ex" : ""}${def.render === "tile" ? " bx-opt-collection" : ""}${def.render === "pill" ? " bx-opt-pill" : ""}`}
              role={def.render === "pill" && filterable ? "button" : undefined}
              aria-pressed={def.render === "pill" && filterable ? on : undefined}
              tabIndex={def.render === "pill" && filterable ? 0 : undefined}
              onClick={def.render === "pill" && filterable ? () => onToggle(def.key, o.value, "inc") : undefined}
              onKeyDown={def.render === "pill" && filterable ? (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onToggle(def.key, o.value, "inc"); } } : undefined}
            >
              {def.render === "tile" ? (
                <TileImage src={o.imageUrl} hue={hue} alt="" />
              ) : (
                <>
                  <span className="bx-opt-box" aria-hidden="true">{on ? "✓" : ex ? "✕" : ""}</span>
                  {def.render === "swatch" && <span className="bx-opt-swatch" style={{ background: `oklch(0.78 0.14 ${hue})` }} aria-hidden="true" />}
                </>
              )}
              <span className="bx-opt-label" title={o.label}>{o.label}</span>
              <span className="bx-opt-count">{o.count.toLocaleString()}</span>
              {filterable && def.render !== "pill" && (
                <span className="bx-opt-acts">
                  <button type="button" className="bx-opt-inc" aria-label={`Include ${o.label}`} aria-pressed={on} onClick={() => onToggle(def.key, o.value, "inc")}>+</button>
                  {excludable && <button type="button" className="bx-opt-exc" aria-label={`Exclude ${o.label}`} aria-pressed={ex} onClick={() => onToggle(def.key, o.value, "exc")}>−</button>}
                </span>
              )}
            </div>
          );
        })}
        {loading && <div className="bx-facet-loading">Loading…</div>}
        {!loading && shown.length === 0 && <div className="bx-facet-empty">No matches</div>}
      </div>
    </div>
  );
}
