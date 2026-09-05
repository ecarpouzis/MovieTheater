/**
 * The filter rail: a title with the active count, the smart search, the saved searches with the
 * result count, then one collapsible section per facet, the year range, the rating floor and — on
 * grouped views — the personal flags.
 *
 * ONE shape, one place. It is the persistent column of the section's sider — and on a phone the nav
 * DRAWER is that sider (2026-08-27), so this is what the hamburger opens. The second skin, a
 * full-page `sheet` raised behind the bar's Filters pill, was deleted on 2026-08-28: it offered the
 * same options the drawer already held, which is the duplicate-options bug in its purest form.
 */
import { Fragment, useState } from "react";
import type { FacetOptionRow, FacetSpec, FacetState, FacetValue, RangeFacetDef } from "./facetSpec";
import { isRangeSet } from "./facetSpec";
import FacetOptions from "./FacetOptions";
import RailSection from "./RailSection";
import { DateFacet, FlagFacet, RatingFacet, StopsRangeFacet } from "./RangeFacets";
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
  /** Render the SmartSearch in the rail (default). The DESKTOP sider passes false since R9 S1d: the
   *  page portals the same SmartSearch into the SectionBar's centre slot instead. A phone has no bar
   *  search box, so there it stays true and the rail carries it (see `SectionSiderRail`). */
  search?: boolean;
  /** Whether the current view groups (the groups-only facets and flags show only then). */
  grouped: boolean;
  saved?: FacetRailSaved;
  activeCount: number;
  title?: string;
  className?: string;
}

// Hoisted empties: `?? []` in the JSX is a NEW array identity every render, which resets
// `FacetOptions`' paged long tail (its reset effect keys on `options`) and defeats its memo.
const NO_OPTIONS: FacetOptionRow[] = [];
const NO_VALUES: FacetValue[] = [];

function RailBody({ spec, state, actions, facets, facetsLoading, total, grouped, saved, activeCount, title, search = true }: FacetRailProps) {
  const [saving, setSaving] = useState(false);
  const [savedOpen, setSavedOpen] = useState(false);
  const noun = spec.noun ?? "results";
  // A fixed-scale range sits under the facet it names (`after`); the rest follow every facet.
  const rangesAfter = (key: string | null) => (spec.ranges ?? []).filter((r) => (key == null ? !r.after || !spec.facets.some((f) => f.key === r.after) : r.after === key));
  const rangeSection = (def: RangeFacetDef) => (
    <RailSection key={`range:${def.key}`} title={def.label} count={isRangeSet(state.ranges?.[def.key]) ? 1 : 0} defaultOpen={def.defaultOpen}>
      <StopsRangeFacet def={def} range={state.ranges?.[def.key]} onChange={(min, max) => actions.setRange(def.key, min, max)} />
    </RailSection>
  );
  // The year range sits where the section PUTS it (`years.after`), not always at the foot of the
  // rail: the approved order runs Type · Genre · MPA · Years · Franchise · People · Mood.
  const yearSection = !spec.years ? null : (
    <RailSection key="years" title={spec.years.label ?? "Years"} count={state.yearMin != null || state.yearMax != null ? 1 : 0}>
      <DateFacet showDecades={spec.years?.decadePills !== false} yearMin={state.yearMin} yearMax={state.yearMax} decades={facets?.[spec.years.decadesKey] ?? NO_OPTIONS} onChange={actions.setYears} />
    </RailSection>
  );
  const yearsAfter = (key: string | null) => {
    if (!yearSection) return null;
    const anchor = spec.years?.after && spec.facets.some((f) => f.key === spec.years!.after) ? spec.years.after : null;
    return anchor === key ? yearSection : null;
  };
  return (
    <>
      {/* The head line is the delineation between whatever the sider puts above the rail (Movies' index
          rows) and the filters: a labelled rule carrying the active badge and the result count. */}
      <div className="bx-rail-top">
        <span className="bx-rail-toptitle">
          {title ?? "Filters"}{activeCount > 0 && <span className="bx-rail-topbadge">{activeCount}</span>}
        </span>
        {total != null && total >= 0 && <span className="bx-rail-count">{total.toLocaleString()} {noun}</span>}
      </div>

      {spec.text !== false && search && <SmartSearch spec={spec} facets={facets} onAdd={actions.add} onText={actions.setText} />}

      <div className="bx-rail-facets" aria-busy={facetsLoading || undefined}>
        {spec.facets.filter((def) => !def.hidden && (def.appliesTo !== "groups" || grouped)).map((def) => (
          <Fragment key={def.key}>
            <RailSection title={def.label} count={(state.include[def.key]?.length ?? 0) + (state.exclude[def.key]?.length ?? 0)} defaultOpen={def.defaultOpen}>
              <FacetOptions
                def={def}
                options={facets?.[def.key] ?? NO_OPTIONS}
                selected={state.include[def.key] ?? NO_VALUES}
                excluded={state.exclude[def.key] ?? NO_VALUES}
                onToggle={actions.setMode}
                loadOptions={spec.loadOptions}
              />
            </RailSection>
            {rangesAfter(def.key).map(rangeSection)}
            {yearsAfter(def.key)}
          </Fragment>
        ))}
        {rangesAfter(null).map(rangeSection)}
        {yearsAfter(null)}
        {spec.rating && (
          <RailSection title="Rating" count={state.ratingMin > 0 ? 1 : 0}>
            <RatingFacet value={state.ratingMin} presets={spec.rating.presets} onChange={actions.setRating} />
          </RailSection>
        )}
        {spec.flags && spec.flagsRail !== false && spec.flags.some((f) => f.appliesTo !== "groups" || grouped) && (
          <RailSection title={spec.flagsLabel ?? "My lists"} count={spec.flags.filter((f) => state.flags[f.key]).length}>
            <FlagFacet flags={spec.flags.filter((f) => f.appliesTo !== "groups" || grouped)} state={state.flags} onChange={actions.setFlag} />
          </RailSection>
        )}
      </div>

      {/* Saved views: one disclosure line at the FOOT of the filters, drawn only when there are some
          (or a save is in progress) — a minimally-used feature must not cost every rail its space
          (Eric, 2026-09-04). "+ Save view" lives in the chip row over the results (RailChips). */}
      {saved && (saving || saved.list.length > 0) && (
        <div className="bx-rail-savedwrap">
          {saving
            ? <SaveSearchPrompt onSave={(name) => { saved.onSave?.(name); setSaving(false); }} onCancel={() => setSaving(false)} />
            : (
              <>
                <button type="button" className="bx-saved-line" aria-expanded={savedOpen} onClick={() => setSavedOpen((v) => !v)}>
                  <span className="bx-saved-line-ic" aria-hidden="true">★</span>
                  Saved views
                  <span className="bx-saved-line-n">{saved.list.length}</span>
                  <span className={`bx-rsec-carat${savedOpen ? " open" : ""}`} aria-hidden="true">▾</span>
                </button>
                {savedOpen && (
                  <div className="bx-saved-open">
                    <SavedSearchesRail list={saved.list} onApply={saved.onApply} onRemove={saved.onRemove} />
                  </div>
                )}
              </>
            )}
        </div>
      )}
    </>
  );
}

export default function FacetRail(props: FacetRailProps) {
  const { className } = props;
  return (
    <aside className={`bx-railbar${className ? ` ${className}` : ""}`} aria-label={props.title ?? "Filters"}>
      <RailBody {...props} />
    </aside>
  );
}
