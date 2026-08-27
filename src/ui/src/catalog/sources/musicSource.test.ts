import { createMusicSource, hexToHue, toAlbumCard, toArtistCard } from "./musicSource";

const albums = [
  { id: 1, title: "Zebra", year: 1999, artistId: 10, artistName: "The Beatles", artistSortName: "Beatles, The", artistKind: null, hasArt: true, dominantColor: "#ff0000", tag: "Live", genres: ["Rock", "Pop"], rating: 71, ratingCount: 2 },
  { id: 2, title: "Apple", year: 2011, artistId: 20, artistName: "Zed", artistSortName: "Zed", artistKind: "comedy", hasArt: false, dominantColor: null, genres: ["Comedy"], rating: 88, ratingCount: 1 },
  { id: 3, title: "Mango", year: 2005, artistId: 10, artistName: "The Beatles", artistSortName: "Beatles, The", artistKind: null, hasArt: true },
];
const artists = [
  { id: 10, name: "The Beatles", sortName: "Beatles, The", yearRange: "1963–1970", albumCount: 2, trackCount: 30, artAlbumId: 1, hasArt: true, dominantColor: "#00ff00", genres: ["Rock"], topRating: 71 },
  { id: 20, name: "Zed", sortName: "Zed", yearRange: null, albumCount: 1, trackCount: 4, artAlbumId: null, hasArt: false, genres: ["Comedy"], topRating: 88 },
];

