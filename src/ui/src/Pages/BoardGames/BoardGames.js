import { useEffect, useMemo, useCallback } from "react";
import { useHistory, useLocation } from "react-router-dom";
import BoardGameCard, { NO_EXPANSIONS } from "./BoardGameCard";
import BoardGameModal from "./BoardGameModal";
import useIsMobile from "../../hooks/useIsMobile";
import useTouchDevice from "../../hooks/useTouchDevice";
import LoadFailure from "../../Components/LoadFailure";
import CardGridSkeleton from "../../Components/CardGridSkeleton";
import CatalogHost from "../../catalog/CatalogHost";
import { hasFacetValue } from "../../catalog/rail/facetSpec";
import { facetStateKey } from "../../catalog/rail/facetUrl";
import useSectionRail from "../../catalog/rail/useSectionRail";
import sectionRailSurfaces from "../../catalog/rail/sectionRailSurfaces";
import { createBoardgamesSource } from "../../catalog/sources/boardgamesSource";
import { DRILL_NEXT_GROUP, LINK_FACETS, RANGE_GROUP_KEYS, legacyToBoardgamesSearch, rangeForGroup, sortBoardgames } from "./boardgamesFacetSpec";
import useBoardgamesBrowse, { BOARDGAMES_ENTITY_PARAMS, useBoardgamesResults } from "./useBoardgamesBrowse";
import { normalizeGame } from "./useBoardgamesCatalog";

const LINK_FACET_KEYS = new Set(LINK_FACETS.map((f) => f.key));

