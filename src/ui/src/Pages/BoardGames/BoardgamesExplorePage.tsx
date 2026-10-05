/**
 * `/boardgames/explore` — the Board Games section's landing (R9 S7).
 *
 * The cheapest Explore on the site: the section already ships its whole catalog to the browser
 * (`useBoardgamesCatalog` — one shared React-Query copy of `/odata/Boardgames` + the facet rows,
 * seeded from `boardgames_v1` in localStorage), so this tab makes NO request of its own. Every rail
 * is a projection of rows the browse is holding anyway, and arriving here from the browse paints
 * from memory.
 *
 * A card opens the section's own sheet at `/boardgames?game=<id>`; a designer card lands on
 * `/boardgames?f=designer:<name>` — the rail URL contract.
 */
import { useCallback, useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import ExploreTab from "../../catalog/explore/ExploreTab";
import { FACET_GROUP_KINDS } from "../../catalog/explore/mapExplore";
import type { CardGroup, CardItem } from "../../catalog/types";
import useBoardgamesCatalog from "./useBoardgamesCatalog";
import { BOARDGAMES_UNSEEDED_RAILS, boardgameFacetHref, boardgameHeroDetail, composeBoardgamesExplore } from "./boardgamesExplore";

const RAIL_SUBTITLES: Record<string, string> = {
  "ways-in": "Start from who's at the table, how long you have, or how it plays",
  top: "By BGG rating, best first",
  recent: "The most recent arrivals in the collection",
  quick: "Three short lists, rolled fresh with every shuffle",
  random: "A shuffled handful of the collection",
};

export function readSeed(search: string): number {
  const raw = new URLSearchParams(search).get("seed");
  if (raw && /^[0-9]{1,9}$/.test(raw)) {
    const n = Number(raw);
    if (Number.isSafeInteger(n) && n > 0) return n;
  }
  return 1;
}

export default function BoardgamesExplorePage() {
  const history = useHistory();
  const location = useLocation();
  const seed = readSeed(location.search);
  const catalog = useBoardgamesCatalog();

  // The doors are cut from rows already in memory, so a tab switch is a re-render, not a fetch.
  const [doorAxis, setDoorAxis] = useState("players");
  const onAxis = useCallback((_rail: string, axis: string) => setDoorAxis(axis), []);
  const data = useMemo(
    () => composeBoardgamesExplore({ games: catalog.games, facetsById: catalog.facetsById, doorAxis, seed }),
    [catalog.games, catalog.facetsById, doorAxis, seed],
  );

  const onSeed = useCallback((next: number) => {
    const p = new URLSearchParams(location.search);
    p.set("seed", String(next));
    history.push({ pathname: location.pathname, search: `?${p.toString()}` });
  }, [history, location.pathname, location.search]);

  const onOpen = useCallback((item: CardItem) => {
    history.push(`/boardgames?game=${item.id}`);
  }, [history]);

  const onOpenGroup = useCallback((group: CardGroup, groupBy: string) => {
    if (groupBy !== "person") return;
    history.push(boardgameFacetHref("designer", group.key));
  }, [history]);

  return (
    <div className="boardgames-explore">
      <ExploreTab
        data={catalog.loading && catalog.games.length === 0 ? null : data}
        loading={catalog.loading}
        error={catalog.error && catalog.games.length === 0 ? new Error("boardgames") : undefined}
        onSeed={onSeed}
        onOpen={onOpen}
        onOpenGroup={onOpenGroup}
        groupKinds={FACET_GROUP_KINDS}
        moreHref={(href) => href || null}
        unseededRails={BOARDGAMES_UNSEEDED_RAILS}
        railSubtitle={(rail) => RAIL_SUBTITLES[rail.key]}
        heroEyebrow="Game night"
        heroDetail={boardgameHeroDetail}
        onAxis={onAxis}
        emptyMessage="No games in the collection yet."
      />
    </div>
  );
}
