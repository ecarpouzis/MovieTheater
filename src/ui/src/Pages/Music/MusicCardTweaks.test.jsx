import { render, waitFor } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import GridView from "../../catalog/views/GridView";
import { MUSIC_GRID_CELL, createMusicSource } from "../../catalog/sources/musicSource";
import { AlbumCard, ArtistCard, artistRepeatsTitle } from "./MusicCards";

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

  describe("the tiles keep one baseline", () => {
    const open = () => {};

    it("reserves the quality-tag line even when the record has no tag", () => {
      // The variance Eric caught on 2026-08-31: the tag line was rendered only when there WAS a tag,
      // so a [320] record stood taller than the untagged one beside it and the grid's bottom edge
      // came out ragged. Every tile is now title + sub + tag, tag sometimes empty.
      const tagged = render(<AlbumCard album={{ ...ALBUMS[0], tag: "FLAC" }} onOpen={open} />);
      const bare = render(<AlbumCard album={{ ...ALBUMS[1] }} onOpen={open} />);

      expect(tagged.container.querySelector(".music-album-card-quality").textContent).toBe("FLAC");
      // Present and empty — the reserved line, not a missing element.
      expect(bare.container.querySelector(".music-album-card-tag")).not.toBeNull();
      expect(bare.container.querySelector(".music-album-card-quality").textContent).toBe("");
    });

    it("shows a rating as a rating and popularity as popularity — never one as the other", () => {
      // The house rule the album sheet already states: they are two different facts. With no house
      // ratings in the library yet, the blended "Top rated" number IS the popularity number, so
      // printing that under a star would claim a verdict nobody reached.
      const rated = render(<AlbumCard album={{ ...ALBUMS[0], ratingAvg: 83.4, ratingCount: 4, popularity: 61 }} onOpen={open} />);
      expect(rated.container.querySelector(".music-album-card-score").textContent).toBe("★83");
      expect(rated.container.querySelector(".music-album-card-score").title).toContain("4 listeners");
      // Popularity is not also printed — one number in the corner, and it is the verdict.
      expect(rated.container.querySelector(".music-album-card-pop")).toBeNull();

      const known = render(<AlbumCard album={{ ...ALBUMS[1], popularity: 61 }} onOpen={open} />);
      const pop = known.container.querySelector(".music-album-card-pop");
      expect(pop.textContent).toBe("♪61");
      expect(pop.title).toContain("not how good it is");
      expect(known.container.querySelector(".music-album-card-score")).toBeNull();
    });

    it("puts your own score ahead of the house's", () => {
      const { container } = render(
        <AlbumCard album={{ ...ALBUMS[0], myRating: 92, ratingAvg: 40, ratingCount: 9, popularity: 61 }} onOpen={open} />,
      );
      const score = container.querySelector(".music-album-card-score--mine");
      expect(score.textContent).toBe("★92");
      expect(score.title).toBe("Your rating: 92");
    });

    it("says nothing when nothing is known — 0 is a real score, absent is not", () => {
      const { container } = render(<AlbumCard album={{ ...ALBUMS[0] }} onOpen={open} />);
      expect(container.querySelector(".music-album-card-score")).toBeNull();
      expect(container.querySelector(".music-album-card-pop")).toBeNull();

      const zero = render(<AlbumCard album={{ ...ALBUMS[0], myRating: 0 }} onOpen={open} />);
      expect(zero.container.querySelector(".music-album-card-score--mine").textContent).toBe("★0");
    });

    it("does not print the artist when it is the title again", () => {
      // A root-level compilation is its own artist, so the card was saying the same string twice.
      const comp = { id: 7, title: "1970s Algerian Proto-Rai Underground", year: 2008,
        artistName: "1970s Algerian Proto-Rai Underground", hasArt: true };
      const { container } = render(<AlbumCard album={comp} onOpen={open} />);

      expect(container.querySelector(".music-album-card-title").textContent)
        .toBe("1970s Algerian Proto-Rai Underground");
      expect(container.querySelector(".music-album-card-artist")).toBeNull();
      // The year still earns its place on that line.
      expect(container.querySelector(".music-album-card-year").textContent).toBe("2008");
    });

    it("still prints the artist for a normal record", () => {
      const { container } = render(<AlbumCard album={ALBUMS[0]} onOpen={open} />);
      expect(container.querySelector(".music-album-card-artist").textContent).toBe("Air");
    });
  });

  describe("artistRepeatsTitle", () => {
    it("folds case and punctuation, because the two strings arrive by different paths", () => {
      expect(artistRepeatsTitle({ artistName: "80's Symphonic", title: "80s Symphonic" })).toBe(true);
      expect(artistRepeatsTitle({ artistName: "Air", title: "Moon Safari" })).toBe(false);
    });

    it("an absent artist is not a repeat", () => {
      // Otherwise a card with no artist at all would silently lose its line for the wrong reason.
      expect(artistRepeatsTitle({ artistName: "", title: "" })).toBe(false);
      expect(artistRepeatsTitle({ title: "Whatever" })).toBe(false);
    });
  });
});
