import { useState, useEffect, useMemo } from "react";
import { useLocation } from "react-router-dom";
import BoardGameCardList from "./BoardGameCardList";
import BoardGameModal from "./BoardGameModal";

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

function BoardGames({ userData }) {
  const [allGames, setAllGames] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [selectedGameId, setSelectedGameId] = useState(null);
  const [isModalVisible, setIsModalVisible] = useState(false);
  const location = useLocation();

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetch("/odata/Boardgames?$select=id,bggThingId,name,yearPublished,minPlayers,maxPlayers,playingTime,minPlayTime,maxPlayTime,minAge,averageRating,averageWeight,description,rulesPdfUrlsJson,rulesPdfCandidateUrlsJson,howToPlayVideoUrlsJson,thingType,baseGameId&$expand=imageDetails&$orderby=name", {
      signal: controller.signal,
    })
      .then((r) => {
        if (!r.ok) throw new Error(`Failed to load boardgames (${r.status})`);
        return r.json();
      })
      .then((data) => {
        setAllGames(extractGames(data));
        setLoading(false);
      })
      .catch((err) => {
        if (err.name === "AbortError") return;
        setAllGames([]);
        setError(err.message || "Failed to load boardgames");
        setLoading(false);
      });

    return () => controller.abort();
  }, []);

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
  const sortParam = params.get("sort");

  const showExpansions = userData?.showBoardgameExpansions ?? false;
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

  const handleOpenGame = (gameId) => {
    setSelectedGameId(gameId);
    setIsModalVisible(true);
  };

  const handleCloseModal = () => {
    setIsModalVisible(false);
    setSelectedGameId(null);
  };

  const handleGameUpdated = (rawData) => {
    const updated = normalizeGame(rawData);
    setAllGames((prev) => prev.map((g) => (g.id === updated.id ? updated : g)));
  };

  if (loading) return <span>Loading</span>;
  if (error) return <span>{error}</span>;

  return (
    <>
      {displayGames.length === 0 ? (
        <span>No boardgames found.</span>
      ) : (
        <BoardGameCardList games={displayGames} expansionMap={expansionMap} onGameClick={handleOpenGame} />
      )}
      <BoardGameModal
        gameId={selectedGameId}
        open={isModalVisible}
        onClose={handleCloseModal}
        games={allGames}
        expansionMap={expansionMap}
        userData={userData}
        onGameUpdated={handleGameUpdated}
        onOpenGame={setSelectedGameId}
      />
    </>
  );
}

export default BoardGames;
