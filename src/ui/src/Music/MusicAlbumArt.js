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
  eager = false,
}) {
  const [failed, setFailed] = useState(false);
  useEffect(() => { setFailed(false); }, [albumId, hasArt]);

  // Ask whenever there IS an album — not only when the catalog already believes it has art.
  //
  // The image route fills art ON DEMAND (MusicImageController): a miss either answers instantly from
  // its negative cache, or takes the single non-blocking remote slot and usually serves the artwork
  // in that same request. Gating this on `hasArt` meant nothing ever asked for a newly ingested
  // album's art, so the lazy fill could never run and the album stayed a tile forever — six freshly
  // ingested albums showed initials while their covers sat one request away.
  //
  // Cheap when it misses: at most one remote lookup is in flight globally, every other miss is an
  // immediate 404, and each album is only ever checked once (ArtCheckedUtc is a durable negative
  // cache). getMusicAlbumArt's hasArt parameter is built for exactly this — it appends ?v= only once
  // art is known, so a card that gains art later stops being served the browser-cached 404.
  const showArt = albumId != null && !failed;
  const style = dominantColor
    ? { background: dominantColor }
    : { background: `hsl(${tileHue(title)}, 32%, 38%)` };

  if (showArt) {
    const src = thumb
      ? MovieAPI.getMusicAlbumArtThumb(albumId, hasArt)
      : MovieAPI.getMusicAlbumArt(albumId, hasArt);
    return (
      <img
        className={`${className} ${className}--art`}
        style={style}
        src={src}
        alt=""
        loading={eager ? "eager" : "lazy"}
        decoding="async"
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
