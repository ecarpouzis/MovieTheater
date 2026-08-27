/**
 * What the Boardgames rail surfaces share (the page, the sider rail, the phone sheet): the cached
 * catalog, the scope the viewer can reach (expansions folded away unless the setting shows them),
 * the facet spec over that scope, and the result count for a facet state — all in memory, so the
 * sider and the page agree without anything crossing the sider/page boundary through props.
 */
import { useMemo } from "react";
import type { FacetState } from "../../catalog/rail/facetSpec";
import { applyBoardgameFacets, boardgamesFacetSpec, type BoardgameFacetData } from "./boardgamesFacetSpec";
import useBoardgamesCatalog, { type BoardgameGame, type BoardgamesCatalog } from "./useBoardgamesCatalog";

export const BOARDGAMES_ENTITY_PARAMS = ["game"] as const;

export interface BoardgamesViewer {
  showBoardgameExpansions?: boolean | null;
}

export interface BoardgamesBrowse extends BoardgamesCatalog {
  /** The rows the browse can reach: every game, or base games only (expansions ride their base's card). */
  scope: BoardgameGame[];
  showExpansions: boolean;
  spec: ReturnType<typeof boardgamesFacetSpec>;
  data: BoardgameFacetData;
}

/** True for the rows the browse lists when expansions are folded away. */
export const isBaseGame = (g: BoardgameGame): boolean => g.thingType !== "boardgameexpansion" && g.baseGameId == null;

export default function useBoardgamesBrowse(viewer: BoardgamesViewer | null | undefined): BoardgamesBrowse {
  const catalog = useBoardgamesCatalog();
  const showExpansions = !!viewer?.showBoardgameExpansions;
  const scope = useMemo(() => (showExpansions ? catalog.games : catalog.games.filter(isBaseGame)), [catalog.games, showExpansions]);
  const data = useMemo<BoardgameFacetData>(() => ({ expansionMap: catalog.expansionMap, facetsById: catalog.facetsById }), [catalog.expansionMap, catalog.facetsById]);
  const spec = useMemo(() => boardgamesFacetSpec(`${catalog.version}:${showExpansions ? "x" : "b"}`, scope, data), [catalog.version, showExpansions, scope, data]);
  return { ...catalog, scope, showExpansions, spec, data };
}

/** The games a state keeps over the scope — the page's list before its sort, the rail's count. */
export function useBoardgamesResults(browse: Pick<BoardgamesBrowse, "scope" | "data">, state: FacetState): BoardgameGame[] {
  return useMemo(() => applyBoardgameFacets(browse.scope, state, browse.data) as BoardgameGame[], [browse.scope, browse.data, state]);
}
