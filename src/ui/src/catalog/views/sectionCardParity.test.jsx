import { readFileSync } from "node:fs";
import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import Card from "../cards/Card";
import { MovieCard, SimpleMovieCard } from "../../Pages/Browse/MovieCard";
import BoardGameCard, { NO_EXPANSIONS } from "../../Pages/BoardGames/BoardGameCard";
import GameCard from "../../Pages/Arcade/GameCard";
import { AlbumCard, ArtistCard } from "../../Pages/Music/MusicCards";

/**
 * CARD PARITY (R9 S3's binding ruling, re-pinned after the 2026-08-27 regression report).
 *
 * "Engine-level migration only — the section cards keep their EXACT presentation." The engine under
 * every Grid is the package's (InfiniteBands + the letter strip + the tweaks plumbing); what a
 * section DRAWS is the section's, and three of them had silently drifted:
 *
 *  - Music's square album/artist art was being cropped into a 0.66 portrait tile, because the Grid's
 *    `.bx-grid .bx-card > .bx-cover` sizing rule out-specified `.music-cover`. The package card now
 *    wears `bx-card--pkg` and that rule is scoped to it — a section card brings its own geometry.
 *  - Arcade's `.arcade-card__rating` and `.arcade-card__body` rules were DELETED with the retired
 *    RecentlyPlayed strip, so the score chip fell into the flex flow as a third column on the left
 *    and the details column (no `min-width: 0`) overflowed its track.
 *  - The boardgame box art lost its top alignment when the letterbox moved to `object-fit`.
 *
 * These tests pin the STRUCTURE — the class names and the field order each card had before S3 — so
 * the presentation cannot drift again without a test saying so. Geometry that lives in CSS is
 * verified by the headless smoke (`stitch-smoke.mjs`), which measures real boxes in a browser;
 * happy-dom computes no stylesheet, so a test here can only pin the DOM.
 */

const wrap = (ui) => render(<MemoryRouter>{ui}</MemoryRouter>);
const classesOf = (root, sel) => [...root.querySelectorAll(sel)].map((e) => e.className);
const noop = () => {};

describe("the package's own card is the only one the Grid may size", () => {
  const item = { kind: "movie", id: 1, key: "movie:1", title: "T", aspect: 0.66, imageUrl: "/i", hue: 1 };

  it("marks itself `bx-card--pkg` so `.bx-grid .bx-card--pkg > .bx-cover` reaches it", () => {
    const { container } = wrap(<Card item={item} cellH={200} metadata="label" hoverClass="" onOpen={noop} />);
    const card = container.querySelector(".bx-card");
    expect(card.classList.contains("bx-card--pkg")).toBe(true);
    // …and it states its own box inline, which is why scoping the CSS rule costs the package nothing.
    expect(container.querySelector(".bx-cover").style.height).toBe("200px");
  });

  it("every SECTION card wears bx-card WITHOUT the package marker", () => {
    const roots = [
      wrap(<AlbumCard album={{ id: 1, title: "A", artistName: "B", year: 1999, hasArt: false }} onOpen={noop} metadata="label" />),
      wrap(<GameCard game={{ id: 1, title: "G", system: "n64", maxPlayers: 1, versions: [] }} onOpen={noop} cellH={180} metadata="label" />),
    ];
    for (const r of roots) {
      const card = r.container.querySelector(".bx-card");
      expect(card.classList.contains("bx-card--pkg")).toBe(false);
    }
  });
});

