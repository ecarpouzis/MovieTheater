/**
 * The smart search: type a value and the dropdown offers the facet values that contain it (top seven,
 * prefix matches first, then by count) under a "search everything" row; type `token:` and the list
 * narrows to that facet. ↑/↓ move, Enter commits, Escape closes; a click outside closes. Committing a
 * facet row adds a filter, committing the text row sets `q`. Tokens come from the spec.
 *
 * A DYNAMIC facet is asked too. The suggestion index is built from the facet lists the section loads
 * up front, and a dynamic facet has none by definition — Movies' People is a server typeahead, so
 * `facets.person` is always empty. That meant the box could never once suggest a person: typing
 * "Tom Hanks" offered only the free-text row and the answer was "No titles match" for an actor with
 * 34 of them (Eric, 2026-09-03). So on two characters the box also asks every dynamic facet's
 * `loadOptions`, debounced and aborted like the rail's own typeahead, and merges what comes back.
 * `person:` as a prefix scopes the ask to that facet alone.
 *
 * The TEXT row is the other half of that report, and it was the half Eric clicked: it says "in all
 * fields" and `q=` read the two title columns, so the top row of a search for an actor was a dead
 * end even once the Person row sat under it. `q` now reaches credited people too
 * (`BrowseFilter.Apply`), which is what the row has always claimed — so the row stays first and the
 * Person row below it is the precise version of the same answer.
 */
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { SEARCH_FOCUS_EVENT, claimRailSearchFocus } from "../bar/useSlot";
import { hueOf } from "../sources/hue";
import type { FacetDef, FacetOptionRow, FacetSpec, FacetValue } from "./facetSpec";

/** Same beat as the rail's own typeahead — keystrokes stay off the wire until you pause. */
const SEARCH_DEBOUNCE_MS = 250;

interface FilterSuggestion { kind: "filter"; key: string; value: FacetValue; display: string; typeLabel: string; count: number; hue?: number }
interface TextSuggestion { kind: "text"; value: string }
export type Suggestion = FilterSuggestion | TextSuggestion;

export interface SmartSearchProps {
  spec: FacetSpec;
  facets?: Record<string, FacetOptionRow[]>;
  onAdd: (key: string, value: FacetValue) => void;
  onText: (q: string) => void;
  big?: boolean;
  placeholder?: string;
}

export function buildSuggestionIndex(spec: FacetSpec, facets?: Record<string, FacetOptionRow[]>): FilterSuggestion[] {
  if (!facets) return [];
  const idx: FilterSuggestion[] = [];
  for (const def of spec.facets) {
    if (def.filterable === false || def.includable === false) continue;
    for (const row of facets[def.key] ?? []) {
      idx.push({ kind: "filter", key: def.key, value: row.value, display: row.label, typeLabel: def.one, count: row.count, hue: def.render === "swatch" ? row.hue ?? hueOf(row.label) : undefined });
    }
  }
  return idx;
}

/** The shortest query a dynamic facet's server will answer (`/API/BrowsePeople`). */
export const MIN_DYNAMIC_QUERY = 2;

/** `token:` at the head of the box, resolved against the spec. */
export function scopeOf(raw: string, spec: FacetSpec): { def: FacetDef | null; term: string } {
  const text = raw.trim();
  const m = /^(\w+):\s*(.*)$/.exec(text);
  if (!m) return { def: null, term: text };
  const def = spec.facets.find((f) => f.token === m[1].toLowerCase());
  return def ? { def, term: m[2].trim() } : { def: null, term: text };
}

export function suggestionsFor(raw: string, spec: FacetSpec, index: FilterSuggestion[], dynamic: FilterSuggestion[] = []): Suggestion[] {
  const text = raw.trim();
  if (!text) return [];
  // The dynamic hits are already server-filtered by the term; deduped so a value that ALSO sits in
  // the up-front list is offered once.
  const seen = new Set(index.map((s) => `${s.key}\u0000${s.value}`));
  const all = [...index, ...dynamic.filter((s) => !seen.has(`${s.key}\u0000${s.value}`))];
  const { def, term: scopedTerm } = scopeOf(text, spec);
  if (def) {
    const term = scopedTerm.toLowerCase();
    return all.filter((s) => s.key === def.key && s.display.toLowerCase().includes(term)).slice(0, 8);
  }
  const term = text.toLowerCase();
  const starts = (s: FilterSuggestion) => (s.display.toLowerCase().startsWith(term) ? 0 : 1);
  const hits = all
    .filter((s) => s.display.toLowerCase().includes(term))
    .sort((a, b) => starts(a) - starts(b) || b.count - a.count)
    .slice(0, 7);
  return [{ kind: "text", value: text }, ...hits];
}

function defaultPlaceholder(spec: FacetSpec, big: boolean): string {
  if (!big) return "Search or filter…";
  const examples = spec.facets.filter((f) => f.valueType === "string" && f.filterable !== false).slice(0, 3).map((f) => `${f.token}:…`);
  return examples.length ? `Search — try ${examples.join(", ")}` : "Search…";
}

