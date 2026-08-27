/**
 * The filter rail: a title with the active count, the smart search, the saved searches with the
 * result count, then one collapsible section per facet, the year range, the rating floor and — on
 * grouped views — the personal flags. Two skins over one body:
 *
 *   rail   the persistent column (the section's sider on desktop)
 *   sheet  the phone's full-page sheet (a partial sheet wastes the rest of the screen on a dimmed
 *          page): fixed, scrolls internally, locks the page behind it, Escape / backdrop / × close
 */
import { useEffect, useState, type ReactNode } from "react";
import type { FacetOptionRow, FacetSpec, FacetState } from "./facetSpec";
import FacetOptions from "./FacetOptions";
import RailSection from "./RailSection";
import { DateFacet, FlagFacet, RatingFacet } from "./RangeFacets";
import { SaveSearchPrompt, SavedSearchesRail } from "./SavedSearchesRail";
import type { SavedSearch } from "./savedSearches";
import SmartSearch from "./SmartSearch";
import type { FacetActions } from "./useFacetState";
import "./rail.css";

export interface FacetRailSaved {
  list: SavedSearch[];
  onApply: (search: string) => void;
  onRemove: (id: string) => void;
  /** Offered only when there is something to save (the caller passes the current search). */
  onSave?: (name: string) => void;
}

export interface FacetRailProps {
  spec: FacetSpec;
  state: FacetState;
  actions: FacetActions;
  facets?: Record<string, FacetOptionRow[]>;
  facetsLoading?: boolean;
  /** The result count for the current state, when known. */
  total?: number | null;
  /** Render the SmartSearch in the rail (default). The desktop sider passes false since R9 S1d: the
   *  page portals the same SmartSearch into the SectionBar's centre slot instead. */
  search?: boolean;
  /** Whether the current view groups (the groups-only facets and flags show only then). */
  grouped: boolean;
  variant: "rail" | "sheet";
  open?: boolean;
  onClose?: () => void;
  saved?: FacetRailSaved;
  activeCount: number;
  title?: string;
  className?: string;
}

function CloseGlyph({ sheet }: { sheet: boolean }) {
  return sheet
    ? <svg viewBox="0 0 12 12" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="1.6" aria-hidden="true"><path d="M2 2l8 8M10 2l-8 8" /></svg>
    : <svg viewBox="0 0 10 6" width="12" height="8" fill="currentColor" style={{ transform: "rotate(90deg)" }} aria-hidden="true"><path d="M0 0h10L5 6z" /></svg>;
}

function RailBody({ spec, state, actions, facets, facetsLoading, total, grouped, saved, activeCount, title, sheet, onClose, search = true }: FacetRailProps & { sheet: boolean }) {
  const [saving, setSaving] = useState(false);
  const canSave = !!saved?.onSave && activeCount > 0;
  const noun = spec.noun ?? "results";
  return (
    <>
      <div className="bx-rail-top">
        <span className="bx-rail-toptitle">
          {title ?? "Filters"}{activeCount > 0 && <span className="bx-rail-topbadge">{activeCount}</span>}
        </span>
        {onClose && (
          <button type="button" className="bx-rail-collapse" onClick={onClose} title={sheet ? "Close filters" : "Collapse filters"} aria-label={sheet ? "Close filters" : "Collapse filters"}>
            <CloseGlyph sheet={sheet} />
          </button>
        )}
      </div>

      {spec.text !== false && search && <SmartSearch spec={spec} facets={facets} onAdd={actions.add} onText={actions.setText} />}

      <div className="bx-rail-savedwrap">
        <div className="bx-rail-savedhead">
          <span className="bx-rail-label">Saved searches</span>
          {total != null && total >= 0 && <span className="bx-rail-count">{total.toLocaleString()} {noun}</span>}
        </div>
        {saved && (saving
          ? <SaveSearchPrompt onSave={(name) => { saved.onSave?.(name); setSaving(false); }} onCancel={() => setSaving(false)} />
          : (
            <>
              <SavedSearchesRail list={saved.list} onApply={saved.onApply} onRemove={saved.onRemove} />
              {canSave && <button type="button" className="bx-chip-save bx-rail-savebtn" onClick={() => setSaving(true)}>＋ Save this search</button>}
            </>
          ))}
      </div>

      <div className="bx-rail-facets" aria-busy={facetsLoading || undefined}>
        {spec.facets.filter((def) => def.appliesTo !== "groups" || grouped).map((def) => (
          <RailSection key={def.key} title={def.label} count={(state.include[def.key]?.length ?? 0) + (state.exclude[def.key]?.length ?? 0)} defaultOpen={def.defaultOpen}>
            <FacetOptions
              def={def}
              options={facets?.[def.key] ?? []}
              selected={state.include[def.key] ?? []}
              excluded={state.exclude[def.key] ?? []}
              onToggle={actions.setMode}
              loadOptions={spec.loadOptions}
            />
          </RailSection>
        ))}
        {spec.years && (
          <RailSection title="Date range" count={state.yearMin != null || state.yearMax != null ? 1 : 0}>
            <DateFacet showDecades={spec.years?.decadePills !== false} yearMin={state.yearMin} yearMax={state.yearMax} decades={facets?.[spec.years.decadesKey] ?? []} onChange={actions.setYears} />
          </RailSection>
        )}
        {spec.rating && (
          <RailSection title="Rating" count={state.ratingMin > 0 ? 1 : 0}>
            <RatingFacet value={state.ratingMin} presets={spec.rating.presets} onChange={actions.setRating} />
          </RailSection>
        )}
        {spec.flags && spec.flags.some((f) => f.appliesTo !== "groups" || grouped) && (
          <RailSection title="My lists" count={spec.flags.filter((f) => state.flags[f.key]).length}>
            <FlagFacet flags={spec.flags.filter((f) => f.appliesTo !== "groups" || grouped)} state={state.flags} onChange={actions.setFlag} />
          </RailSection>
        )}
      </div>
    </>
  );
}

export default function FacetRail(props: FacetRailProps) {
  const { variant, open = true, onClose, className } = props;
  const sheet = variant === "sheet";

  // The sheet locks the page behind it and closes on Escape.
  useEffect(() => {
    if (!sheet || !open) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose?.(); };
    document.addEventListener("keydown", onKey);
    return () => { document.body.style.overflow = prev; document.removeEventListener("keydown", onKey); };
  }, [sheet, open, onClose]);

  if (sheet && !open) return null;

  const body: ReactNode = <RailBody {...props} sheet={sheet} />;
  if (!sheet) return <aside className={`bx-railbar${className ? ` ${className}` : ""}`} aria-label={props.title ?? "Filters"}>{body}</aside>;
  return (
    <>
      <div className="bx-rail-backdrop" onClick={onClose} aria-hidden="true" />
      <aside className={`bx-railbar bx-railbar-sheet${className ? ` ${className}` : ""}`} role="dialog" aria-modal="true" aria-label={props.title ?? "Filters"}>{body}</aside>
    </>
  );
}