describe("Movies — the horizontal poster card and the phone `simple` tile", () => {
  const item = {
    id: 7, kind: "movie", title: "Heat", releaseDate: "1995-01-01", rating: "R", runtime: "2h 50m",
    imdbRating: "8.3", topCast: "Al Pacino, Robert De Niro", plotFull: "A crew.", posterVersion: 1,
  };
  const handlers = { onMovieClick: noop, onActorSearch: noop, onToggleSeen: noop, onToggleWant: noop };

  it("keeps poster · title · badges · cast · plot · Seen/Want, in that order", () => {
    const { container } = wrap(
      <MovieCard item={item} metadata="label" hoverClass="bx-hover-lift" showOptions eager {...handlers} />,
    );
    expect(container.querySelector(".card-cell.bx-card")).toBeTruthy();
    // The poster box IS the `bx-cover` — that is the seam the Rounded/Hover tweaks select.
    expect(container.querySelector(".card-content-wrapper > .card-poster-container.bx-cover")).toBeTruthy();
    const col = container.querySelector(".card-right-col");
    expect([...col.children].map((c) => c.className.split(" ")[0])).toEqual([
      "card-title", "card-meta-row", "card-actor-row", "card-plot", "viewing-options",
    ]);
  });

  it("`metadata: minimal` drops the badges/cast/plot but keeps title + Seen/Want", () => {
    const { container } = wrap(
      <MovieCard item={item} metadata="minimal" hoverClass="" showOptions {...handlers} />,
    );
    const col = container.querySelector(".card-right-col");
    expect([...col.children].map((c) => c.className.split(" ")[0])).toEqual(["card-title", "viewing-options"]);
  });

  it("the phone `simple` style is a POSTER TILE variant of the same card, not a package view", () => {
    const { container } = wrap(
      <SimpleMovieCard item={item} metadata="label" hoverClass="" showOptions onMovieClick={noop} onToggleSeen={noop} onToggleWant={noop} />,
    );
    expect(container.querySelector(".simple-card-cell.bx-card > .mobile-movie-card")).toBeTruthy();
    expect(container.querySelector(".mobile-movie-card > .simple-card-poster.bx-cover")).toBeTruthy();
  });
});

describe("Board games — the box-art card", () => {
  const game = {
    id: 3, name: "Hive", yearPublished: 2001, minPlayers: 2, maxPlayers: 2, minPlayTime: 20, maxPlayTime: 20,
    description: "A boardless strategy game.", averageRating: 7.2, imageVersion: 1,
  };

  it("keeps the wrapper · antd card · box art (bx-cover) · title · chips · plot", () => {
    const { container } = wrap(
      <BoardGameCard game={game} expansions={NO_EXPANSIONS} metadata="label" hoverClass="" onGameClick={noop} />,
    );
    expect(container.querySelector(".boardgame-card-wrapper.bx-card")).toBeTruthy();
    expect(container.querySelector(".movie-card.boardgame-card")).toBeTruthy();
    expect(container.querySelector(".card-content-wrapper > .boardgame-card-poster-container.bx-cover")).toBeTruthy();
    expect(classesOf(container, ".card-right-col > *").map((c) => c.split(" ")[0]))
      .toEqual(expect.arrayContaining(["card-title", "card-meta-row", "card-plot"]));
  });
});

describe("Arcade — the art-left lobby card", () => {
  const game = {
    id: 5, title: "GoldenEye 007", system: "n64", maxPlayers: 4, rating: 96, year: 1997,
    developer: "Rare", summary: "The console shooter.", genres: "Shooter", versions: [{ region: "USA" }], versionCount: 2,
  };

  it("keeps the score chip as the CARD's own child (not a column in the flex row)", () => {
    const { container } = wrap(<GameCard game={game} onOpen={noop} cellH={180} metadata="label" hoverClass="" />);
    const card = container.querySelector(".arcade-card.bx-card");
    // The rating is the card's FIRST child and a sibling of the art column — it is pinned by CSS
    // (`position: absolute`), and if that rule ever goes away again it becomes a visible third
    // column. Pinning the order here is what makes the deletion detectable.
    expect([...card.children].map((c) => c.className.split(" ")[0]))
      .toEqual(["arcade-card__rating", "arcade-card__art", "arcade-card__body"]);
  });

  it("keeps title · chips · summary · foot inside the details column", () => {
    const { container } = wrap(<GameCard game={game} onOpen={noop} cellH={180} metadata="label" hoverClass="" />);
    const body = container.querySelector(".arcade-card__body");
    expect([...body.children].map((c) => c.className.split(" ")[0]))
      .toEqual(["arcade-card__title", "arcade-tags", "arcade-card__summary", "arcade-card__foot"]);
    // The cover wears `bx-cover` so the tweaks reach it, INSIDE the fixed art box.
    expect(container.querySelector(".arcade-card__art .bx-cover")).toBeTruthy();
  });

  it("`metadata: minimal` leaves the art + title only", () => {
    const { container } = wrap(<GameCard game={game} onOpen={noop} cellH={180} metadata="minimal" hoverClass="" />);
    const body = container.querySelector(".arcade-card__body");
    expect([...body.children].map((c) => c.className.split(" ")[0])).toEqual(["arcade-card__title"]);
  });
});

