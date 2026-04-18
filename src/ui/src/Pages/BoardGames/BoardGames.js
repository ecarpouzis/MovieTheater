import { useState, useEffect } from "react";
import { useLocation } from "react-router-dom";
import BoardGameCardList from "./BoardGameCardList";
import BoardGameModal from "./BoardGameModal";

function normalizeGame(game) {
  return {
    id: game.id ?? game.Id,
    bggThingId: game.bggThingId ?? game.BggThingId,
    name: game.name ?? game.Name,
    yearPublished: game.yearPublished ?? game.YearPublished,
    minPlayers: game.minPlayers ?? game.MinPlayers,
    maxPlayers: game.maxPlayers ?? game.MaxPlayers,
    playingTime: game.playingTime ?? game.PlayingTime,
    minAge: game.minAge ?? game.MinAge,
    averageRating: game.averageRating ?? game.AverageRating,
    averageWeight: game.averageWeight ?? game.AverageWeight,
    description: game.description ?? game.Description,
  };
}

function extractGames(payload) {
  const rawGames = Array.isArray(payload) ? payload : (Array.isArray(payload?.value) ? payload.value : []);
  return rawGames.map(normalizeGame).filter((g) => Number.isInteger(g.id) && g.id > 0);
}

function BoardGames() {
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

    fetch("/odata/Boardgames?$select=id,bggThingId,name,yearPublished,minPlayers,maxPlayers,playingTime,minAge,averageRating,averageWeight,description&$orderby=name", {
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

  let displayGames = games;
  if (mode === "title" && value.trim()) {
    const q = value.trim().toLowerCase();
    displayGames = games.filter((g) => g.name?.toLowerCase().includes(q));
  } else if (mode === "letter" && value.trim()) {
    const letter = value.trim().toUpperCase();
    if (letter === "#") {
      displayGames = games.filter((g) => g.name && !/^[A-Z]/i.test(g.name));
    } else {
      displayGames = games.filter((g) => g.name?.toUpperCase().startsWith(letter));
    }
  }

  const handleOpenGame = (gameId) => {
    setSelectedGameId(gameId);
    setIsModalVisible(true);
  };

  const handleCloseModal = () => {
    setIsModalVisible(false);
    setSelectedGameId(null);
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
      />
    </>
  );
}

export default BoardGames;
