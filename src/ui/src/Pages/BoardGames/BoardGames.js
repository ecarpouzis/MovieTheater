import { useState, useEffect, useMemo, useCallback } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Empty } from "antd";
import BoardGameCardList from "./BoardGameCardList";
import BoardGameModal from "./BoardGameModal";
import { bucketsFor } from "../../Components/CatalogPager";
import LoadFailure from "../../Components/LoadFailure";
import useCachedResource from "../../hooks/useCachedResource";
import CardGridSkeleton from "../../Components/CardGridSkeleton";
import CatalogHost from "../../catalog/CatalogHost";
import { createBoardgamesSource, facetsMap } from "../../catalog/sources/boardgamesSource";

function parseJsonArray(json) {
  if (!json) return null;
  try { const v = JSON.parse(json); return Array.isArray(v) ? v : null; } catch { return null; }
}

function parsePdfEntries(json) {
  const arr = parseJsonArray(json);
  if (!arr) return null;
  return arr.map((e) => typeof e === "string" ? { url: e, name: null } : { url: e.Url ?? e.url ?? "", name: e.Name ?? e.name ?? null });
}

function normalizeGame(game) {
  const details = game.imageDetails ?? game.ImageDetails ?? null;
  return {
    id: game.id ?? game.Id,
    bggThingId: game.bggThingId ?? game.BggThingId,
    name: game.name ?? game.Name,
    yearPublished: game.yearPublished ?? game.YearPublished,
    minPlayers: game.minPlayers ?? game.MinPlayers,
    maxPlayers: game.maxPlayers ?? game.MaxPlayers,
    playingTime: game.playingTime ?? game.PlayingTime,
    minPlayTime: game.minPlayTime ?? game.MinPlayTime,
    maxPlayTime: game.maxPlayTime ?? game.MaxPlayTime,
    minAge: game.minAge ?? game.MinAge,
    averageRating: game.averageRating ?? game.AverageRating,
    averageWeight: game.averageWeight ?? game.AverageWeight,
    description: game.description ?? game.Description,
    rulesPdfUrls: parsePdfEntries(game.rulesPdfUrlsJson ?? game.RulesPdfUrlsJson) ?? game.rulesPdfUrls ?? game.RulesPdfUrls ?? [],
    rulesPdfCandidateUrls: parseJsonArray(game.rulesPdfCandidateUrlsJson ?? game.RulesPdfCandidateUrlsJson) ?? game.rulesPdfCandidateUrls ?? game.RulesPdfCandidateUrls ?? [],
    howToPlayVideoUrlsJson: game.howToPlayVideoUrlsJson ?? game.HowToPlayVideoUrlsJson ?? null,
    howToPlayVideoUrls: (parseJsonArray(game.howToPlayVideoUrlsJson ?? game.HowToPlayVideoUrlsJson) ?? game.howToPlayVideoUrls ?? game.HowToPlayVideoUrls ?? [])
      .map((e) => (typeof e === "string" ? e : e.Url ?? e.url ?? "")).filter(Boolean),
    imageUrl: details?.imageUrl ?? details?.ImageUrl ?? null,
    imageVersion: details?.imageVersion ?? details?.ImageVersion ?? null,
    thingType: game.thingType ?? game.ThingType ?? null,
    baseGameId: game.baseGameId ?? game.BaseGameId ?? null,
  };
}

function extractGames(payload) {
  const rawGames = Array.isArray(payload) ? payload : (Array.isArray(payload?.value) ? payload.value : []);
  return rawGames.map(normalizeGame).filter((g) => Number.isInteger(g.id) && g.id > 0);
}

const CACHE_KEY = "boardgames_v1";

