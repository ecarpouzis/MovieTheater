/**
 * The ⚙ for a page that has NO CatalogHost (R9 S5). Every catalog page's tweaks button rides the
 * SectionBar's tools slot; a page that draws its own cards — the Books Shelf — used to grow a
 * bespoke control instead (a "Size" slider bolted onto its header), which is exactly the
 * two-websites seam this phase is closing. It gets the same ⚙ in the same slot and the same panel,
 * showing only the rows that actually reach its cards.
 *
 * The panel is rendered inside a `.bx-host` wrapper because its stylesheet paints from the
 * package's local aliases (`--chrome/--ink/--bg/--line`), which that class declares.
 */
import { useState } from "react";
import { BarToolsSlot } from "../bar/SlotPortal";
import { TweaksButton } from "../ViewSwitcher";
import "../styles/catalog-views.css";
import type { ViewMode } from "../types";
import TweaksPanel, { type TweaksPanelRows } from "./TweaksPanel";
import useTweaks from "./useTweaks";

export interface PageTweaksToolProps {
  /** The tweaks store (`catalog.tweaks.v1:<section>`) — a page may keep its own. */
  section: string;
  /** Which view's cover scale this page reads. */
  view: ViewMode;
  rows?: TweaksPanelRows;
  footNote?: string;
}

export default function PageTweaksTool({ section, view, rows, footNote }: PageTweaksToolProps) {
  const { tweaks, update, setCoverScale, setExtra, coverScale } = useTweaks(section);
  const [open, setOpen] = useState(false);
  return (
    <>
      <BarToolsSlot><TweaksButton open={open} onToggle={() => setOpen((o) => !o)} /></BarToolsSlot>
      {open && (
        <div className="bx-host bx-host--tools">
          <TweaksPanel
            view={view}
            tweaks={tweaks}
            coverScale={coverScale(view)}
            onCoverScale={(v) => setCoverScale(view, v)}
            onChange={update}
            onExtra={setExtra}
            onClose={() => setOpen(false)}
            rows={rows}
            footNote={footNote}
          />
        </div>
      )}
    </>
  );
}
