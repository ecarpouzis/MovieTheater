/**
 * The rail surfaces a SECTION PAGE owns, built in one call (R9 S2 normalization): the bar's
 * SmartSearch on desktop, the phone's Filters pill + full-page sheet, and the active chips over the
 * results. Every section drew the same three things from the same state; only the placeholder and
 * the count differ.
 *
 * Returned as nodes rather than rendered here because the pieces land in three different places —
 * `pill` in the host's `tools` (or the bar's tools slot), `chips` in `beforeResults`, and `surfaces`
 * anywhere in the page's tree (both halves portal/fix themselves where they belong).
 *
 * Deliberately NOT a hook (it calls none): the pages that use it return early on their loading and
 * gated states, and a `use*` name there would be a rules-of-hooks trap for the next reader. The
 * sheet's open state (`useRailSheet`) stays the PAGE's and is passed in — a section's count query
 * usually needs `isMobile` before this runs, and the sider tree must not answer the phone bar's
 * search request; only the tree that renders the sheet may.
 */
import type { ReactNode } from "react";
import { BarSearchSlot } from "../bar/SlotPortal";
import FacetRail from "./FacetRail";
import FilterPill from "./FilterPill";
import RailChips from "./RailChips";
import SmartSearch from "./SmartSearch";
import type { SectionRailState } from "./useSectionRail";
import type useRailSheet from "./useRailSheet";

export type RailSheet = ReturnType<typeof useRailSheet>;

export interface SectionRailSurfacesOptions {
  /** The result count for the current state (the sheet's head line). */
  total?: number | null;
  /** The section's own rows are still loading (ANDed with the option lists' own loading flag). */
  loading?: boolean;
  /** The bar search's placeholder — the section's own vocabulary ("A game, system:PS2, genre:RPG…"). */
  placeholder?: string;
  /** An extra class on the chips row (a section that skins it). */
  chipsClassName?: string;
}

export interface SectionRailSurfaces {
  /** The phone's Filters pill; null on desktop, where the sider carries the rail. */
  pill: ReactNode | null;
  /** The active-filter chips over the results (draws nothing when nothing is active). */
  chips: ReactNode;
  /** The bar's SmartSearch (desktop) and the phone's facet sheet — both portal/fix themselves. */
  surfaces: ReactNode;
}

export default function sectionRailSurfaces(rail: SectionRailState, sheet: RailSheet, opts: SectionRailSurfacesOptions = {}): SectionRailSurfaces {
  const { total, loading, placeholder, chipsClassName } = opts;
  return {
    pill: sheet.isMobile ? <FilterPill count={rail.activeCount} onClick={sheet.show} /> : null,
    chips: (
      <RailChips
        spec={rail.spec}
        state={rail.state}
        actions={rail.actions}
        facets={rail.facets.data}
        activeCount={rail.activeCount}
        onSave={rail.saveCurrent}
        className={chipsClassName}
      />
    ),
    surfaces: (
      <>
        {/* The SmartSearch in the SectionBar's centre box (R9 S1d/S2): text = `q`, a token = a facet. */}
        {!sheet.isMobile && (
          <BarSearchSlot>
            <SmartSearch spec={rail.spec} facets={rail.facets.data} onAdd={rail.actions.add} onText={rail.actions.setText} placeholder={placeholder} />
          </BarSearchSlot>
        )}
        {sheet.isMobile && (
          <FacetRail
            variant="sheet"
            open={sheet.open}
            onClose={sheet.hide}
            spec={rail.spec}
            state={rail.state}
            actions={rail.actions}
            activeCount={rail.activeCount}
            facets={rail.facets.data}
            facetsLoading={rail.facets.isLoading || !!loading}
            total={total ?? null}
            grouped={rail.grouped}
            saved={{ list: rail.saved.list, onApply: rail.actions.replaceSearch, onRemove: rail.saved.remove, onSave: rail.saveCurrent }}
          />
        )}
      </>
    ),
  };
}
