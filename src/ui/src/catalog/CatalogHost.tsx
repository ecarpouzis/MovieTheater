import { useState, type ReactNode } from "react";
import "./styles/catalog-views.css";
import ViewSwitcher from "./ViewSwitcher";
import useCatalogView from "./state/useCatalogView";
import TweaksPanel from "./tweaks/TweaksPanel";
import useTweaks, { hoverClass } from "./tweaks/useTweaks";
import type { CatalogSource, ViewMode } from "./types";
import GridView from "./views/GridView";
import ListView from "./views/ListView";
import type { ViewProps } from "./views/ViewProps";
import WallView from "./views/WallView";

/**
 * The one mount point a section uses: the switcher row, the tweaks panel, and the current view over
 * the section's CatalogSource. Tweaks are applied HERE, once — `data-hover` on the results root and
 * the shared hoverClass on every card — so no view can drift from the setting (the standalone's
 * drill view once did, and Zoom/Tilt/Dim silently stopped working there).
 *
 * `overrides` lets a section keep an existing renderer for a view (Movies keeps its CardList as
 * the Grid, untouched); the switcher still lists the view, the host just renders the override.
 */
export const AVAILABLE_VIEWS: readonly ViewMode[] = ["grid", "wall", "list"];

const VIEWS: Partial<Record<ViewMode, (p: ViewProps) => JSX.Element>> = {
  grid: GridView,
  wall: WallView,
  list: ListView,
};

export interface CatalogHostProps {
  /** Storage/URL scope: "movies", "music", … */
  section: string;
  source: CatalogSource;
  overrides?: Partial<Record<ViewMode, ReactNode>>;
  /** Anything to show at the left of the switcher row (a count, a scope title). */
  leading?: ReactNode;
  className?: string;
}

export default function CatalogHost({ section, source, overrides, leading, className }: CatalogHostProps) {
  const { state, setView, setGroup, setItems, setSort } = useCatalogView(section, source, AVAILABLE_VIEWS);
  const { tweaks, update, setCoverScale, setExtra, coverScale } = useTweaks(section);
  const [tweaksOpen, setTweaksOpen] = useState(false);
  const scale = coverScale(state.view);
  const hc = hoverClass(tweaks.hover);
  const View = VIEWS[state.view];
  const override = overrides?.[state.view];

  return (
    <div className={`bx-host${className ? ` ${className}` : ""}`} data-view={state.view} data-section={section}>
      <ViewSwitcher
        state={state}
        source={source}
        available={AVAILABLE_VIEWS}
        onView={setView}
        onGroup={setGroup}
        onItems={setItems}
        onSort={setSort}
        tweaksOpen={tweaksOpen}
        onTweaks={() => setTweaksOpen((o) => !o)}
        leading={leading}
      />
      <div className={`bx-results ${tweaks.rounded ? "bx-rounded" : "bx-sharp"}`} data-hover={tweaks.hover} data-view={state.view}>
        {override ?? (View
          ? <View source={source} state={state} coverScale={scale} metadata={tweaks.metadata} hover={tweaks.hover} hoverClass={hc} />
          : null)}
      </div>
      {tweaksOpen && (
        <TweaksPanel
          view={state.view}
          tweaks={tweaks}
          coverScale={scale}
          onCoverScale={(v) => setCoverScale(state.view, v)}
          onChange={update}
          onExtra={setExtra}
          extras={source.tweakExtras}
          onClose={() => setTweaksOpen(false)}
        />
      )}
    </div>
  );
}
