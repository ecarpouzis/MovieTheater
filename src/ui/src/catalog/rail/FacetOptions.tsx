/**
 * One facet's option list: a filter box once the list is long, THE ROW ITSELF as the include
 * control, a "−" for exclude that comes forward when you reach for it, active rows sorted to the top
 * and always shown (even when they fell below the server's cut), and — for a `dynamic` facet — a
 * debounced server search plus scroll-to-load paging through the spec's `loadOptions`. Publishers
 * draw a swatch, collections a square cover tile with a hue fallback.
 *
 * THE LABEL IS THE HERO, and it used to be the loser. The row carried a decorative 16px square that
 * looked like a checkbox but had no handler, plus a "+" and a "−" — five things in a 158px row, and
 * the only one that says WHAT the option is came last. Measured across every section on 2026-09-02
 * (`shot-rail`): the label box bottomed out at 46px, so Movies' "Comedy" drew as "Come…" and
 * "Adventure" as "Adve…"; 65% of Boardgames' publishers and 44% of Music's artists were clipped.
 *
 * So: the square is gone (it did nothing), the "+" is gone (the ROW is the +, the way every facet
 * list on the web works), and the "−" shares the count's cell instead of taking a column of its own.
 * Coarse pointers get the count and the "−" side by side, always visible, because the phone rail is
 * 390px wide and has the room the 236px desktop sider does not — the squeeze is desktop-only, so
 * only desktop pays for it.
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

  // What one click on the ROW does. Normally it includes; a facet the API can only subtract from
  // (the Arcade's regions — `includable: false`) makes the row the exclude control instead, so its
  // one obvious gesture is still its one available gesture.
  const rowMode: FacetMode = includable ? "inc" : "exc";
  const rowVerb = rowMode === "inc" ? "Include" : "Exclude";
  const pill = def.render === "pill";
  // The separate "−" exists only where the row's own click cannot reach exclude.
  const hasExcludeButton = filterable && excludable && includable && !pill;

  return (
    <div className="bx-facet">
      {showSearch && (
        <input className="bx-facet-search" value={q} onChange={(e) => setQ(e.target.value)} placeholder={`Filter ${def.label.toLowerCase()}…`} aria-label={`Filter ${def.label.toLowerCase()}`} />
      )}
      <div
        className={`bx-facet-opts${pill ? " bx-facet-opts--pills" : ""}${def.stops ? " bx-facet-opts--stops" : ""}`}
        onScroll={dynamic ? (e) => { const el = e.currentTarget; if (el.scrollHeight - el.scrollTop - el.clientHeight < 40) void loadMore(); } : undefined}
      >
        {shown.map((o) => {
          const on = isOn(o.value);
          const ex = isEx(o.value);
          const hue = o.hue ?? hueOf(o.label);
          const rowClass = `bx-opt${on ? " on" : ""}${ex ? " ex" : ""}${def.render === "tile" ? " bx-opt-collection" : ""}${pill ? " bx-opt-pill" : ""}`;
          // Everything the row SAYS. Identical in both shapes below; only the element differs.
          const body = (
            <>
              {def.render === "tile" && <TileImage src={o.imageUrl} hue={hue} alt="" />}
              {def.render === "swatch" && <span className="bx-opt-swatch" style={{ background: `oklch(0.78 0.14 ${hue})` }} aria-hidden="true" />}
              <span className="bx-opt-label" title={o.label}>{o.label}</span>
              {def.showCounts !== false && <span className="bx-opt-count">{o.count.toLocaleString()}</span>}
            </>
          );
          // A pill IS the control — one element, a real button, no tail. Everything else is a row
          // whose body is the button and whose "−" is a SIBLING: a button inside a button is not
          // something browsers or screen readers honour.
          if (pill) {
            return filterable
              ? (
                <button type="button" key={String(o.value)} className={rowClass} aria-label={`${rowVerb} ${o.label}`} aria-pressed={rowMode === "inc" ? on : ex} onClick={() => onToggle(def.key, o.value, rowMode)}>
                  {body}
                </button>
              )
              : <div key={String(o.value)} className={rowClass}>{body}</div>;
          }
          return (
            <div key={String(o.value)} className={rowClass}>
              {filterable
                ? (
                  <button type="button" className="bx-opt-main" aria-label={`${rowVerb} ${o.label}`} aria-pressed={rowMode === "inc" ? on : ex} onClick={() => onToggle(def.key, o.value, rowMode)}>
                    {body}
                  </button>
                )
                : <div className="bx-opt-main">{body}</div>}
              {hasExcludeButton && (
                <button type="button" className="bx-opt-exc" title={`Exclude ${o.label}`} aria-label={`Exclude ${o.label}`} aria-pressed={ex} onClick={() => onToggle(def.key, o.value, "exc")}>−</button>
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
