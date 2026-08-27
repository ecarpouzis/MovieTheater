/**
 * A section's filter rail in the sider (desktop): the generic `FacetRail` over the section's spec,
 * reading the same URL the page reads and pushing the same URLs — nothing crosses the sider/page
 * boundary through props. The SmartSearch lives in the SectionBar's centre slot (the page mounts it
 * there), so the rail draws none; the head line carries the section's own result count.
 *
 * A section's own rail file is now just its spec, its count and this (`…SiderRail.tsx`).
 */
import type { ReactNode } from "react";
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
  if (note != null) return <div className="bx-rail-on-sider bx-rail-note">{note}</div>;
  return (
    <div className="bx-rail-on-sider">
      <FacetRail
        variant="rail"
        search={false}
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
