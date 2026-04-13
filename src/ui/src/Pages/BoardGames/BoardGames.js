import { useState } from "react";
import SimpleBoardGameCardList from "./SimpleBoardGameCardList";
import SimpleBoardGameModal from "./SimpleBoardGameModal";
import useIsMobile from "../../hooks/useIsMobile";

function getPlaceholderImage(title = "Board Game") {
  const safeTitle = encodeURIComponent(title);
  return `data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='400' height='400' viewBox='0 0 400 400'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0%' stop-color='%23e6f4ff'/><stop offset='100%' stop-color='%23f0f0f0'/></linearGradient></defs><rect width='400' height='400' fill='url(%23g)'/><circle cx='200' cy='150' r='48' fill='%231677ff' fill-opacity='0.2'/><text x='200' y='160' text-anchor='middle' font-family='Arial, sans-serif' font-size='44' fill='%231677ff'>🎲</text><text x='200' y='250' text-anchor='middle' font-family='Arial, sans-serif' font-size='20' fill='%23555'>${safeTitle}</text><text x='200' y='280' text-anchor='middle' font-family='Arial, sans-serif' font-size='14' fill='%23888'>Image coming soon</text></svg>`;
}

function BoardGames({ userData, setUserData }) {
  const isMobile = useIsMobile();
  const [selectedGameId, setSelectedGameId] = useState(null);
  const [isModalVisible, setIsModalVisible] = useState(false);

  // Sample board game data - replace with API call later
  const boardGameDataArray = [
    {
      id: 1,
      title: "Catan",
      releaseDate: "1995-01-01",
      players: "3-4",
      ageRange: "10-100",
      playtime: "60-120 min",
      complexity: "Medium",
      thumbnailUrl: "https://via.placeholder.com/200x200?text=Catan",
      placeholderImage: getPlaceholderImage("Catan"),
    },
    {
      id: 2,
      title: "Ticket to Ride",
      releaseDate: "2004-01-01",
      players: "2-5",
      ageRange: "8-100",
      playtime: "30-60 min",
      complexity: "Easy",
      thumbnailUrl: "https://via.placeholder.com/200x200?text=Ticket+to+Ride",
      placeholderImage: getPlaceholderImage("Ticket to Ride"),
    },
    {
      id: 3,
      title: "Pandemic",
      releaseDate: "2008-01-01",
      players: "2-4",
      ageRange: "8-100",
      playtime: "45 min",
      complexity: "Medium",
      thumbnailUrl: "https://via.placeholder.com/200x200?text=Pandemic",
      placeholderImage: getPlaceholderImage("Pandemic"),
    },
    {
      id: 4,
      title: "Carcassonne",
      releaseDate: "2000-01-01",
      players: "2-5",
      ageRange: "7-100",
      playtime: "30-45 min",
      complexity: "Easy",
      thumbnailUrl: "https://via.placeholder.com/200x200?text=Carcassonne",
      placeholderImage: getPlaceholderImage("Carcassonne"),
    },
    {
      id: 5,
      title: "7 Wonders",
      releaseDate: "2010-01-01",
      players: "2-7",
      ageRange: "10-100",
      playtime: "30 min",
      complexity: "Medium",
      thumbnailUrl: "https://via.placeholder.com/200x200?text=7+Wonders",
      placeholderImage: getPlaceholderImage("7 Wonders"),
    },
    {
      id: 6,
      title: "Dominion",
      releaseDate: "2008-01-01",
      players: "2-4",
      ageRange: "13-100",
      playtime: "30 min",
      complexity: "Medium",
      thumbnailUrl: "https://via.placeholder.com/200x200?text=Dominion",
      placeholderImage: getPlaceholderImage("Dominion"),
    },
  ];

  const handleOpenGame = (gameId) => {
    setSelectedGameId(gameId);
    setIsModalVisible(true);
  };

  const handleCloseModal = () => {
    setIsModalVisible(false);
    setSelectedGameId(null);
  };

  return (
    <>
      <SimpleBoardGameCardList gameDataArray={boardGameDataArray} userData={userData} setUserData={setUserData} onGameClick={handleOpenGame} />
      <SimpleBoardGameModal gameId={selectedGameId} open={isModalVisible} onClose={handleCloseModal} gameDataArray={boardGameDataArray} />
    </>
  );
}

export default BoardGames;
