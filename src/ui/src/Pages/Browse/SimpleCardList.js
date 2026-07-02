import { MovieAPI } from "../../MovieAPI";
import { Card, List } from "antd";
import { memo, useCallback, useEffect, useMemo, useRef, useState } from "react";
import UserMovieOptions, { useViewingToggles } from "./UserMovieOptions";
import { preloadImages } from "../../preloadImages";

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

// One mobile grid card. Memoized on PRIMITIVES + stable handlers so a Seen/Want toggle, an
// infinite-scroll append, or hovering a sibling card doesn't re-render the whole grid. The hover
// state that highlights the title lives INSIDE the card now (was a list-level state that re-rendered
// every card on any hover).
const SimpleMovieCard = memo(function SimpleMovieCard({
  item,
  isAboveFold,
  showOptions,
  isWatched,
  isWanted,
  onMovieClick,
  onToggleSeen,
  onToggleWant,
}) {
  const [hovered, setHovered] = useState(false);
  const hoverTimeoutRef = useRef(null);

  const isMisc = item.kind === "misc";
  const thumbUrl = MovieAPI.getPosterThumbnail(item.id, item.posterVersion, item.kind);
  const year = item.releaseDate ? new Date(item.releaseDate).getFullYear() : null;
  const metaText = isMisc
    ? [year, item.category || "Misc"].filter(Boolean).join(" • ")
    : [year, item.rating, item.runtime].filter(Boolean).join(" • ");

  const handleTitleTouchStart = () => {
    if (hoverTimeoutRef.current) clearTimeout(hoverTimeoutRef.current);
    setHovered(true);
  };
  const handleTitleTouchEnd = () => {
    hoverTimeoutRef.current = setTimeout(() => setHovered(false), 2000);
  };

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
            onError={(e) => { e.currentTarget.style.display = "none"; }}
          />
        </div>
        <div
          onClick={isMisc ? undefined : () => onMovieClick(item.id, item.kind)}
          onMouseEnter={() => !isMisc && setHovered(true)}
          onMouseLeave={() => setHovered(false)}
          onTouchStart={() => !isMisc && handleTitleTouchStart()}
          onTouchEnd={handleTitleTouchEnd}
          style={{ cursor: isMisc ? "default" : "pointer", display: "flex", flexDirection: "column", flex: "0 0 auto" }}
        >
          <div style={{ ...cardTitleStyle, color: hovered ? "#1890ff" : "#5E5E5E" }}>{item.title}</div>
          <div style={cardMetaStyle}>{metaText}</div>
        </div>
        {!isMisc && showOptions && (
          <div style={buttonContainerStyle}>
            <UserMovieOptions
              id={item.id}
              kind={item.kind}
              isWatched={isWatched}
              isWanted={isWanted}
              onToggleSeen={onToggleSeen}
              onToggleWant={onToggleWant}
              inline={true}
            />
          </div>
        )}
      </Card>
    </List.Item>
  );
});

function SimpleCardList({ movieDataArray, userData, setUserData, onMovieClick, onToggleViewing }) {
  const { toggleSeen, toggleWant } = useViewingToggles(userData, setUserData, onToggleViewing);
  const handleMovieClick = useCallback((id, kind) => onMovieClick(id, kind), [onMovieClick]);
  const seenSet = useMemo(() => new Set(userData?.moviesSeen), [userData?.moviesSeen]);
  const wantSet = useMemo(() => new Set(userData?.moviesToWatch), [userData?.moviesToWatch]);

  // Preload every loaded card's poster thumbnail as soon as the page data arrives (deduped), so the
  // below-the-fold lazy <img>s render from cache instead of snapping in when scrolled to. Bounded by
  // what infinite-scroll has loaded; a new page adds only its new thumbs.
  useEffect(() => {
    preloadImages((movieDataArray || []).map((m) => MovieAPI.getPosterThumbnail(m.id, m.posterVersion, m.kind)));
  }, [movieDataArray]);

  return (
    <List
      style={listStyle}
      grid={{ gutter: 4, column: 2 }}
      dataSource={movieDataArray}
      renderItem={(item, index) => (
        <SimpleMovieCard
          item={item}
          isAboveFold={index < 6}
          showOptions={!!userData}
          isWatched={userData ? seenSet.has(item.id) : false}
          isWanted={userData ? wantSet.has(item.id) : false}
          onMovieClick={handleMovieClick}
          onToggleSeen={toggleSeen}
          onToggleWant={toggleWant}
        />
      )}
    />
  );
}

export default SimpleCardList;
