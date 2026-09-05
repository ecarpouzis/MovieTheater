/**
 * The rail surfaces a SECTION PAGE owns, built in one call (R9 S2 normalization): the bar's
 * SmartSearch on desktop, and the active chips over the results. Every section drew the same things
 * from the same state; only the placeholder differs.
 *
 * It used to own a third thing — the phone's Filters pill and the full-page facet SHEET it raised.
 * Both were deleted on 2026-08-28 (Eric, on his phone: "this filter button seems to present the same
 * options opening the drawer does — why do these buttons still exist?"). Since the drawer became the
 * sider it holds the section's own `FacetRail`, so the pill was a second door onto one room. On a
 * phone the filters live in the DRAWER, full stop; the top bar's magnifier opens it and the rail's
 * SmartSearch takes the caret (`catalog/bar/useSlot.ts`).
 *
 * Returned as nodes rather than rendered here because the pieces land in two different places —
 * `chips` in `beforeResults` and `surfaces` anywhere in the page's tree (it portals itself where it
 * belongs).
 *
 * Deliberately NOT a hook (it calls none): the pages that use it return early on their loading and
 * gated states, and a `use*` name there would be a rules-of-hooks trap for the next reader. `isMobile`
 * is passed in for the same reason — a section's count query usually needs it before this runs.
 */
import type { ReactNode } from "react";
import { BarSearchSlot } from "../bar/SlotPortal";
import RailChips from "./RailChips";
import SmartSearch from "./SmartSearch";
import type { SectionRailState } from "./useSectionRail";

export interface SectionRailSurfacesOptions {
  /** The bar search's placeholder — the section's own vocabulary ("A game, system:PS2, genre:RPG…"). */
  placeholder?: string;
  /** An extra class on the chips row (a section that skins it). */
  chipsClassName?: string;
}

export interface SectionRailSurfaces {
  /** The active-filter chips over the results (draws nothing when nothing is active). */
  chips: ReactNode;
  /** The bar's SmartSearch (desktop only — it portals itself into the bar's centre slot). */
  surfaces: ReactNode;
}

export default function sectionRailSurfaces(rail: SectionRailState, isMobile: boolean, opts: SectionRailSurfacesOptions = {}): SectionRailSurfaces {
  const { placeholder, chipsClassName } = opts;
  return {
    chips: (
      <RailChips
        spec={rail.spec}
        state={rail.state}
        actions={rail.actions}
        facets={rail.facets.data}
        activeCount={rail.activeCount}
        // No save button here: "Saved views" at the foot of the rail is the tool's ONE home
        // (Eric, 2026-09-05) — a second button over the results was the duplicate-options bug.
        className={chipsClassName}
      />
    ),
    surfaces: (
      /* The SmartSearch in the SectionBar's centre box (R9 S1d/S2): text = `q`, a token = a facet.
         Desktop only — the phone bar has no centre box, and the phone's copy of this same component
         is the one at the top of the drawer's rail. */
      !isMobile ? (
        <BarSearchSlot>
          <SmartSearch spec={rail.spec} facets={rail.facets.data} onAdd={rail.actions.add} onText={rail.actions.setText} placeholder={placeholder} />
        </BarSearchSlot>
      ) : null
    ),
  };
}
