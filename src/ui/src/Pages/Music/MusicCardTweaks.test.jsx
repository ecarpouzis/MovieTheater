import { render, waitFor } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import GridView from "../../catalog/views/GridView";
import { MUSIC_GRID_CELL, createMusicSource } from "../../catalog/sources/musicSource";
import { AlbumCard, ArtistCard } from "./MusicCards";

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getMusicAlbumArt: (id) => `/MusicAlbumArt?id=${id}`,
    getMusicAlbumArtThumb: (id) => `/MusicAlbumArtThumb?id=${id}`,
  },
}));

/**
 * R9 S3 for Music: the album and artist tiles are the tiles they always were, and the Tweaks panel
 * moves them — including in the "one per artist" mode, which pages the ARTIST rows (not album-group
 * representatives) so a loose-tracks-only artist keeps their tile.
 */
const ALBUMS = [
  { id: 11, title: "Moon Safari", year: 1998, artistId: 1, artistName: "Air", artistSortName: "Air", hasArt: true },
  { id: 22, title: "Abbey Road", year: 1969, artistId: 2, artistName: "The Beatles", artistSortName: "Beatles, The", hasArt: true },
];
const ARTISTS = [
  { id: 1, name: "Air", sortName: "Air", albumCount: 2, trackCount: 20, artAlbumId: 11, hasArt: true },
  { id: 2, name: "The Beatles", sortName: "Beatles, The", albumCount: 3, trackCount: 40, artAlbumId: 22, hasArt: true },
  // Loose tracks only: no album in the list at all. A group representative could never stand for them.
  { id: 3, name: "Nobody", sortName: "Nobody", albumCount: 0, trackCount: 5, artAlbumId: null, hasArt: false },
];

const renderCard = (item, view) => (item.kind === "artist" ? (
  <ArtistCard artist={item.raw} onOpen={() => {}} metadata={view.metadata} hoverClass={view.hoverClass} eager={view.eager} />
) : (
  <AlbumCard album={item.raw} onOpen={() => {}} metadata={view.metadata} hoverClass={view.hoverClass} eager={view.eager} />
));

const makeSource = (artistItems) => createMusicSource({
  albums: ALBUMS, artists: ARTISTS, artistItems, listKey: "t", renderCard,
  onOpenAlbum: () => {}, onOpenArtist: () => {},
});

const props = (artistItems, over = {}) => ({
  source: makeSource(artistItems),
  state: { view: "grid", group: "artist", items: artistItems ? "groups" : "items", sort: "artist" },
  coverScale: 1, metadata: "label", hover: "lift", hoverClass: "bx-hover-lift",
  ...over,
});

async function mount(artistItems, over) {
  const r = render(<GridView {...props(artistItems, over)} />);
  await waitFor(() => expect(r.container.querySelector(".bx-card")).toBeTruthy());
  return r;
}

describe("the music tiles, on the catalog Grid", () => {
  it("pages the ARTIST rows in the one-per-artist mode — an album-less artist keeps their tile", async () => {
    const { container } = await mount(true);
    expect(container.querySelector(".music-artist-grid")).toBeTruthy();
    expect(container.querySelectorAll(".music-artist-card")).toHaveLength(3);
    expect(container.textContent).toContain("Nobody");
  });

  it("pages the albums in the every-album mode", async () => {
    const { container } = await mount(false);
    expect(container.querySelector(".music-album-grid")).toBeTruthy();
    expect(container.querySelectorAll(".music-album-card")).toHaveLength(2);
  });

  it("cover size — the Grid's --cell is the section's base cell times the tweak", async () => {
    const one = await mount(true);
    expect(one.container.querySelector(".music-artist-grid").style.getPropertyValue("--cell")).toBe(`${MUSIC_GRID_CELL}px`);
    one.unmount();
    const big = await mount(true, { coverScale: 2 });
    expect(big.container.querySelector(".music-artist-grid").style.getPropertyValue("--cell")).toBe(`${MUSIC_GRID_CELL * 2}px`);
  });

  it("hover — the host's one hover class rides every tile", async () => {
    const { container } = await mount(false, { hover: "zoom", hoverClass: "bx-hover-zoom" });
    expect(container.querySelectorAll(".music-album-card.bx-hover-zoom")).toHaveLength(2);
  });

  it("rounded + dim — the art box is a bx-cover, which is what both rules select", async () => {
    const { container } = await mount(false);
    expect(container.querySelectorAll(".music-cover.bx-cover")).toHaveLength(2);
  });

  it("metadata: minimal — the sub-line under the title goes", async () => {
    const { container } = await mount(false, { metadata: "minimal" });
    expect(container.querySelectorAll(".music-album-card")).toHaveLength(2);
    expect(container.textContent).toContain("Moon Safari");
    expect(container.querySelector(".music-album-card-sub")).toBeNull();
  });
});
