/**
 * A section's filter rail in the sider: the generic `FacetRail` over the section's spec, reading the
 * same URL the page reads and pushing the same URLs — nothing crosses the sider/page boundary
 * through props. The head line carries the section's own result count.
 *
 * On a PHONE the nav drawer is this sider (2026-08-27) and it is the section's ONE filter surface
 * (2026-08-28) — so the search moves with it. On desktop the SmartSearch lives in the SectionBar's
 * centre slot (the page mounts it there) and the rail draws none; on a phone the bar has no search
 * box at all, only the top bar's magnifier, which opens THIS drawer — so the rail carries the
 * SmartSearch, at the top, where the magnifier's caret lands.
 *
 * A section's own rail file is now just its spec, its count and this (`…SiderRail.tsx`).
 */
import type { ReactNode } from "react";
import useIsMobile from "../../hooks/useIsMobile";
import FacetRail from "./FacetRail";
import type { SectionRailState } from "./useSectionRail";

export interface SectionSiderRailProps {
  rail: SectionRailState;
  /** The result count for the current state, when known (the head line). */
  total?: number | null;
  /** The section's own rows are still loading (ANDed with the option lists' own loading flag). */
  loading?: boolean;
  title?: string;
  /** Drawn instead of the rail — a section that has no filters on the view on screen (Books' Directory). */
  note?: ReactNode;
}

export default function SectionSiderRail({ rail, total, loading, title, note }: SectionSiderRailProps) {
  const isMobile = useIsMobile();
  if (note != null) return <div className="bx-rail-on-sider bx-rail-note">{note}</div>;
  return (
    <div className="bx-rail-on-sider">
      <FacetRail
        search={isMobile}
        title={title}
        spec={rail.spec}
        state={rail.state}
        actions={rail.actions}
        activeCount={rail.activeCount}
        facets={rail.facets.data}
        facetsLoading={rail.facets.isLoading || !!loading}
        total={total ?? null}
        grouped={rail.grouped}
        saved={{
          list: rail.saved.list,
          onApply: rail.actions.replaceSearch,
          onRemove: rail.saved.remove,
          onSave: rail.saveCurrent,
        }}
      />
    </div>
  );
}
