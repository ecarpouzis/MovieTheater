/**
 * One facet's option list: a filter box once the list is long, a "+" (include) and "−" (exclude) per
 * row, active rows sorted to the top and always shown (even when they fell below the server's cut),
 * and — for a `dynamic` facet — a debounced server search plus scroll-to-load paging through the
 * spec's `loadOptions`. Publishers draw a swatch, collections a square cover tile with a hue fallback.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
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
  // decoding="async" keeps a long tile list off the main thread's decode path (the catalog's image law).
  return <img className="bx-opt-cover" src={src} alt={alt} loading="lazy" decoding="async" onError={() => setFailed(true)} />;
}

export default function FacetOptions({ def, options, selected, excluded, onToggle, loadOptions, max = 9 }: FacetOptionsProps) {
  const dynamic = !!def.dynamic && !!loadOptions;
  const filterable = def.filterable !== false;
  const excludable = def.excludable !== false;
  const includable = def.includable !== false;
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

  // The typeahead ABORTS the answer it no longer wants (the catalog's fetch law: a sequence guard
  // alone leaves the server running every superseded query to completion) — the debounce keeps the
  // keystrokes off the wire, the controller takes back the one that did go out.
  useEffect(() => {
    if (!dynamic || !loadOptions) return;
    if (!q.trim()) { setSearchResults(null); return; }
    const id = ++searchId.current;
    const controller = new AbortController();
    const timer = setTimeout(async () => {
      setLoading(true);
      try {
        const r = await loadOptions(def.key, q.trim(), 0, PAGE, controller.signal);
        if (id !== searchId.current) return;
        setSearchResults(r.items);
      } catch {
        if (id === searchId.current && !controller.signal.aborted) setSearchResults([]);
      } finally {
        if (id === searchId.current) setLoading(false);
      }
    }, SEARCH_DEBOUNCE_MS);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [q, dynamic, loadOptions, def.key]);

  // The scroll-to-load page is aborted on unmount for the same reason (a closed rail section, a
  // sheet dismissed mid-page).
  const moreAbort = useRef<AbortController | null>(null);
  useEffect(() => () => moreAbort.current?.abort(), []);

  const loadMore = useCallback(async () => {
    if (!dynamic || !loadOptions || loading || !hasMore || q.trim()) return;
    setLoading(true);
    const controller = new AbortController();
    moreAbort.current?.abort();
    moreAbort.current = controller;
    try {
      const skip = options.length + moreItems.length;
      const r = await loadOptions(def.key, "", skip, PAGE, controller.signal);
      if (controller.signal.aborted) return;
      setMoreItems((prev) => [...prev, ...r.items]);
      if (r.items.length < PAGE) setHasMore(false);
    } catch {
      if (!controller.signal.aborted) setHasMore(false);
    } finally {
      if (!controller.signal.aborted) setLoading(false);
    }
  }, [dynamic, loadOptions, loading, hasMore, q, def.key, options.length, moreItems.length]);

  const isOn = (v: FacetValue) => hasFacetValue(selected, v);
  const isEx = (v: FacetValue) => hasFacetValue(excluded, v);

  // The rows to draw, memoized: a long tail (Movies' genres, Boardgames' designers, the paged
  // People typeahead) is filtered, deduped against the active values and sorted here, and the rail
  // re-renders on every URL change — doing that work per render is a scroll-jank source.
  const shown = useMemo(() => {
    const term = q.trim().toLowerCase();
    const base: FacetOptionRow[] = dynamic && term && searchResults != null
      ? searchResults
      : dynamic
        ? [...options, ...moreItems]
        : term
          ? options.filter((o) => o.label.toLowerCase().includes(term))
          : options;
    const extras: FacetOptionRow[] = [...selected, ...excluded]
      .filter((v) => !base.some((o) => hasFacetValue([o.value], v)))
      .map((v) => ({ value: v, label: def.labelOf ? def.labelOf(v) : String(v), count: 0 }));
    const active = (v: FacetValue) => (hasFacetValue(selected, v) || hasFacetValue(excluded, v) ? 0 : 1);
    return [...extras, ...base].sort((a, b) => active(a.value) - active(b.value));
  }, [q, dynamic, searchResults, options, moreItems, selected, excluded, def]);
  const showSearch = dynamic || options.length > max;

  return (
    <div className="bx-facet">
      {showSearch && (
        <input className="bx-facet-search" value={q} onChange={(e) => setQ(e.target.value)} placeholder={`Filter ${def.label.toLowerCase()}…`} aria-label={`Filter ${def.label.toLowerCase()}`} />
      )}
      <div
        className={`bx-facet-opts${def.render === "pill" ? " bx-facet-opts--pills" : ""}${def.stops ? " bx-facet-opts--stops" : ""}`}
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
              {def.showCounts !== false && <span className="bx-opt-count">{o.count.toLocaleString()}</span>}
              {filterable && def.render !== "pill" && (
                <span className="bx-opt-acts">
                  {includable && <button type="button" className="bx-opt-inc" aria-label={`Include ${o.label}`} aria-pressed={on} onClick={() => onToggle(def.key, o.value, "inc")}>+</button>}
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
