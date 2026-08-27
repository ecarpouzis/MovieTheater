import { createMusicSource, hexToHue, letterKeyFor, sortRows, toAlbumCard, toArtistCard } from "./musicSource";

const albums = [
  { id: 1, title: "Zebra", year: 1999, artistId: 10, artistName: "The Beatles", artistSortName: "Beatles, The", artistKind: null, hasArt: true, dominantColor: "#ff0000", tag: "Live" },
  { id: 2, title: "Apple", year: 2011, artistId: 20, artistName: "Zed", artistSortName: "Zed", artistKind: "comedy", hasArt: false, dominantColor: null },
  { id: 3, title: "Mango", year: 2005, artistId: 10, artistName: "The Beatles", artistSortName: "Beatles, The", artistKind: null, hasArt: true },
];
const artists = [
  { id: 10, name: "The Beatles", sortName: "Beatles, The", yearRange: "1963–1970", albumCount: 2, trackCount: 30, artAlbumId: 1, hasArt: true, dominantColor: "#00ff00" },
  { id: 20, name: "Zed", sortName: "Zed", yearRange: null, albumCount: 1, trackCount: 4, artAlbumId: null, hasArt: false },
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

  it("sorts rows the way the views sort cards, and buckets letters on the sort's own key", () => {
    expect(sortRows(albums, "albums", "artist").map((a) => a.id)).toEqual([1, 2, 3]); // the server's order stands
    expect(sortRows(albums, "albums", "title").map((a) => a.title)).toEqual(["Apple", "Mango", "Zebra"]);
    expect(sortRows(albums, "albums", "newest").map((a) => a.year)).toEqual([2011, 2005, 1999]);
    // The artist rows sort by the SAME keys as the albums (one section, one Sort pill — R9 S1b).
    expect(sortRows(artists, "artists", "artist").map((a) => a.id)).toEqual([10, 20]);
    expect(letterKeyFor("albums", "artist")!(albums[0])).toBe("Beatles, The");
    expect(letterKeyFor("albums", "title")!(albums[0])).toBe("Zebra");
    expect(letterKeyFor("albums", "newest")).toBeNull();
    expect(letterKeyFor("artists", null)!(artists[0])).toBe("Beatles, The");
  });

  it("the one catalog (albums) groups by artist / decade / kind / letter, lands on one-per-artist, walks artists in the directory and opens an artist from a group", async () => {
    const onOpenAlbum = vi.fn();
    const onOpenArtist = vi.fn();
    const s = createMusicSource({ albums, listKey: "k", onOpenAlbum, onOpenArtist });
    expect(s.groups.map((g) => g.value)).toEqual(["artist", "decade", "kind", "letter"]);
    expect(s.currentSort).toBeUndefined();
    // R9 S1b: no artists tab — "one per artist" is the Items mode a fresh visitor lands on
    expect(s.itemsModes).toEqual(["items", "groups"]);
    expect(s.defaultItems).toBe("groups");
    expect(s.itemsLabels).toEqual({ items: "Every album", groups: "One per artist" });
    const byArtist = await s.fetchGroupBand!(0, 10, 10, "artist", "artist");
    expect(byArtist.groups.map((g) => [g.key, g.label, g.totalItems])).toEqual([["10", "The Beatles", 2], ["20", "Zed", 1]]);
    const byKind = await s.fetchGroupBand!(0, 10, 10, "kind", "artist");
    expect(byKind.groups.map((g) => g.label)).toEqual(["Comedy", "Music"]);
    const byLetter = await s.fetchGroupBand!(0, 10, 10, "letter", "artist");
    expect(byLetter.groups.map((g) => g.key)).toEqual(["B", "Z"]);
    expect(await s.letters!("title")).toEqual([{ letter: "A", count: 1, offset: 0 }, { letter: "M", count: 1, offset: 1 }, { letter: "Z", count: 1, offset: 2 }]);
    const roots = await s.directory!.roots();
    expect(roots.map((r) => [r.id, r.label, r.count])).toEqual([["10", "The Beatles", 2], ["20", "Zed", 1]]);
    s.onOpen(byArtist.groups[0].items[0]);
    expect(onOpenAlbum).toHaveBeenCalledWith(1);
    s.onOpenGroup!(byArtist.groups[1], "artist");
    expect(onOpenArtist).toHaveBeenCalledWith(20);
    s.onOpenGroup!(byLetter.groups[0], "letter");
    expect(onOpenArtist).toHaveBeenCalledTimes(1);
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