function BoardGames({ userData }) {
  const tooltipTrigger = useTouchDevice() ? "click" : "hover";
  // The catalog is ONE shared resource (React Query, seeded from the localStorage cache): the sider
  // rail reads the same rows, so its counts and this list always agree.
  const browse = useBoardgamesBrowse(userData);
  const { games: allGames, expansionMap, facetsById, loading, setGames } = browse;
  const history = useHistory();
  const location = useLocation();
  const isMobile = useIsMobile();

  // ── The facet rail's state (R9 S2c): the URL is the filter (`q/f/x/y` + the `a/t/w` ranges). ──
  const rail = useSectionRail("boardgames", browse.spec, { entityParams: BOARDGAMES_ENTITY_PARAMS });
  const facetState = rail.state;
  const facetActions = rail.actions;

  // A pre-S2c link (?players=&age=&time=&mode=title&value= — the old rail's Selects, old bookmarks)
  // is rewritten ONCE into the facet form it means; the page re-renders on the new URL.
  useEffect(() => {
    const legacy = legacyToBoardgamesSearch(location.search);
    if (legacy != null) history.replace({ pathname: location.pathname, search: legacy, state: location.state });
  }, [history, location.pathname, location.search, location.state]);

  // The open game modal lives in the URL (?game=<internal id> — the browse ?title= pattern):
  // a card click pushes it (Back closes the full-page modal), ✕ replaces it away, and the link
  // is shareable/reload-safe. The catalog is client-side, so a cold load just finds the row.
  const selectedGameId = (() => {
    const raw = new URLSearchParams(location.search).get("game");
    if (!raw || !/^[0-9]+$/.test(raw)) return null;
    const n = Number(raw);
    return Number.isSafeInteger(n) && n > 0 ? n : null;
  })();
  const isModalVisible = selectedGameId != null;

  // `name` is the catalog switcher's name for the default order (the server's $orderby=name) — the
  // same list as no sort at all, letters included.
  const rawSort = new URLSearchParams(location.search).get("sort");
  const sortParam = rawSort && rawSort !== "name" ? rawSort : null;

  // Names what makes the grid a DIFFERENT list — the card list's window resets on it.
  const listKey = `${facetStateKey(facetState)}:${sortParam ?? ""}:${browse.showExpansions}`;

  // The facet state over the reachable scope, then the sort — both memoized (the whole list used to
  // be re-filtered and re-sorted in the render body on every render).
  const results = useBoardgamesResults(browse, facetState);
  const displayGames = useMemo(() => sortBoardgames(results, sortParam), [results, sortParam]);

  const handleOpenGame = useCallback((gameId) => {
    const p = new URLSearchParams(history.location.search);
    p.set("game", String(gameId));
    history.push({ pathname: "/boardgames", search: `?${p.toString()}` });
  }, [history]);

  // Switching the modal between a base and its expansions re-points the SAME open sheet — replace,
  // so flag-flipping doesn't grow the history.
  const handleSwitchGame = useCallback((gameId) => {
    const p = new URLSearchParams(history.location.search);
    p.set("game", String(gameId));
    history.replace({ pathname: "/boardgames", search: `?${p.toString()}` });
  }, [history]);

  const handleCloseModal = useCallback(() => {
    const p = new URLSearchParams(history.location.search);
    if (!p.has("game")) return;
    p.delete("game");
    const search = p.toString();
    history.replace({ pathname: "/boardgames", search: search ? `?${search}` : "" });
  }, [history]);

  // A group header in the package views scopes in place (adds its facet, decade or range) and drills
  // to the next axis — one push. Since R9 S8 a Players shelf is ONE exact count (the axis is
  // range-aware), so it drills into the Players facet like any other; the three ladder axes drill
  // into their own two-thumb range (`a=`/`t=`/`w=`). Rating tier and Base-or-expansion have no facet
  // to become, so their headers only regroup — a header that cannot scope does not pretend to.
  const handleOpenGroup = useCallback((group, groupBy) => {
    const next = { group: DRILL_NEXT_GROUP[groupBy] ?? null };
    if (LINK_FACET_KEYS.has(groupBy) || groupBy === "players") {
      facetActions.apply((d) => {
        if (!hasFacetValue(d.include[groupBy], group.key)) d.include[groupBy] = [...(d.include[groupBy] ?? []), group.key];
      }, next);
    } else if (groupBy === "decade") {
      const d = Number(group.key);
      if (Number.isFinite(d)) facetActions.apply((s) => { s.yearMin = d; s.yearMax = d + 9; }, next);
    } else if (RANGE_GROUP_KEYS.has(groupBy)) {
      const span = rangeForGroup(groupBy, group.key);
      if (span) facetActions.apply((s) => { s.ranges = { ...s.ranges, [groupBy]: span }; }, next);
    }
  }, [facetActions]);

  // The Grid lays THIS section's card into the shared bands (R9 S3): BoardGameCard is a
  // MODULE-LEVEL component (the BandSlot memo law), reached through a renderer whose identity
  // changes only when something a card draws changes — the expansion map, the tooltip trigger.
  const renderCard = useCallback((item, view) => (
    <BoardGameCard
      game={item.raw}
      expansions={expansionMap?.[item.id] ?? NO_EXPANSIONS}
      tooltipTrigger={tooltipTrigger}
      metadata={view.metadata}
      hoverClass={view.hoverClass}
      eager={view.eager}
      onGameClick={handleOpenGame}
    />
  ), [expansionMap, tooltipTrigger, handleOpenGame]);

  // ONE engine under every view: the grid is the package's GridView over InfiniteBands, drawing the
  // card above; Wall / List / Extended / Shelves / Newspaper / Directory read the same source.
  const source = useMemo(
    () => createBoardgamesSource({ games: displayGames, expansionMap, facetsById, listKey, currentSort: sortParam ?? "name", onOpen: handleOpenGame, onOpenGroup: handleOpenGroup, renderCard }),
    [displayGames, expansionMap, facetsById, listKey, sortParam, handleOpenGame, handleOpenGroup, renderCard]
  );

  const handleGameUpdated = (rawData) => {
    const updated = normalizeGame(rawData);
    setGames((prev) => prev.map((g) => (g.id === updated.id ? updated : g)));
  };

  if (loading) {
    // Same first-paint convention as the movie grid: skeleton cards in the real layout, not a
    // lone spinner.
    return <CardGridSkeleton />;
  }
  if (browse.error && allGames.length === 0) {
    return <LoadFailure message="Couldn't load the board games." onRetry={browse.refresh} />;
  }

  // The rail itself is the sider's BoardgamesSiderRail, which carries the count on its head line —
  // and on a phone that sider IS the drawer. The page's share: the chips over the results and, on
  // desktop, the bar's SmartSearch.
  const { chips, surfaces } = sectionRailSurfaces(rail, isMobile, {
    placeholder: "A game, mechanic:Deck, designer:Knizia…",
  });

  return (
    <>
      {surfaces}
      <CatalogHost section="boardgames" source={source} beforeResults={chips} />
      <BoardGameModal
        gameId={selectedGameId}
        open={isModalVisible}
        onClose={handleCloseModal}
        games={allGames}
        expansionMap={expansionMap}
        userData={userData}
        onGameUpdated={handleGameUpdated}
        onOpenGame={handleSwitchGame}
      />
    </>
  );
}

export default BoardGames;
