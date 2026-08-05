import { useEffect, useState } from "react";
import { MovieAPI } from "../MovieAPI";

// Album art square (music-plan.md §2.5), shared by the library grid, the album modal hero and the
// mini-player. Falls back to the initials tile whenever there is no art on the mount OR the image
// fails to load — the art pass covers roughly half the catalog, so the fallback is the normal case,
// not an error state. Where a dominant color exists it tints the fallback (and the art's frame), so
// even art-less cards pick up the album's palette once the remote pass fills it in.

// Stable hue per album title until real art lands: hash the title into the hue wheel.
export function tileHue(text) {
  let h = 0;
  for (let i = 0; i < (text || "").length; i++) h = (h * 31 + text.charCodeAt(i)) | 0;
  return ((h % 360) + 360) % 360;
}

export function initialsFor(title) {
  return (title || "")
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => w[0])
    .join("")
    .toUpperCase();
}

export default function MusicAlbumArt({
  albumId,
  hasArt,
  title = "",
  dominantColor,
  thumb = true,
  className = "music-album-tile",
}) {
  const [failed, setFailed] = useState(false);
  useEffect(() => { setFailed(false); }, [albumId, hasArt]);

  const showArt = hasArt && albumId != null && !failed;
  const style = dominantColor
    ? { background: dominantColor }
    : { background: `hsl(${tileHue(title)}, 32%, 38%)` };

  if (showArt) {
    const src = thumb
      ? MovieAPI.getMusicAlbumArtThumb(albumId, true)
      : MovieAPI.getMusicAlbumArt(albumId, true);
    return (
      <img
        className={`${className} ${className}--art`}
        style={style}
        src={src}
        alt=""
        loading="lazy"
        onError={() => setFailed(true)}
      />
    );
  }

  return (
    <div className={className} style={style}>
      <span>{initialsFor(title) || "♪"}</span>
    </div>
  );
}