describe("catalog/musicSource — albums and artists as cards, the catalog owning the sort", () => {
  it("maps an album and an artist; art only when the mount has it, else a tinted tile; hue from the dominant colour", () => {
    const a = toAlbumCard(albums[0]);
    expect(a.key).toBe("album:1");
    expect(a.imageUrl).toBe("/MusicImage/1?v=1");
    expect(a.imageThumbUrl).toBe("/MusicImageThumb/1?v=1");
    expect(a.hue).toBe(0);
    expect(a.subtitle).toBe("The Beatles");
    expect(a.badges?.[0].label).toBe("Live");
    const b = toAlbumCard(albums[1]);
    expect(b.imageUrl.startsWith("data:image/svg+xml")).toBe(true);
    expect(b.imageThumbUrl).toBeUndefined();
    const ar = toArtistCard(artists[0]);
    expect(ar).toMatchObject({ kind: "artist", key: "artist:10", label: "2 albums", subtitle: "1963–1970", count: 2, imageUrl: "/MusicImage/1?v=1", hue: 120 });
    expect(toArtistCard(artists[1]).label).toBe("1 album");
    expect(hexToHue("#0000ff")).toBe(240);
    expect(hexToHue("nope")).toBeNull();
  });

  // R9 S3: the page's own grid retired, so `sortRows`/`letterKeyFor` — the helpers that kept it in
  // step with the views' order — went with it. The SOURCE does both now, so that is what is pinned.
  it("sorts and buckets letters on each sort's own key", async () => {
    const s = createMusicSource({ albums, listKey: "k", onOpenAlbum: vi.fn(), onOpenArtist: vi.fn() });
    expect((await s.fetchFlatBand(0, 10, "artist")).items.map((c) => c.id)).toEqual([1, 2, 3]); // the server's order stands
    expect((await s.fetchFlatBand(0, 10, "title")).items.map((c) => c.title)).toEqual(["Apple", "Mango", "Zebra"]);
    expect((await s.fetchFlatBand(0, 10, "newest")).items.map((c) => c.year)).toEqual([2011, 2005, 1999]);
    // R9 S10 "Top rated": the server's blend, best first — and an album nothing is known about files
    // LAST rather than in the middle (the fallback is -1, so a genuine 0 would still outrank it).
    expect((await s.fetchFlatBand(0, 10, "rated")).items.map((c) => c.id)).toEqual([2, 1, 3]);
    expect((await s.letters!("artist")).map((b) => b.letter)).toContain("B");
    expect((await s.letters!("title")).map((b) => b.letter)).toEqual(["A", "M", "Z"]);
    // The artist rows sort by the SAME keys as the albums (one section, one Sort pill — R9 S1b).
    const a = createMusicSource({ albums, artists, artistItems: true, listKey: "k", onOpenAlbum: vi.fn(), onOpenArtist: vi.fn() });
    expect((await a.fetchFlatBand(0, 10, "artist")).items.map((c) => c.id)).toEqual([10, 20]);
    expect((await a.letters!("artist")).map((b) => b.letter)).toContain("B");
  });

  it("the one catalog (albums) groups by artist / decade / year / kind / tag / genre, lands on one-per-artist, walks artists in the directory and opens an artist from a group", async () => {
    const onOpenAlbum = vi.fn();
    const onOpenArtist = vi.fn();
    const s = createMusicSource({ albums, listKey: "k", onOpenAlbum, onOpenArtist });
    // R9 S8: `letter` retired (the A–Z strip is the letter axis); year and the quality tag added.
    expect(s.groups.map((g) => g.value)).toEqual(["artist", "decade", "year", "kind", "tag", "genre"]);
    expect(s.groups.map((g) => g.value)).not.toContain("letter");
    expect(s.currentSort).toBeUndefined();
    // R9 S1b: no artists tab — "one per artist" is the Items mode a fresh visitor lands on
    expect(s.itemsModes).toEqual(["items", "groups"]);
    expect(s.defaultItems).toBe("groups");
    expect(s.itemsLabels).toEqual({ items: "Every album", groups: "One per artist" });
    const byArtist = await s.fetchGroupBand!(0, 10, 10, "artist", "artist");
    expect(byArtist.groups.map((g) => [g.key, g.label, g.totalItems])).toEqual([["10", "The Beatles", 2], ["20", "Zed", 1]]);
    const byKind = await s.fetchGroupBand!(0, 10, 10, "kind", "artist");
    expect(byKind.groups.map((g) => g.label)).toEqual(["Comedy", "Music"]);
    const byYear = await s.fetchGroupBand!(0, 10, 10, "year", "artist");
    expect(byYear.groups.map((g) => [g.label, g.totalItems])).toEqual([["2011", 1], ["2005", 1], ["1999", 1]]);
    // The quality/curation tag: the value is the album's `tag` VERBATIM — `MusicNaming` already
    // stripped the folder's brackets, so it is "FLAC" / "V0" / "Live", not "[FLAC]".
    const byTag = await s.fetchGroupBand!(0, 10, 10, "tag", "artist");
    expect(byTag.groups.map((g) => [g.key, g.totalItems])).toEqual([["Live", 1]]);
    // R9 S10: an album is several genres at once, so this axis puts a record on EVERY shelf its
    // genres name — the one axis here whose shelves overlap on purpose. An album with no genre is
    // dropped rather than pooled under an "Unknown" shelf.
    const byGenre = await s.fetchGroupBand!(0, 10, 10, "genre", "artist");
    expect(byGenre.groups.map((g) => [g.key, g.totalItems])).toEqual([["Comedy", 1], ["Pop", 1], ["Rock", 1]]);
    expect(await s.letters!("title")).toEqual([{ letter: "A", count: 1, offset: 0 }, { letter: "M", count: 1, offset: 1 }, { letter: "Z", count: 1, offset: 2 }]);
    // A numeric / count-ordered axis has no A–Z rail; the alphabetical ones keep theirs.
    expect(await s.groupLetters!("year", "artist")).toEqual([]);
    expect(await s.groupLetters!("tag", "artist")).toEqual([]);
    expect((await s.groupLetters!("artist", "artist")).map((b) => b.letter)).toEqual(["T", "Z"]);
    const roots = await s.directory!.roots();
    expect(roots.map((r) => [r.id, r.label, r.count])).toEqual([["10", "The Beatles", 2], ["20", "Zed", 1]]);
    s.onOpen(byArtist.groups[0].items[0]);
    expect(onOpenAlbum).toHaveBeenCalledWith(1);
    s.onOpenGroup!(byArtist.groups[1], "artist");
    expect(onOpenArtist).toHaveBeenCalledWith(20);
  });

  it("a group header that HAS a facet scopes in place; an artist header still opens the artist drill", async () => {
    const onScope = vi.fn();
    const onOpenArtist = vi.fn();
    const s = createMusicSource({ albums, listKey: "k", onOpenAlbum: vi.fn(), onOpenArtist, onScope });
    const g = (key: string, label = key) => ({ key, label, totalItems: 1, renderTotal: 1, items: [] });

    s.onOpenGroup!(g("Live"), "tag");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "tag", value: "Live" }, group: "artist" });
    s.onOpenGroup!(g("comedy", "Comedy"), "kind");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "kind", value: "comedy" }, group: "artist" });
    // The library's own rows are the shelf-LESS scope — "no kind pill", not `kind:music`.
    s.onOpenGroup!(g("music", "Music"), "kind");
    expect(onScope).toHaveBeenLastCalledWith({ group: "artist" });
    s.onOpenGroup!(g("1990", "1990s"), "decade");
    expect(onScope).toHaveBeenLastCalledWith({ years: [1990, 1999], group: "artist" });
    s.onOpenGroup!(g("2011"), "year");
    expect(onScope).toHaveBeenLastCalledWith({ years: [2011, 2011], group: "artist" });

    s.onOpenGroup!(g("20", "Zed"), "artist");
    expect(onOpenArtist).toHaveBeenCalledWith(20);
    expect(onScope).toHaveBeenCalledTimes(5);
  });

  it("one per artist is the package's representative mode over the same albums — a group head per artist", async () => {
    const onOpenArtist = vi.fn();
    const s = createMusicSource({ albums, listKey: "k", onOpenAlbum: vi.fn(), onOpenArtist });
    const heads = await s.fetchGroupBand!(0, 10, 1, "artist", "artist");
    expect(heads.groups.map((g) => [g.label, g.totalItems, g.items.length])).toEqual([["The Beatles", 2, 1], ["Zed", 1, 1]]);
    s.onOpenGroup!(heads.groups[0], "artist");
    expect(onOpenArtist).toHaveBeenCalledWith(10);
  });
});