export default function SmartSearch({ spec, facets, onAdd, onText, big = false, placeholder }: SmartSearchProps) {
  const [q, setQ] = useState("");
  const [open, setOpen] = useState(false);
  const [sel, setSel] = useState(0);
  const wrap = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);

  // The phone top bar's magnifier opens the drawer and asks for the caret (`useSlot.ts`). Claimed
  // on MOUNT because the rail mounts with the drawer — the input does not exist when the button is
  // pressed — and on the event for a tap while the drawer is already open. Unconditional: on a phone
  // this is the section's ONE search box, and on a desktop nothing ever sets the flag.
  useEffect(() => {
    const take = () => {
      if (!claimRailSearchFocus()) return;
      const el = input.current;
      if (!el) return;
      // `preventScroll` then scroll the RAIL, not the input: focus()'s own scroll parks the input
      // flush against the phone top bar and pushes the rail's head line — the section's result count
      // — off the top of the drawer. Scrolling to the rail shows both.
      el.focus({ preventScroll: true });
      (el.closest(".bx-railbar") ?? el).scrollIntoView?.({ block: "start" });
    };
    take();
    window.addEventListener(SEARCH_FOCUS_EVENT, take);
    return () => window.removeEventListener(SEARCH_FOCUS_EVENT, take);
  }, []);

  useEffect(() => {
    const h = (e: MouseEvent) => { if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, []);

  const index = useMemo(() => buildSuggestionIndex(spec, facets), [spec, facets]);

  // The dynamic facets' hits, fetched as you type. The spec is held in a ref and the effect keys on
  // `spec.identity` so a caller that rebuilds the spec object every render cannot turn this into a fetch
  // loop.
  const specRef = useRef(spec);
  specRef.current = spec;
  const [dynamicHits, setDynamicHits] = useState<FilterSuggestion[]>([]);
  useEffect(() => {
    const s = specRef.current;
    const load = s.loadOptions;
    const { def, term } = scopeOf(q, s);
    const targets = s.facets.filter((f) => f.dynamic && f.filterable !== false && f.includable !== false && (!def || f.key === def.key));
    if (!load || !targets.length || term.length < MIN_DYNAMIC_QUERY) { setDynamicHits([]); return; }
    const controller = new AbortController();
    const timer = setTimeout(async () => {
      const rows = await Promise.all(targets.map((f) => load(f.key, term, 0, 6, controller.signal)
        .then((r) => r.items.map((row): FilterSuggestion => ({ kind: "filter", key: f.key, value: row.value, display: row.label, typeLabel: f.one, count: row.count })))
        .catch(() => [] as FilterSuggestion[])));
      if (!controller.signal.aborted) setDynamicHits(rows.flat());
    }, SEARCH_DEBOUNCE_MS);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [q, spec.identity]);

  const suggestions = useMemo(() => suggestionsFor(q, spec, index, dynamicHits), [q, spec, index, dynamicHits]);
  useEffect(() => { setSel(0); setOpen(suggestions.length > 0); }, [suggestions]);

  const commit = (s: Suggestion) => {
    if (s.kind === "text") onText(s.value);
    else onAdd(s.key, s.value);
    setQ("");
    setOpen(false);
  };

  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter" && !open && q.trim()) { e.preventDefault(); commit({ kind: "text", value: q.trim() }); return; }
    if (!open) return;
    if (e.key === "ArrowDown") { e.preventDefault(); setSel((i) => Math.min(i + 1, suggestions.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setSel((i) => Math.max(i - 1, 0)); }
    else if (e.key === "Enter") { e.preventDefault(); const s = suggestions[sel]; if (s) commit(s); }
    else if (e.key === "Escape") setOpen(false);
  };

  return (
    <div className={`bx-search${big ? " bx-search-big" : ""}`} ref={wrap}>
      <span className="bx-search-icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" width={big ? 18 : 15} height={big ? 18 : 15} fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round"><circle cx="11" cy="11" r="6.5" /><path d="M16.5 16.5L21 21" /></svg>
      </span>
      <input
        ref={input}
        className="bx-search-input"
        type="search"
        value={q}
        onChange={(e) => setQ(e.target.value)}
        onKeyDown={onKey}
        onFocus={() => suggestions.length && setOpen(true)}
        placeholder={placeholder ?? defaultPlaceholder(spec, big)}
        aria-label="Search"
        role="combobox"
        aria-expanded={open}
        aria-autocomplete="list"
      />
      {open && suggestions.length > 0 && (
        <div className="bx-suggest" role="listbox">
          {suggestions.map((s, i) =>
            s.kind === "text" ? (
              <button key="__text" type="button" role="option" aria-selected={i === sel} className={`bx-sugg${i === sel ? " on" : ""}`} onMouseEnter={() => setSel(i)} onClick={() => commit(s)}>
                <span className="bx-sugg-type">Search</span>
                <span className="bx-sugg-val">“{s.value}” in all fields</span>
              </button>
            ) : (
              <button key={`${s.key}:${String(s.value)}`} type="button" role="option" aria-selected={i === sel} className={`bx-sugg${i === sel ? " on" : ""}`} onMouseEnter={() => setSel(i)} onClick={() => commit(s)}>
                <span className="bx-sugg-type" style={s.hue != null ? { background: `oklch(0.82 0.12 ${s.hue})`, color: "#1a1410" } : undefined}>{s.typeLabel}</span>
                <span className="bx-sugg-val">{s.display}</span>
                <span className="bx-sugg-count">{s.count.toLocaleString()}</span>
              </button>
            ),
          )}
        </div>
      )}
    </div>
  );
}
