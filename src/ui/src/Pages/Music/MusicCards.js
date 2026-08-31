import { memo } from "react";
import MusicAlbumArt from "../../Music/MusicAlbumArt";

/**
 * The music grids' cards — the section's own tiles, laid out by the catalog package's Grid (R9 S3:
 * `CatalogSource.renderCard`). Unchanged in what they draw; what is new is the tweak contract:
 * `bx-card` + the host's hover class on the root, `bx-cover` around the square art (so Hover and
 * Rounded apply through catalog-views.css), the tile sized off the Grid's `--cell`, and
 * `metadata: "minimal"` dropping the sub-line under the title.
 *
 * They are also used OUTSIDE the catalog, on the `?artist=` drill page's own short album list —
 * hence the plain defaults when no tweak values are passed.
 */

/** The Grid's base tile size for Music before the cover-size tweak (the old `minmax(150px, 1fr)`). */
export const MUSIC_GRID_CELL = 150;

/**
 * True when the artist line would just repeat the title.
 *
 * A compilation that sits at the library root has no artist folder above it, so ingest makes the
 * folder both the artist and the album — "(The Microcosm) Visionary Music of Continental Europe,
 * 1970-1986", "1970s Algerian Proto-Rai Underground". The data is right; printing it twice is not.
 * Compared on a fold rather than exactly, because the two strings reach the card down different
 * paths and differ in case and punctuation more often than you would think.
 */
export function artistRepeatsTitle(album) {
  // The apostrophe is dropped rather than treated as a break: it is elision, so "80's Symphonic"
  // and "80s Symphonic" are one name, where splitting on it would make them "80 s" and "80s".
  const fold = (s) => String(s ?? "").toLowerCase().replace(/['’]/g, "").replace(/[^a-z0-9]+/g, " ").trim();
  const artist = fold(album?.artistName);
  return artist.length > 0 && artist === fold(album?.title);
}

/**
 * The score in the corner of a tile.
 *
 * It follows the house rule the album sheet already states (`AlbumScoreLine`): a RATING and the
 * POPULARITY signal are two different facts and are never merged into one number here. A rating is a
 * verdict somebody actually reached; popularity only says how widely a record is heard, and printing
 * it under a star would claim an opinion nobody has. So they get different glyphs, different weight,
 * and a title that says which is which.
 *
 * The blended 0–100 behind the "Top rated" order is deliberately NOT what is shown. On a tile it
 * reads as a verdict, and with no house ratings yet that blend is simply the popularity number
 * wearing a rating's clothes.
 *
 * Precedence is your own score, then the house's, then popularity — most personal first.
 */
export function AlbumScore({ album }) {
  const mine = typeof album?.myRating === "number" ? album.myRating : null;
  if (mine != null) {
    return (
      <span className="music-album-card-score music-album-card-score--mine" title={`Your rating: ${mine}`}>
        {"★"}{mine}
      </span>
    );
  }
  const count = album?.ratingCount ?? 0;
  if (count > 0 && typeof album.ratingAvg === "number") {
    const avg = Math.round(album.ratingAvg);
    // The count rides in the tooltip because an average of one is not an average.
    return (
      <span className="music-album-card-score" title={`${avg} from ${count} listener${count === 1 ? "" : "s"}`}>
        {"★"}{avg}
      </span>
    );
  }
  // The outside community's verdict. Its vote count matters for the same reason the house's does —
  // MusicBrainz ratings run thin — so it rides in the tooltip rather than being implied.
  if (typeof album?.externalRating === "number") {
    const votes = album.externalRatingVotes ?? 0;
    return (
      <span
        className="music-album-card-score music-album-card-score--outside"
        title={votes > 0
          ? `${album.externalRating} — rated by ${votes} ${votes === 1 ? "person" : "people"} outside this house`
          : `${album.externalRating} — rated outside this house`}
      >
        {"★"}{album.externalRating}
      </span>
    );
  }
  if (typeof album?.popularity === "number") {
    return (
      <span
        className="music-album-card-pop"
        title={`${album.popularity} popularity — how widely this record is heard, not how good it is`}
      >
        {"♪"}{album.popularity}
      </span>
    );
  }
  return null;
}

export const AlbumCard = memo(function AlbumCard({ album, onOpen, metadata, hoverClass = "", eager }) {
  return (
    <button className={`music-album-card bx-card${hoverClass ? ` ${hoverClass}` : ""}`} onClick={() => onOpen(album.id)}>
      <span className="music-cover bx-cover">
        <MusicAlbumArt
          albumId={album.id}
          hasArt={album.hasArt}
          title={album.title}
          dominantColor={album.dominantColor}
          eager={eager}
        />
      </span>
      <div className="music-album-card-title" title={album.title}>{album.title}</div>
      {metadata !== "minimal" && (
        <div className="music-album-card-sub">
          {/* A root-level compilation folder IS its own artist ("14 Tracks - Ganja Reggae"), so the
              name and the title are the same string and printing both just says it twice. */}
          {!artistRepeatsTitle(album) && (
            <span className="music-album-card-artist" title={album.artistName}>{album.artistName}</span>
          )}
          {album.year != null && <span className="music-album-card-year">{album.year}</span>}
        </div>
      )}
      {/* Always rendered, empty when the record carries neither a quality tag nor a score.
          Conditionally is what made a tagged tile taller than its untagged neighbour and left the
          grid with a ragged bottom edge. `minimal` drops the line entirely, which is uniform by
          construction. */}
      {metadata !== "minimal" && (
        <div className="music-album-card-tag">
          <span className="music-album-card-quality">{album.tag}</span>
          <AlbumScore album={album} />
        </div>
      )}
    </button>
  );
});

/** An artist wears their first album's cover (see /API/Music/Artists) — initials tile when none has art. */
export const ArtistCard = memo(function ArtistCard({ artist, onOpen, metadata, hoverClass = "", eager }) {
  return (
    <button className={`music-artist-card bx-card${hoverClass ? ` ${hoverClass}` : ""}`} onClick={() => onOpen(artist.id)}>
      <span className="music-cover bx-cover">
        <MusicAlbumArt
          albumId={artist.artAlbumId}
          hasArt={artist.hasArt}
          title={artist.name}
          dominantColor={artist.dominantColor}
          eager={eager}
        />
      </span>
      <div className="music-artist-card-name" title={artist.name}>{artist.name}</div>
      {metadata !== "minimal" && (
        <div className="music-artist-card-sub">
          {artist.yearRange && <span>{artist.yearRange}</span>}
          <span>{artist.albumCount} album{artist.albumCount === 1 ? "" : "s"}</span>
          <span>{artist.trackCount} track{artist.trackCount === 1 ? "" : "s"}</span>
        </div>
      )}
    </button>
  );
});
