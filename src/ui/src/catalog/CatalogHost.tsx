import { useState, type ReactNode } from "react";
import "./styles/catalog-views.css";
import "./styles/catalog-grouped.css";
import "./styles/catalog-shelves.css";
import ViewSwitcher from "./ViewSwitcher";
import useMiddleDragScroll from "./engine/useMiddleDragScroll";
import useCatalogView from "./state/useCatalogView";
import TweaksPanel, { TweakRow, TweakToggle } from "./tweaks/TweaksPanel";
import useTweaks, { hoverClass } from "./tweaks/useTweaks";
import type { CatalogSource, DirectoryNode, ViewMode } from "./types";
import DirectoryView from "./views/DirectoryView";
import ExtendedView from "./views/ExtendedView";
import GridView from "./views/GridView";
import ListView from "./views/ListView";
import NewspaperView from "./views/NewspaperView";
import ShelvesView from "./views/ShelvesView";
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
export const AVAILABLE_VIEWS: readonly ViewMode[] = ["grid", "wall", "list", "extended", "shelf", "newspaper", "directory"];

const VIEWS: Partial<Record<ViewMode, (p: ViewProps) => JSX.Element>> = {
  grid: GridView,
  wall: WallView,
  list: ListView,
  extended: ExtendedView,
  shelf: ShelvesView,
  newspaper: NewspaperView,
};

export interface CatalogHostProps {
  /** Storage/URL scope: "movies", "music", … */
  section: string;
  source: CatalogSource;
  overrides?: Partial<Record<ViewMode, ReactNode>>;
  /** Anything to show at the left of the switcher row (a count, a scope title). */
  leading?: ReactNode;
  className?: string;
  /** Directory view: start drilled into these nodes (a section's "Browse this folder"). */
  directoryStart?: DirectoryNode[];
}

export default function CatalogHost({ section, source, overrides, leading, className, directoryStart }: CatalogHostProps) {
  const { state, setView, setGroup, setItems, setSort } = useCatalogView(section, source, AVAILABLE_VIEWS);
  const { tweaks, update, setCoverScale, setExtra, coverScale } = useTweaks(section);
  const [tweaksOpen, setTweaksOpen] = useState(false);
  useMiddleDragScroll();
  const scale = coverScale(state.view);
  const hc = hoverClass(tweaks.hover);
  const override = overrides?.[state.view];
  const viewProps: ViewProps = { source, state, coverScale: scale, metadata: tweaks.metadata, hover: tweaks.hover, hoverClass: hc };
  const View = VIEWS[state.view];
  let content: ReactNode = null;
  if (override != null) content = override;
  else if (state.view === "directory") content = <DirectoryView {...viewProps} showEmpty={tweaks.showEmptyFolders} initialStack={directoryStart} />;
  else if (View) content = <View {...viewProps} />;

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
        {content}
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
        >
          {state.view === "directory" && (
            <>
              <div className="twk-sect">Directory</div>
              <TweakRow label="Show empty folders" inline>
                <TweakToggle on={tweaks.showEmptyFolders} onChange={(showEmptyFolders) => update({ showEmptyFolders })} label="Show empty folders" />
              </TweakRow>
            </>
          )}
        </TweaksPanel>
      )}
    </div>
  );
}
