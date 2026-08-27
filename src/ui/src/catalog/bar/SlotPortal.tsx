/**
 * Mount children in one of the bar's portal slots by id — nothing where the slot is absent (phones
 * for the search slot, tests). `BarSearchSlot` and `BarToolsSlot` are the two named forms; a page
 * with no CatalogHost (the dense Seen/Want browse, a section root without a catalog) uses
 * `BarToolsSlot` to contribute its own tools (the phone's Filters pill) to the same seam the host's
 * pills ride.
 */
import { createPortal } from "react-dom";
import useSlot, { BAR_SEARCH_SLOT, BAR_TOOLS_SLOT } from "./useSlot";

export default function SlotPortal({ id, children }: { id: string; children: React.ReactNode }) {
  const slot = useSlot(id);
  if (!slot) return null;
  return createPortal(children, slot);
}

/** Mounts any search control (a section's SmartSearch) in the bar's search slot. */
export function BarSearchSlot({ children }: { children: React.ReactNode }) {
  return <SlotPortal id={BAR_SEARCH_SLOT}>{children}</SlotPortal>;
}

/** Mounts page tools in the bar's tools slot when no CatalogHost is on the page to carry them. */
export function BarToolsSlot({ children }: { children: React.ReactNode }) {
  return <SlotPortal id={BAR_TOOLS_SLOT}>{children}</SlotPortal>;
}
