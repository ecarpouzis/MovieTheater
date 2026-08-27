import { describe, expect, it } from "vitest";
import { emptyFacetState, parseFacetState } from "../../catalog/rail/facetUrl";
import type { MusicAlbumRow, MusicArtistRow } from "../../catalog/sources/musicSource";
import { MUSIC_PARSE_SPEC, applyMusicFacets, countMusicFacets, kindFromSearch, kindOf, legacyToMusicSearch, musicFacetSpec, musicItemsMode, narrowsAlbums } from "./musicFacetSpec";

const artists: MusicArtistRow[] = [
  { id: 1, name: "Air", sortName: "Air", albumCount: 2 },
  { id: 2, name: "The Beatles", sortName: "Beatles, The", albumCount: 1 },
  { id: 3, name: "Nobody", sortName: "Nobody", albumCount: 0 },
];
const albums: MusicAlbumRow[] = [
  { id: 11, title: "Moon Safari", year: 1998, artistId: 1, artistName: "Air", tag: "Live", genres: ["Electronic", "Downtempo"], rating: 82 },
  { id: 12, title: "Talkie Walkie", year: 2004, artistId: 1, artistName: "Air", genres: ["Electronic"], rating: 64 },
  { id: 22, title: "Abbey Road", year: 1969, artistId: 2, artistName: "The Beatles", tag: "Remaster", genres: ["Rock"] },
];
const state = (search: string) => parseFacetState(search, MUSIC_PARSE_SPEC);
const ids = (rows: { id: number }[]) => rows.map((r) => r.id);

describe("applyMusicFacets", () => {
  it("nothing narrowing → every album and every artist of the shelf; a kind: include never drops a row", () => {
    const r = applyMusicFacets(albums, artists, emptyFacetState());
    expect(ids(r.albums)).toEqual([11, 12, 22]);
    expect(ids(r.artists)).toEqual([1, 2, 3]);
    const c = applyMusicFacets(albums, artists, state("?f=kind:comedy"));
    expect(ids(c.albums)).toEqual([11, 12, 22]);
    expect(ids(c.artists)).toEqual([1, 2, 3]);
  });

  it("artist / tag / year narrow the albums and fold the artists to those with a kept album (or named by the facet)", () => {
    expect(ids(applyMusicFacets(albums, artists, state("?f=artist:1")).albums)).toEqual([11, 12]);
    expect(ids(applyMusicFacets(albums, artists, state("?f=artist:1")).artists)).toEqual([1]);
    expect(ids(applyMusicFacets(albums, artists, state("?f=artist:3")).artists)).toEqual([3]);
    expect(ids(applyMusicFacets(albums, artists, state("?f=tag:Live")).albums)).toEqual([11]);
    expect(ids(applyMusicFacets(albums, artists, state("?x=tag:Live")).albums)).toEqual([12, 22]);
    expect(ids(applyMusicFacets(albums, artists, state("?y=-1990")).artists)).toEqual([2]);
  });

  it("q matches an album's title or artist; the artist grid also keeps artists whose NAME matches when q is the only filter", () => {
    const r = applyMusicFacets(albums, artists, state("?q=bod"));
    expect(ids(r.albums)).toEqual([]);
    expect(ids(r.artists)).toEqual([3]);
    const air = applyMusicFacets(albums, artists, state("?q=air"));
    expect(ids(air.albums)).toEqual([11, 12]);
    expect(ids(air.artists)).toEqual([1]);
    // With another filter alongside, the name match no longer rescues an artist without a kept album.
    expect(ids(applyMusicFacets(albums, artists, state("?q=bod&f=tag:Live")).artists)).toEqual([]);
  });
});

describe("countMusicFacets", () => {
  it("lists artists by album count with their names (loose-track-only artists at 0), tags, ascending decades, the shelf pills", () => {
    const f = countMusicFacets(albums, artists);
    expect(f.artist).toEqual([
      { value: 1, label: "Air", count: 2 },
      { value: 2, label: "The Beatles", count: 1 },
      { value: 3, label: "Nobody", count: 0 },
    ]);
    expect(f.tag.map((r) => r.value)).toEqual(["Live", "Remaster"]);
    expect(f.decades.map((r) => r.label)).toEqual(["1960s", "1990s", "2000s"]);
    expect(f.kind.map((r) => r.value)).toEqual(["comedy", "audiobook"]);
  });
});

