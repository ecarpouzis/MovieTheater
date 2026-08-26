import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import type { CatalogViewState } from "./state/useCatalogView";
import { NO_GROUP } from "./state/useCatalogView";
import { FLAT_VIEWS, VIEW_LABELS, type CatalogSource, type ItemsMode, type ViewMode } from "./types";

/**
 * The command pills — View · Group · Items · Sort — and the ⚙ Tweaks button. Until R9's single
 * section bar exists this is a compact row above each section's grid; R9 moves the same pills
 * into the bar. Everything it shows is derived from the source's offer and the current state.
 */

/**
 * On phones the pill row is a horizontal scroll container (overflow-x), which would clip an
 * absolutely-positioned menu. Anchor the menu to the viewport instead: fixed elements escape
 * ancestor overflow clipping. (The standalone site's `mobileMenuPos`, 680 px breakpoint.)
 */
export function mobileMenuPos(btn: HTMLElement, menuWidth = 178): CSSProperties | undefined {
  if (!window.matchMedia("(max-width: 680px)").matches) return undefined;
  const r = btn.getBoundingClientRect();
  const left = Math.max(8, Math.min(r.left, window.innerWidth - menuWidth - 8));
  return { position: "fixed", top: r.bottom + 5, left, right: "auto" };
}

export interface PillOption<T extends string> { value: T; label: string }

export function Pill<T extends string>({ label, value, options, onPick }: {
  label: string; value: T; options: PillOption<T>[]; onPick: (v: T) => void;
}) {
  const [open, setOpen] = useState(false);
  const [menuPos, setMenuPos] = useState<CSSProperties | undefined>();
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return undefined;
    const h = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, [open]);
  const current = options.find((o) => o.value === value);
  return (
    <div className="bx-pill-wrap" ref={ref}>
      <button
        type="button" className="bx-pill" aria-haspopup="listbox" aria-expanded={open}
        onClick={(e) => { setMenuPos(mobileMenuPos(e.currentTarget)); setOpen((o) => !o); }}
      >
        <span className="bx-pill-k">{label}</span>
        <span className="bx-pill-v">{current?.label ?? value}</span>
        <svg viewBox="0 0 10 6" width="9" height="6" fill="currentColor" aria-hidden="true"><path d="M0 0h10L5 6z" /></svg>
      </button>
      {open && (
        <div className="bx-dd-menu" role="listbox" style={menuPos}>
          {options.map((o) => (
            <button
              key={o.value} type="button" role="option" aria-selected={o.value === value}
              className={`bx-dd-item${o.value === value ? " on" : ""}`}
              onClick={() => { onPick(o.value); setOpen(false); }}
            >
              {o.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export interface ViewSwitcherProps {
  state: CatalogViewState;
  source: CatalogSource;
  /** Views the package has implemented (∩ source.supports = what the pill offers). */
  available: readonly ViewMode[];
  onView: (v: ViewMode) => void;
  onGroup: (g: string) => void;
  onItems: (i: ItemsMode) => void;
  onSort: (s: string) => void;
  tweaksOpen: boolean;
  onTweaks: () => void;
  /** Anything the section wants at the left of the row (a count, a title). */
  leading?: ReactNode;
}

export default function ViewSwitcher({ state, source, available, onView, onGroup, onItems, onSort, tweaksOpen, onTweaks, leading }: ViewSwitcherProps) {
  const views = source.supports.filter((v) => available.includes(v));
  const viewOptions: PillOption<ViewMode>[] = views.map((v) => ({ value: v, label: VIEW_LABELS[v] }));
  // The flat views never fragment into groups; their group axis is the Items pill instead.
  const groupable = !FLAT_VIEWS.has(state.view) && state.view !== "directory" && source.groups.length > 0;
  const groupOptions: PillOption<string>[] = [{ value: NO_GROUP, label: "No grouping" }, ...source.groups.map((g) => ({ value: g.value, label: `By ${g.label}` }))];
  const itemsModes = source.itemsModes ?? [];
  const showItems = itemsModes.includes("groups") && (FLAT_VIEWS.has(state.view) || (groupable && state.group !== NO_GROUP));
  const itemsOptions: PillOption<ItemsMode>[] = itemsModes.map((m) => ({ value: m, label: source.itemsLabels?.[m] ?? (m === "items" ? "Every item" : "One per group") }));
  const sortOptions: PillOption<string>[] = source.sorts.map((s) => ({ value: s.value, label: s.label }));

  return (
    <div className="bx-toolbar" role="toolbar" aria-label="Browse controls">
      <div className="bx-tool-left">{leading}</div>
      <div className="bx-tool-right">
        {viewOptions.length > 1 && <Pill<ViewMode> label="View" value={state.view} options={viewOptions} onPick={onView} />}
        {groupable && <Pill<string> label="Group" value={state.group} options={groupOptions} onPick={onGroup} />}
        {showItems && <Pill<ItemsMode> label="Items" value={state.items} options={itemsOptions} onPick={onItems} />}
        {sortOptions.length > 1 && <Pill<string> label="Sort" value={state.sort} options={sortOptions} onPick={onSort} />}
        <button
          type="button" className={`bx-tool-btn bx-tool-gear${tweaksOpen ? " on" : ""}`}
          aria-pressed={tweaksOpen} aria-label="Browse tweaks" title="Browse tweaks" onClick={onTweaks}
        >
          ⚙
        </button>
      </div>
    </div>
  );
}
