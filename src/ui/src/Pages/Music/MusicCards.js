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
          <span className="music-album-card-artist" title={album.artistName}>{album.artistName}</span>
          {album.year != null && <span className="music-album-card-year">{album.year}</span>}
        </div>
      )}
      {album.tag && <div className="music-album-card-tag">{album.tag}</div>}
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
