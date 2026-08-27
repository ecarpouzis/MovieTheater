import { useEffect, useMemo, useCallback } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Empty } from "antd";
import BoardGameCardList from "./BoardGameCardList";
import BoardGameModal from "./BoardGameModal";
import { bucketsFor } from "../../Components/CatalogPager";
import LoadFailure from "../../Components/LoadFailure";
import CardGridSkeleton from "../../Components/CardGridSkeleton";
import CatalogHost from "../../catalog/CatalogHost";
import { BarSearchSlot } from "../../catalog/bar/BarSearch";
import FacetRail from "../../catalog/rail/FacetRail";
import FilterPill from "../../catalog/rail/FilterPill";
import RailChips from "../../catalog/rail/RailChips";
import SmartSearch from "../../catalog/rail/SmartSearch";
import { hasFacetValue } from "../../catalog/rail/facetSpec";
import { facetStateKey } from "../../catalog/rail/facetUrl";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import useRailSheet from "../../catalog/rail/useRailSheet";
import { isGroupedBrowse } from "../../catalog/state/useCatalogView";
import { createBoardgamesSource } from "../../catalog/sources/boardgamesSource";
import { DRILL_NEXT_GROUP, LINK_FACETS, legacyToBoardgamesSearch, sortBoardgames } from "./boardgamesFacetSpec";
import useBoardgamesBrowse, { BOARDGAMES_ENTITY_PARAMS, useBoardgamesResults } from "./useBoardgamesBrowse";
import { normalizeGame } from "./useBoardgamesCatalog";

const LINK_FACET_KEYS = new Set(LINK_FACETS.map((f) => f.key));

function BoardGames({ userData }) {
  // The catalog is ONE shared resource (React Query, seeded from the localStorage cache): the sider
  // rail reads the same rows, so its counts and this list always agree.
  const browse = useBoardgamesBrowse(userData);
  const { games: allGames, expansionMap, facetsById, loading, setGames } = browse;
  const history = useHistory();
  const location = useLocation();

  // ── The facet rail's state (R9 S2c): the URL is the filter (`q/f/x/y` + the `a/t/w` ranges). ──
  const spec = browse.spec;
  const { state: facetState, actions: facetActions, activeCount } = useFacetState(spec, { entityParams: BOARDGAMES_ENTITY_PARAMS });
  const facets = useFacetOptions(spec);
  const sheet = useRailSheet();
  const grouped = isGroupedBrowse(location.search, "boardgames");
  const saved = useSavedSearches("boardgames");
  const saveCurrent = useCallback((name) => saved.save(name, savableSearch(location.search, BOARDGAMES_ENTITY_PARAMS)), [saved, location.search]);

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

  // A–Z quick-scroll buckets — only under the default alphabetical order, where a letter jump
  // means anything (the server list arrives $orderby=name).
  const letters = useMemo(
    () => (sortParam ? null : bucketsFor(displayGames, (g) => g.name || "")),
    [displayGames, sortParam]
  );

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

  // A group header in the package views scopes in place (adds its facet or decade) and drills to
  // the next axis — one push. Player buckets are ranges of counts, not one count: they only regroup.
  const handleOpenGroup = useCallback((group, groupBy) => {
    if (LINK_FACET_KEYS.has(groupBy)) {
      facetActions.apply((d) => {
        if (!hasFacetValue(d.include[groupBy], group.key)) d.include[groupBy] = [...(d.include[groupBy] ?? []), group.key];
      }, { group: DRILL_NEXT_GROUP[groupBy] ?? null });
    } else if (groupBy === "decade") {
      const d = Number(group.key);
      if (Number.isFinite(d)) facetActions.apply((s) => { s.yearMin = d; s.yearMax = d + 9; }, { group: DRILL_NEXT_GROUP.decade });
    }
  }, [facetActions]);

  // The catalog views (Wall / List / Extended / Shelves / Newspaper / Directory) over the SAME list
  // the grid shows; the grid itself stays BoardGameCardList (the host's `grid` override).
  const source = useMemo(
    () => createBoardgamesSource({ games: displayGames, expansionMap, facetsById, listKey, currentSort: sortParam ?? "name", onOpen: handleOpenGame, onOpenGroup: handleOpenGroup }),
    [displayGames, expansionMap, facetsById, listKey, sortParam, handleOpenGame, handleOpenGroup]
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

  const grid = displayGames.length === 0 ? (
    <Empty description="No board games match." />
  ) : (
    <BoardGameCardList
      games={displayGames}
      expansionMap={expansionMap}
      onGameClick={handleOpenGame}
      listKey={listKey}
      letters={letters}
    />
  );

  // The bar's tools: the phone's Filters pill raising the full-page sheet (the desktop rail is the
  // sider's BoardgamesSiderRail, which carries the count on its head line).
  const filtersPill = sheet.isMobile ? <FilterPill count={activeCount} onClick={sheet.show} /> : null;
  const chips = (
    <RailChips spec={spec} state={facetState} actions={facetActions} facets={facets.data} activeCount={activeCount} onSave={saveCurrent} />
  );

  return (
    <>
      {/* The SmartSearch in the SectionBar's centre box (R9 S1d/S2c): text = `q`, a token = a facet. */}
      {!sheet.isMobile && (
        <BarSearchSlot>
          <SmartSearch spec={spec} facets={facets.data} onAdd={facetActions.add} onText={facetActions.setText} placeholder="A game, mechanic:Deck, designer:Knizia…" />
        </BarSearchSlot>
      )}
      {sheet.isMobile && (
        <FacetRail
          variant="sheet"
          open={sheet.open}
          onClose={sheet.hide}
          spec={spec}
          state={facetState}
          actions={facetActions}
          activeCount={activeCount}
          facets={facets.data}
          facetsLoading={facets.isLoading}
          total={displayGames.length}
          grouped={grouped}
          saved={{ list: saved.list, onApply: facetActions.replaceSearch, onRemove: saved.remove, onSave: saveCurrent }}
        />
      )}
      <CatalogHost section="boardgames" source={source} overrides={{ grid }} tools={filtersPill} beforeResults={chips} />
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