describe("kind + legacy links", () => {
  it("the last kind added wins; unknown kinds read as the library", () => {
    expect(kindOf(state("?f=kind:comedy&f=kind:audiobook"))).toBe("audiobook");
    expect(kindOf(state("?f=kind:bogus"))).toBe("");
    expect(kindFromSearch("?f=kind:comedy&q=x")).toBe("comedy");
    expect(kindFromSearch("")).toBe("");
  });

  it("rewrites ?kind= and ?tab=/?view=artists|albums once, collapses several kind pills, leaves a final URL alone", () => {
    expect(legacyToMusicSearch("?q=air&f=kind:comedy&view=wall")).toBeNull();
    expect(legacyToMusicSearch("?kind=comedy&q=x")).toBe("?q=x&f=kind%3Acomedy");
    expect(legacyToMusicSearch("?kind=&f=artist:1")).toBe("?f=artist%3A1");
    expect(legacyToMusicSearch("?tab=albums&view=wall")).toBe("?view=wall&items=items");
    expect(legacyToMusicSearch("?view=artists")).toBe("?items=groups");
    expect(legacyToMusicSearch("?f=kind:comedy&f=kind:audiobook")).toBe("?f=kind%3Aaudiobook");
  });

  it("the Items mode reads the URL first", () => {
    expect(musicItemsMode("?items=items")).toBe("items");
    expect(musicItemsMode("?items=groups")).toBe("groups");
  });
});

describe("the genre facet and the rating floor (R9 S10)", () => {
  it("a genre include keeps the albums filed under it; two includes read as AND", () => {
    expect(ids(applyMusicFacets(albums, artists, state("?f=genre:Electronic")).albums)).toEqual([11, 12]);
    expect(ids(applyMusicFacets(albums, artists, state("?f=genre:Electronic&f=genre:Downtempo")).albums)).toEqual([11]);
    expect(ids(applyMusicFacets(albums, artists, state("?x=genre:Electronic")).albums)).toEqual([22]);
    // The artist grid folds to the artists with a kept album, as it does for every other facet.
    expect(ids(applyMusicFacets(albums, artists, state("?f=genre:Rock")).artists)).toEqual([2]);
  });

  it("the rating floor keeps what reaches it and DROPS the unscored — an unrated record is below every floor", () => {
    expect(ids(applyMusicFacets(albums, artists, state("?r=70")).albums)).toEqual([11]);
    expect(ids(applyMusicFacets(albums, artists, state("?r=60")).albums)).toEqual([11, 12]);
    // Abbey Road has no score at all and never survives a floor, however low.
    expect(ids(applyMusicFacets(albums, artists, state("?r=1")).albums)).toEqual([11, 12]);
    // A floor IS a narrowing, so the artist grid must fold rather than showing everybody.
    expect(narrowsAlbums(state("?r=70"))).toBe(true);
    expect(ids(applyMusicFacets(albums, artists, state("?r=70")).artists)).toEqual([1]);
  });

  it("the rail lists genres by size, and the long tail is searched and paged out of the shelf itself", async () => {
    expect(countMusicFacets(albums, artists).genre.map((r) => [r.value, r.count])).toEqual([["Electronic", 2], ["Downtempo", 1], ["Rock", 1]]);
    const spec = musicFacetSpec("t", albums, artists, "albums");
    expect(spec.facets.find((f) => f.key === "genre")?.dynamic).toBe(true);
    expect(spec.rating?.presets.map((p) => p.value)).toEqual([60, 70, 80, 90]);
    const page = await spec.loadOptions!("genre", "elec", 0, 10);
    expect(page).toEqual({ items: [{ value: "Electronic", label: "Electronic", count: 2 }], total: 1 });
  });
});
