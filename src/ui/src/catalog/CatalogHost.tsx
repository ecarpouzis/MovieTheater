import { useCallback, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import "./styles/catalog-views.css";
import "./styles/catalog-grouped.css";
import "./styles/catalog-shelves.css";
import "./skin/skin.css";
import ViewSwitcher, { TweaksButton, ViewPills } from "./ViewSwitcher";
import useSlot, { BAR_TOOLS_SLOT, TOPBAR_TOOLS_SLOT } from "./bar/useSlot";
import PerfHud from "./PerfHud";
import useMiddleDragScroll from "./engine/useMiddleDragScroll";
import useIsMobile from "../hooks/useIsMobile";
import { requestSiteTheme } from "../hooks/useTheme";
import { applySectionSkin, crossFamilyPick, skinTweakExtras, useSiteTheme } from "./skin/skin";
import "./skin/sectionSkins";
import useCatalogView from "./state/useCatalogView";
import TweaksPanel, { TweakRow, TweakToggle, type TweaksPanelRows } from "./tweaks/TweaksPanel";
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
 * The one mount point a section uses: the command pills + ⚙ (portaled into the SectionBar's tools
 * slot — on phones the ⚙ goes to the top bar's generic slot), the tweaks panel, and the current
 * view over the section's CatalogSource. Tweaks are applied HERE, once — `data-hover` on the
 * results root and the shared hoverClass on every card — so no view can drift from the setting
 * (the standalone's drill view once did, and Zoom/Tilt/Dim silently stopped working there).
 *
 * `overrides` lets a section keep an existing renderer for a view (Movies keeps its CardList as
 * the Grid, untouched); the switcher still lists the view, the host just renders the override.
 * `tools` are the section's own bar controls (a phone Filters pill, Arcade's Saves/Quality); they
 * sit before the pills in the slot. Where no bar exists (a host rendered outside the app shell),
 * the pills fall back to an in-flow row above the results.
 */
export const AVAILABLE_VIEWS: readonly ViewMode[] = ["grid", "wall", "list", "extended", "shelf", "newspaper", "directory"];

/**
 * Which of the standard card tweaks REACH each view's cards. The law (catalog-views skill, "Tweaks reach
 * every view they name") is that a control that does nothing is removed, not disabled — a lever that
 * visibly changes nothing is a bug the smoke fails on. Grid/Extended honour all four; the Wall drops
 * Rounded (out-specified by the zero-gap mosaic) and Under-the-cover (no meta strip at rest); the List,
 * Shelves and Newspaper draw no `.bx-card` at all, so only Cover size (their own scale) applies; the
 * Directory's tiles wear `bx-card`/`bx-cover`, so Hover and Rounded reach them and Under-the-cover reaches
 * its loose items. Omitted keys mean "shown".
 */
export const VIEW_TWEAK_ROWS: Partial<Record<ViewMode, TweaksPanelRows>> = {
  wall: { rounded: false, metadata: false },
  list: { hover: false, rounded: false, metadata: false },
  shelf: { hover: false, rounded: false, metadata: false },
  newspaper: { hover: false, rounded: false, metadata: false },
};

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
  /** The section's own bar tools, placed before the pills in the bar's tools slot. */
  tools?: ReactNode;
  className?: string;
  /** Directory view: start drilled into these nodes (a section's "Browse this folder"). */
  directoryStart?: DirectoryNode[];
  /** Between the bar and the results (a section's active-filter chips). */
  beforeResults?: ReactNode;
}

export default function CatalogHost({ section, source, overrides, tools, className, directoryStart, beforeResults }: CatalogHostProps) {
  const { state, setView, setGroup, setItems, setSort } = useCatalogView(section, source, AVAILABLE_VIEWS);
  const { tweaks, update, setCoverScale, setExtra, coverScale } = useTweaks(section);
  const [tweaksOpen, setTweaksOpen] = useState(false);
  const isMobile = useIsMobile();
  const hostRef = useRef<HTMLDivElement>(null);
  // The section SKIN (catalog/skin): the registered backdrop + type rows go into the ⚙ panel, and
  // the chosen token set is written ONCE on this root — never per card. A cross-family pick (a
  // dark backdrop while the site is light) asks the site to switch theme, so no swatch is inert.
  const theme = useSiteTheme();
  const skinExtras = useMemo(() => skinTweakExtras(section, theme), [section, theme]);
  const extras = useMemo(
    () => (source.tweakExtras?.length ? [...skinExtras, ...source.tweakExtras] : skinExtras),
    [skinExtras, source.tweakExtras],
  );
  useLayoutEffect(() => {
    applySectionSkin(hostRef.current, section, tweaks.extras, theme, state.view);
  }, [section, tweaks.extras, theme, state.view]);
  const chooseExtra = useCallback((key: string, value: string) => {
    setExtra(key, value);
    const family = crossFamilyPick(section, key, value, theme);
    if (family) requestSiteTheme(family);
  }, [section, setExtra, theme]);
  const pillsSlot = useSlot(BAR_TOOLS_SLOT);
  const gearSlot = useSlot(isMobile ? TOPBAR_TOOLS_SLOT : BAR_TOOLS_SLOT);
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

  const pillProps = { state, source, available: AVAILABLE_VIEWS, onView: setView, onGroup: setGroup, onItems: setItems, onSort: setSort };
  const toggleTweaks = () => setTweaksOpen((o) => !o);

  let chrome: ReactNode;
  if (pillsSlot) {
    chrome = (
      <>
        {createPortal(<>{tools}<ViewPills {...pillProps} /></>, pillsSlot)}
        {gearSlot && gearSlot !== pillsSlot
          ? createPortal(<TweaksButton open={tweaksOpen} onToggle={toggleTweaks} />, gearSlot)
          : createPortal(<TweaksButton open={tweaksOpen} onToggle={toggleTweaks} />, pillsSlot)}
      </>
    );
  } else {
    chrome = <ViewSwitcher {...pillProps} tweaksOpen={tweaksOpen} onTweaks={toggleTweaks} leading={tools} />;
  }

  return (
    <div ref={hostRef} className={`bx-host${className ? ` ${className}` : ""}`} data-view={state.view} data-section={section}>
      {chrome}
      {beforeResults}
      <div className={`bx-results ${tweaks.rounded ? "bx-rounded" : "bx-sharp"}`} data-hover={tweaks.hover} data-view={state.view} data-skin={source.shelvesSkin ?? "bookcase"}>
        {content}
      </div>
      {tweaksOpen && (
        <TweaksPanel
          view={state.view}
          tweaks={tweaks}
          coverScale={scale}
          onCoverScale={(v) => setCoverScale(state.view, v)}
          onChange={update}
          onExtra={chooseExtra}
          extras={extras}
          rows={VIEW_TWEAK_ROWS[state.view]}
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
      {/* The perf HUD, mounted once wherever a catalog is. Renders nothing and installs nothing
          unless localStorage["catalog.perfhud.v1"] is set — see PerfHud.tsx. */}
      <PerfHud />
    </div>
  );
}
