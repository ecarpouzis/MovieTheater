import type { CatalogViewState } from "../state/useCatalogView";
import type { CatalogSource } from "../types";
import type { HoverEffect, MetadataMode } from "../tweaks/useTweaks";

/** What the host hands every view. Views read tweaks as VALUES; they never touch storage. */
export interface ViewProps {
  source: CatalogSource;
  state: CatalogViewState;
  coverScale: number;
  metadata: MetadataMode;
  hover: HoverEffect;
  /** The per-card class for the current hover effect ("" for dim/none) — ONE source of truth. */
  hoverClass: string;
}
