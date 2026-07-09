import { useCallback, useState } from "react";
import { canHaveArt } from "./arcadeSystems";
import { getCoverAspect, rememberCoverAspect } from "./coverAspect";

// The shape we reserve for a cover we've never measured. Box art has no width/height in the DB, so
// the first paint of a never-seen cover has to guess *something*; 3:4 is the jewel-case shape. The
// guess is only ever a reservation — the image is drawn `object-fit: contain`, so a cover that turns
// out to be landscape letterboxes for one frame rather than cropping, then the box snaps to its true
// ratio the instant `onLoad` reports the natural size.
const FALLBACK_ASPECT = 3 / 4;

/**
 * The smart box-art tile (design README → "Box art: natural aspect, uniform height").
 *
 * Real covers have wildly different shapes — PlayStation jewel cases are portrait (~3:4), SNES and
 * Master System boxes are landscape (~4:3). Rather than crop, letterbox, or mat every cover into one
 * poster shape, each renders at its TRUE ratio pinned to one shared height; the width follows. The
 * tile sits on the left of a horizontal card, so its variable width is simply absorbed — the details
 * column flexes into whatever is left, and every card stays the same total width.
 *
 * `height` is the shared pin: 118px in the grid, 64px for the smaller Live-rooms thumbnail.
 */
function GameCover({ game, artId, height, className = "" }) {
  const id = artId ?? game?.artId;
  const [aspect, setAspect] = useState(() => getCoverAspect(id));
  const [broken, setBroken] = useState(() => !canHaveArt(game));

  const onLoad = useCallback((e) => {
    const { naturalWidth, naturalHeight } = e.currentTarget;
    const measured = rememberCoverAspect(id, naturalWidth, naturalHeight);
    if (measured) setAspect(measured);
  }, [id]);

  if (broken) {
    return (
      <div className={`arcade-cover arcade-cover--empty ${className}`}
        style={{ height, aspectRatio: String(FALLBACK_ASPECT) }}>
        <span className="arcade-cover__label">{game.title}</span>
      </div>
    );
  }

  return (
    <div className={`arcade-cover ${className}`} style={{ height, aspectRatio: String(aspect || FALLBACK_ASPECT) }}>
      <img
        className="arcade-cover__img"
        src={`/ArcadeImage/${id}`}
        alt=""
        loading="lazy"
        decoding="async"
        onLoad={onLoad}
        onError={() => setBroken(true)}
      />
    </div>
  );
}

export default GameCover;
