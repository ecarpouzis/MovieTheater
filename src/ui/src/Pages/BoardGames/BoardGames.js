import { useState, useEffect } from "react";
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
    howToPlayVideoUrls: parseJsonArray(game.howToPlayVideoUrlsJson ?? game.HowToPlayVideoUrlsJson) ?? game.howToPlayVideoUrls ?? game.HowToPlayVideoUrls ?? [],
    imageUrl: details?.imageUrl ?? details?.ImageUrl ?? null,
    imageVersion: details?.imageVersion ?? details?.ImageVersion ?? null,
  };
}

function extractGames(payload) {
  const rawGames = Array.isArray(payload) ? payload : (Array.isArray(payload?.value) ? payload.value : []);
  return rawGames.map(normalizeGame).filter((g) => Number.isInteger(g.id) && g.id > 0);
}

function BoardGames({ userData }) {
  const [games, setGames] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [selectedGameId, setSelectedGameId] = useState(null);
  const [isModalVisible, setIsModalVisible] = useState(false);
  const location = useLocation();

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetch("/odata/Boardgames?$select=id,bggThingId,name,yearPublished,minPlayers,maxPlayers,playingTime,minPlayTime,maxPlayTime,minAge,averageRating,averageWeight,description,rulesPdfUrlsJson,rulesPdfCandidateUrlsJson,howToPlayVideoUrlsJson&$expand=imageDetails&$orderby=name", {
      signal: controller.signal,
    })
      .then((r) => {
        if (!r.ok) throw new Error(`Failed to load boardgames (${r.status})`);
        return r.json();
      })
      .then((data) => {
        setGames(extractGames(data));
        setLoading(false);
      })
      .catch((err) => {
        if (err.name === "AbortError") return;
        setGames([]);
        setError(err.message || "Failed to load boardgames");
        setLoading(false);
      });

    return () => controller.abort();
  }, []);

  const params = new URLSearchParams(location.search);
  const mode = params.get("mode");
  const value = params.get("value") || "";
  const playersParam = params.get("players");
  const ageParam = params.get("age");
  const timeParam = params.get("time");
  const sortParam = params.get("sort");

  let displayGames = games;

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
      displayGames = displayGames.filter((g) => g.maxPlayers == null || g.maxPlayers >= 8);
    } else {
      displayGames = displayGames.filter((g) =>
        (g.minPlayers == null || g.minPlayers <= p) &&
        (g.maxPlayers == null || g.maxPlayers >= p)
      );
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
    setGames((prev) => prev.map((g) => (g.id === updated.id ? updated : g)));
  };

  if (loading) return <span>Loading</span>;
  if (error) return <span>{error}</span>;

  return (
    <>
      {displayGames.length === 0 ? (
        <span>No boardgames found.</span>
      ) : (
        <BoardGameCardList games={displayGames} onGameClick={handleOpenGame} />
      )}
      <BoardGameModal
        gameId={selectedGameId}
        open={isModalVisible}
        onClose={handleCloseModal}
        games={games}
        userData={userData}
        onGameUpdated={handleGameUpdated}
      />
    </>
  );
}

export default BoardGames;