describe("Music — the SQUARE album and artist tiles", () => {
  it("the album card is cover · title · artist/year sub · tag", () => {
    const album = { id: 9, title: "Homogenic", artistName: "Björk", year: 1997, hasArt: true, tag: "FLAC" };
    const { container } = wrap(<AlbumCard album={album} onOpen={noop} metadata="label" hoverClass="" />);
    const card = container.querySelector(".music-album-card.bx-card");
    expect([...card.children].map((c) => c.className.split(" ")[0]))
      .toEqual(["music-cover", "music-album-card-title", "music-album-card-sub", "music-album-card-tag"]);
    // `music-cover` is the square box (aspect-ratio: 1 in MusicPage.css). The package Grid must not
    // be able to re-size it — that is what `bx-card--pkg` scoping guarantees.
    expect(container.querySelector(".music-cover.bx-cover")).toBeTruthy();
  });

  it("the artist card is cover · name · year-range/albums/tracks sub", () => {
    const artist = { id: 2, name: "A Perfect Circle", artAlbumId: 3, hasArt: true, yearRange: "2000–2004", albumCount: 3, trackCount: 36 };
    const { container } = wrap(<ArtistCard artist={artist} onOpen={noop} metadata="label" hoverClass="" />);
    const card = container.querySelector(".music-artist-card.bx-card");
    expect([...card.children].map((c) => c.className.split(" ")[0]))
      .toEqual(["music-cover", "music-artist-card-name", "music-artist-card-sub"]);
  });

  it("`metadata: minimal` drops only the sub-line", () => {
    const album = { id: 9, title: "Homogenic", artistName: "Björk", year: 1997, hasArt: false };
    const { container } = wrap(<AlbumCard album={album} onOpen={noop} metadata="minimal" hoverClass="" />);
    const card = container.querySelector(".music-album-card");
    expect([...card.children].map((c) => c.className.split(" ")[0]))
      .toEqual(["music-cover", "music-album-card-title"]);
  });
});

/**
 * The GEOMETRY half. happy-dom computes no stylesheet (setupTests.js replaces getComputedStyle with
 * the inline style, deliberately — see the site-frontend skill), so a rendered card cannot be
 * measured here. What CAN be pinned is that the rules a card's geometry depends on still EXIST and
 * are still scoped the way they are — which is exactly the failure mode that shipped: a rule deleted
 * with a neighbouring feature, and a package rule reaching a section card it was never meant to.
 */
describe("the CSS the card geometry depends on", () => {
  const read = (p) => readFileSync(new URL(p, import.meta.url), "utf8");

  it("the Grid sizes the PACKAGE card only — never a section card", () => {
    const css = read("../styles/catalog-views.css");
    expect(css).toMatch(/\.bx-grid \.bx-card--pkg > \.bx-cover \{/);
    expect(css).toMatch(/\.bx-grid \.bx-card--pkg \.bx-meta \{/);
    // The unscoped form is the bug: it out-specifies `.music-cover` and crops square art to 0.66.
    expect(css).not.toMatch(/\.bx-grid \.bx-card > \.bx-cover \{/);
  });

  it("the arcade card keeps its pinned score chip and its bounded details column", () => {
    const css = read("../../Pages/Arcade/ArcadePage.css");
    expect(css).toMatch(/\.arcade-card__rating \{[^}]*position:\s*absolute/);
    // `min-width: 0` is what keeps the chips + summary inside the grid track.
    expect(css).toMatch(/\.arcade-card__body \{[^}]*min-width:\s*0/);
  });

  it("the boardgame box art letterboxes to the TOP of its box, as it always did", () => {
    const css = read("../../Pages/BoardGames/BoardGameCardList.css");
    expect(css).toMatch(/object-position:\s*center top/);
  });

  it("music art is a SQUARE box", () => {
    expect(read("../../Pages/Music/MusicPage.css")).toMatch(/\.music-cover \{[^}]*aspect-ratio:\s*1/);
  });
});
