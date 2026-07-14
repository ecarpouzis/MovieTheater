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
 * The cover's box: as tall as `height` allows, as wide as its own shape implies — but never wider
 * than `maxWidth`, in which case the WIDTH becomes the binding constraint and the height comes down
 * to match, keeping the aspect exact.
 *
 * Both bounds are load-bearing and neither may become a percentage of the card:
 *
 *  - The height must be a CONSTANT, not the card's height. A percentage height inside a flex item of
 *    indefinite height resolves to `auto` — i.e. the image's intrinsic size — so the art ends up
 *    sizing the card that was supposed to be sizing the art. That circular sizing is what blew the
 *    cards apart: covers rendered at their natural pixel size, drove the card's height, and took
 *    whatever width their aspect implied.
 *  - The width must be capped, because width follows from height × aspect, and a 4:3 cartridge box is
 *    twice as wide as a 3:4 jewel case at the same height. Uncapped, a landscape cover squeezes the
 *    details column until the title has nowhere to go.
 *
 * Returns exact pixel dimensions, so the box is always precisely the art: no cropping, no letterbox.
 */
export function coverBox(aspect, height, maxWidth) {
  const a = aspect > 0 ? aspect : FALLBACK_ASPECT;
  let h = height;
  let w = height * a;
  if (maxWidth && w > maxWidth) {
    w = maxWidth;
    h = maxWidth / a;
  }
  return { width: `${Math.round(w)}px`, height: `${Math.round(h)}px` };
}

/**
 * The smart box-art tile (design README → "Box art: natural aspect, uniform height").
 *
 * Real covers have wildly different shapes — PlayStation jewel cases are portrait (~3:4), SNES and
 * Master System boxes are landscape (~4:3). Rather than crop, letterbox, or mat every cover into one
 * poster shape, each renders at its TRUE ratio pinned to one shared height; the width follows. The
 * tile sits on the left of a horizontal card, so its variable width is simply absorbed — the details
 * column flexes into whatever is left, and every card stays the same total width.
 *
 * `height` is the shared pin (160px in the lobby grid, 64px for the smaller Live-rooms thumbnail) and
 * `maxWidth` caps how far a landscape cover may grow across — see coverBox above for why both are
 * needed, and why neither may be expressed relative to the card.
 */
function GameCover({ game, artId, height, maxWidth, className = "" }) {
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
        style={coverBox(FALLBACK_ASPECT, height, maxWidth)}>
        <span className="arcade-cover__label">{game.title}</span>
      </div>
    );
  }

  return (
    <div className={`arcade-cover ${className}`} style={coverBox(aspect, height, maxWidth)}>
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
