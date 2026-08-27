import {
  MUSIC_MORE,
  MUSIC_UNSEEDED_RAILS,
  bestAlbums,
  composeMusicExplore,
  favouriteAlbums,
  musicArtistHref,
  seededPick,
  topArtists,
} from "./musicExplore";
import type { MusicAlbumRow, MusicArtistRow } from "../../catalog/sources/musicSource";

const album = (id: number, over: Partial<MusicAlbumRow> = {}): MusicAlbumRow =>
  ({ id, title: `Album ${id}`, artistId: 1, artistName: "Bush", year: 1994, hasArt: true, ...over });
const artist = (id: number, albums: number): MusicArtistRow =>
  ({ id, name: `Artist ${id}`, albumCount: albums, artAlbumId: id, hasArt: true });

describe("Pages/Music/musicExplore — the Music Explore composition (R9 S7)", () => {
  const albums = Array.from({ length: 40 }, (_, i) => album(i + 1));

  it("names its rails and drops the ones with nothing in them", () => {
    const out = composeMusicExplore({
      albums,
      artists: [artist(1, 9), artist(2, 3)],
      playlists: [{ id: 1, name: "Favorites", isFavorites: true, albumIds: [3, 4] }],
      seed: 12,
    });
    // No album carries a score in this fixture, so "Best on the shelf" is DROPPED rather than
    // padded with records nobody has an opinion about (R9 S10).
    expect(out.rails.map((r) => r.key)).toEqual(["favourites", "just-added", "artists", "random"]);
    expect(out.spotlight).toHaveLength(5);

    const bare = composeMusicExplore({ albums: [], artists: [] });
    expect(bare.rails).toHaveLength(0);
    expect(bare.spotlight).toHaveLength(0);
  });

  it("routes an artist GROUP card to the browse with the artist facet", () => {
    const out = composeMusicExplore({ albums, artists: [artist(412, 6)], seed: 3 });
    const card = out.rails.find((r) => r.key === "artists")!.items[0];
    expect(card.kind).toBe("artist");
    expect(card.groupKey).toBe("412");
    expect(musicArtistHref(card.groupKey!)).toBe("/music?f=artist%3A412");
    expect(MUSIC_MORE.artists).toBe("/music?items=groups");
  });

  it("'Latest on the shelf' is descending id — the section has no added stamp", () => {
    const out = composeMusicExplore({ albums, artists: [], seed: 1 });
    const ids = out.rails.find((r) => r.key === "just-added")!.items.map((i) => i.id);
    expect(ids[0]).toBe(40);
    expect(ids[1]).toBe(39);
    // …and it is not a rail a shuffle can honestly re-roll.
    expect(MUSIC_UNSEEDED_RAILS.has("just-added")).toBe(true);
  });

  it("the favourites rail resolves the Favorites playlist's album ids in the shelf, in order", () => {
    const rows = favouriteAlbums(albums, [
      { id: 2, name: "Road trip", albumIds: [9] },
      { id: 1, name: "Favorites", isFavorites: true, albumIds: [7, 9999, 2] },
    ]);
    expect(rows.map((a) => a.id)).toEqual([7, 2]);
    expect(favouriteAlbums(albums, [])).toEqual([]);
    expect(favouriteAlbums(albums, undefined)).toEqual([]);
  });

  it("the shuffle is seeded — the same seed is the same page, a new seed is a new one", () => {
    const a = seededPick(albums, 5, 10).map((x) => x.id);
    const b = seededPick(albums, 5, 10).map((x) => x.id);
    const c = seededPick(albums, 6, 10).map((x) => x.id);
    expect(a).toEqual(b);
    expect(a).not.toEqual(c);
    expect(seededPick([], 5, 10)).toEqual([]);
  });

  it("the artists rail leads with the names that have the most on the shelf", () => {
    expect(topArtists([artist(1, 2), artist(2, 9), artist(3, 5)]).map((a) => a.id)).toEqual([2, 3, 1]);
  });

  it("'Best on the shelf' is the browse's own Top-rated order, and drops what has no score (R9 S10)", () => {
    const scored = [album(1, { rating: 70, ratingCount: 1 }), album(2, { rating: 91, ratingCount: 4 }), album(3), album(4, { rating: 70, ratingCount: 3 })];
    // Ties break on how many people said so — five agreeing is a better bet than one.
    expect(bestAlbums(scored).map((a) => a.id)).toEqual([2, 4, 1]);
    const out = composeMusicExplore({ albums: scored, artists: [], seed: 1 });
    const rail = out.rails.find((r) => r.key === "best")!;
    expect(rail.title).toBe("Best on the shelf");
    // The rail's "more" IS the browse under the same order, so it is a window onto a real view.
    expect(rail.more).toEqual({ href: "/music?items=items&sort=rated" });
    expect(MUSIC_UNSEEDED_RAILS.has("best")).toBe(true);
  });
});
