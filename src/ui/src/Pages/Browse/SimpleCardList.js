import { MovieAPI } from "../../MovieAPI";
import { Card, List } from "antd";
import { useState, useRef } from "react";
import UserMovieOptions from "./UserMovieOptions";

const listStyle = {
  width: "100%",
  height: "100%",
  padding: "5px",
};

const cardPosterStyle = {
  height: "100%",
  width: "100%",
  objectFit: "contain",
};

const cardTitleStyle = {
  fontWeight: "bold",
  fontFamily: "Arial Black",
  color: "#5E5E5E",
  width: "100%",
  textAlign: "center",
  fontSize: "13px",
  marginTop: "0px",
  marginBottom: "0px",
  display: "-webkit-box",
  WebkitLineClamp: "2",
  WebkitBoxOrient: "vertical",
  lineHeight: "1.3",
  overflow: "hidden",
  textOverflow: "ellipsis",
  wordBreak: "break-word",
  maxHeight: "34px",
  flex: "0 0 34px",
};

const cardMetaStyle = {
  textAlign: "center",
  fontSize: "11px",
  color: "#888",
  marginTop: "2px",
  marginBottom: "0px",
  height: "16px",
  overflow: "hidden",
  textOverflow: "ellipsis",
  whiteSpace: "nowrap",
  flex: "0 0 16px",
};

const baseCardBodyStyle = {
  padding: "4px 8px",
  display: "flex",
  flexDirection: "column",
  userSelect: "none",
  height: "100%",
  overflow: "hidden",
  gap: "0",
};

const posterContainer = {
  width: "100%",
  height: "200px",
  overflow: "hidden",
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  backgroundColor: "#f0f0f0",
  flex: "0 0 auto",
};

const buttonContainerStyle = {
  display: "flex",
  justifyContent: "center",
  gap: "0px",
  marginTop: "auto",
  padding: "4px 8px",
  backgroundColor: "#f5f5f5",
  borderRadius: "6px",
  flex: "0 0 auto",
};

function SimpleCardList({ movieDataArray, userData, setUserData, onMovieClick, onToggleViewing }) {
  const [hoveredMovieId, setHoveredMovieId] = useState(null);
  const hoverTimeoutRef = useRef(null);

  const handleTitleTouchStart = (id) => {
    if (hoverTimeoutRef.current) clearTimeout(hoverTimeoutRef.current);
    setHoveredMovieId(id);
  };

  const handleTitleTouchEnd = () => {
    hoverTimeoutRef.current = setTimeout(() => setHoveredMovieId(null), 2000);
  };

  return (
    <List
      style={listStyle}
      grid={{ gutter: 4, column: 2 }}
      dataSource={movieDataArray}
      renderItem={(item, index) => {
        const thumbUrl = MovieAPI.getPosterThumbnail(item.id, item.posterVersion);
        const isAboveFold = index < 6;
        return (
          <List.Item>
            <Card
              className="mobile-movie-card"
              bodyStyle={baseCardBodyStyle}
              style={{
                border: "1px solid #d9d9d9",
                width: "100%",
                height: "320px",
                display: "flex",
                flexDirection: "column",
                overflow: "hidden",
              }}
            >
              <div style={posterContainer}>
                <img
                  style={cardPosterStyle}
                  alt={item.title}
                  src={thumbUrl}
                  loading={isAboveFold ? "eager" : "lazy"}
                  fetchPriority={isAboveFold ? "high" : "auto"}
                  decoding="async"
                />
              </div>
              <div
                onClick={() => onMovieClick(item.id, item.kind)}
                onMouseEnter={() => setHoveredMovieId(item.id)}
                onMouseLeave={() => setHoveredMovieId(null)}
                onTouchStart={() => handleTitleTouchStart(item.id)}
                onTouchEnd={handleTitleTouchEnd}
                style={{ cursor: "pointer", display: "flex", flexDirection: "column", flex: "0 0 auto" }}
              >
                <div style={{ ...cardTitleStyle, color: hoveredMovieId === item.id ? "#1890ff" : "#5E5E5E" }}>{item.title}</div>
                <div style={cardMetaStyle}>
                  {new Date(item.releaseDate).getFullYear()} � {item.rating} � {item.runtime}
                </div>
              </div>
              <div style={buttonContainerStyle}>
                <UserMovieOptions userData={userData} id={item.id} kind={item.kind} setUserData={setUserData} onToggleViewing={onToggleViewing} inline={true} />
              </div>
            </Card>
          </List.Item>
        );
      }}
    />
  );
}

export default SimpleCardList;