function BoardGames({ userData }) {
  // The render-from-cache-then-background-refresh this page pioneered, now the shared hook.
  const catalog = useCachedResource(CACHE_KEY, (signal) =>
    fetch("/odata/Boardgames?$select=id,bggThingId,name,yearPublished,minPlayers,maxPlayers,playingTime,minPlayTime,maxPlayTime,minAge,averageRating,averageWeight,description,rulesPdfUrlsJson,rulesPdfCandidateUrlsJson,howToPlayVideoUrlsJson,thingType,baseGameId&$expand=imageDetails&$orderby=name", {
      signal,
    })
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => (data == null ? null : extractGames(data)))
      .catch((err) => (err?.name === "AbortError" ? null : null))
  );
  const allGames = catalog.data ?? [];
  const setAllGames = catalog.setData;
  const loading = catalog.loading;
  // Publisher / family / designer / category / mechanic per game (parsed server-side out of the BGG
  // links) — the catalog views' group modes. Cached like the catalog; the grid never needs it.
  const facets = useCachedResource("boardgames_facets_v1", (signal) =>
    fetch("/API/Boardgames/Facets", { signal })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => (Array.isArray(d?.items) ? d.items : null))
      .catch(() => null)
  );
  const facetsById = useMemo(() => facetsMap(facets.data), [facets.data]);
  const history = useHistory();
  const location = useLocation();

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

  const expansionMap = useMemo(() => {
    const map = {};
    for (const g of allGames) {
      if (g.baseGameId != null) {
        if (!map[g.baseGameId]) map[g.baseGameId] = [];
        map[g.baseGameId].push(g);
      }
    }
    return map;
  }, [allGames]);

  const params = new URLSearchParams(location.search);
  const mode = params.get("mode");
  const value = params.get("value") || "";
  const playersParam = params.get("players");
  const ageParam = params.get("age");
  const timeParam = params.get("time");
  // `name` is the catalog switcher's name for the default order (the server's $orderby=name) — the
  // same list as no sort at all, letters included.
  const rawSort = params.get("sort");
  const sortParam = rawSort && rawSort !== "name" ? rawSort : null;

  const showExpansions = userData?.showBoardgameExpansions ?? false;
  // Names what makes the grid a DIFFERENT list — the card list's window resets on it.
  const listKey = `${mode ?? ""}:${value}:${playersParam ?? ""}:${ageParam ?? ""}:${timeParam ?? ""}:${sortParam ?? ""}:${showExpansions}`;

  // Memoized: the six sort branches and four filter passes used to run in the render body on
  // every render (each sort spreading + resorting the whole list).
  const displayGames = useMemo(() => {
  let displayGames = showExpansions
    ? allGames
    : allGames.filter((g) => g.thingType !== "boardgameexpansion" && g.baseGameId == null);

  if (mode === "title" && value.trim()) {
    const q = value.trim().toLowerCase();
    displayGames = displayGames.filter((g) => g.name?.toLowerCase().includes(q));
  } else if (mode === "letter" && value.trim()) {
    const letter = value.trim().toUpperCase();
    if (letter === "#") {
      displayGames = displayGames.filter((g) => g.name && !/^[A-Z]/i.test(g.name));
    } else {
      displayGames = displayGames.filter((g) => g.name?.toUpperCase().startsWith(letter));
    }
  }

  if (playersParam) {
    const p = parseInt(playersParam, 10);
    if (p === 8) {
      displayGames = displayGames.filter((g) => {
        if (g.maxPlayers == null || g.maxPlayers >= 8) return true;
        return (expansionMap[g.id] ?? []).some((e) => e.maxPlayers == null || e.maxPlayers >= 8);
      });
    } else {
      displayGames = displayGames.filter((g) => {
        const gameMatches =
          (g.minPlayers == null || g.minPlayers <= p) &&
          (g.maxPlayers == null || g.maxPlayers >= p);
        if (gameMatches) return true;
        return (expansionMap[g.id] ?? []).some(
          (e) => (e.minPlayers == null || e.minPlayers <= p) && (e.maxPlayers == null || e.maxPlayers >= p)
        );
      });
    }
  }

  if (ageParam) {
    const a = parseInt(ageParam, 10);
    displayGames = displayGames.filter((g) => g.minAge == null || g.minAge <= a);
  }

  if (timeParam) {
    const t = parseInt(timeParam, 10);
    displayGames = displayGames.filter((g) => {
      const min = g.minPlayTime ?? g.playingTime;
      return min == null || min <= t;
    });
  }

  const getPlayTimeSortValue = (game) => game.minPlayTime ?? game.playingTime ?? game.maxPlayTime ?? 0;
  const getRatingSortValue = (game) => game.averageRating ?? 0;

  if (sortParam === "play_time_asc") {
    displayGames = [...displayGames].sort((a, b) => getPlayTimeSortValue(a) - getPlayTimeSortValue(b));
  } else if (sortParam === "play_time_desc") {
    displayGames = [...displayGames].sort((a, b) => getPlayTimeSortValue(b) - getPlayTimeSortValue(a));
  } else if (sortParam === "rating_asc") {
    displayGames = [...displayGames].sort((a, b) => getRatingSortValue(a) - getRatingSortValue(b));
  } else if (sortParam === "rating_desc") {
    displayGames = [...displayGames].sort((a, b) => getRatingSortValue(b) - getRatingSortValue(a));
  } else if (sortParam === "complexity_asc") {
    displayGames = [...displayGames].sort((a, b) => (a.averageWeight ?? 0) - (b.averageWeight ?? 0));
  } else if (sortParam === "complexity_desc") {
    displayGames = [...displayGames].sort((a, b) => (b.averageWeight ?? 0) - (a.averageWeight ?? 0));
  }

  return displayGames;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [allGames, expansionMap, showExpansions, mode, value, playersParam, ageParam, timeParam, sortParam]);

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

  // The catalog views (Wall / List / Extended / Shelves / Newspaper / Directory) over the SAME list
  // the grid shows; the grid itself stays BoardGameCardList (the host's `grid` override).
  const source = useMemo(
    () => createBoardgamesSource({ games: displayGames, expansionMap, facetsById, listKey, currentSort: sortParam ?? "name", onOpen: handleOpenGame }),
    [displayGames, expansionMap, facetsById, listKey, sortParam, handleOpenGame]
  );

  const handleGameUpdated = (rawData) => {
    const updated = normalizeGame(rawData);
    setAllGames((prev) => prev.map((g) => (g.id === updated.id ? updated : g)));
  };

  if (loading) {
    // Same first-paint convention as the movie grid: skeleton cards in the real layout, not a
    // lone spinner.
    return <CardGridSkeleton />;
  }
  if (catalog.error && allGames.length === 0) {
    return <LoadFailure message="Couldn't load the board games." onRetry={catalog.refresh} />;
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

  return (
    <>
      <CatalogHost section="boardgames" source={source} overrides={{ grid }} />
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
