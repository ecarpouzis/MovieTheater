import {
  MUSIC_MORE,
  MUSIC_UNSEEDED_RAILS,
  bestAlbums,
  composeMusicExplore,
  favouriteAlbums,
  genreShelves,
  mostPlayedAlbums,
  popularAlbums,
  musicArtistHref,
  musicGenreHref,
  recentlyPlayedAlbums,
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
    // Nor has anything been PLAYED, so both play rails are dropped too — before the beacon has ever
    // fired every album is a real zero, and a "Most played" rail full of records nobody has put on
    // would be a lie about its own contents (R9 closing pass).
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

  it("'Most played' and 'Recently played' read the library-wide numbers, and drop what was never played", () => {
    const played = [
      album(1, { playCount: 4, lastPlayedUtc: "2026-08-20T09:00:00Z" }),
      album(2, { playCount: 31, lastPlayedUtc: "2026-08-01T09:00:00Z" }),
      album(3),
      album(4, { playCount: 0, lastPlayedUtc: null }),
      album(5, { playCount: 4, lastPlayedUtc: "2026-08-26T09:00:00Z" }),
    ];
    // Most played: count first, then id — so the order is total and does not wobble between fetches.
    expect(mostPlayedAlbums(played).map((a) => a.id)).toEqual([2, 1, 5]);
    // Recently played: the same rows read by their stamp, newest first.
    expect(recentlyPlayedAlbums(played).map((a) => a.id)).toEqual([5, 1, 2]);
    // Never played is not "played least" — it is absent from both.
    expect(mostPlayedAlbums(played).map((a) => a.id)).not.toContain(3);
    expect(recentlyPlayedAlbums(played).map((a) => a.id)).not.toContain(4);

    const out = composeMusicExplore({ albums: played, artists: [], seed: 1 });
    const most = out.rails.find((r) => r.key === "most-played")!;
    expect(most.title).toBe("Most played");
    // The rail's "more" IS the browse under the same order — a window onto a real view.
    expect(most.more).toEqual({ href: "/music?items=items&sort=played" });
    expect(out.rails.find((r) => r.key === "recently-played")!.title).toBe("Recently played");
    expect(MUSIC_UNSEEDED_RAILS.has("most-played")).toBe(true);
    expect(MUSIC_UNSEEDED_RAILS.has("recently-played")).toBe(true);
  });

  describe("popularity and rating are two rails, not one", () => {
    // They were one rail called "Best on the shelf", reading a server blend of house ratings with
    // the popularity signal. With no house ratings the blend WAS popularity, so the rail ranked
    // records by fame under a name promising quality.
    const rows = [
      album(1, { popularity: 95, rating: null }),
      album(2, { popularity: 20, rating: 88, ratingCount: 6 }),
      album(3, { popularity: 60, rating: null }),
    ];

    it("ranks the popular rail by how widely heard, not by any verdict", () => {
      expect(popularAlbums(rows).map((a) => a.id)).toEqual([1, 3, 2]);
    });

    it("keeps records nobody has judged OUT of the best-regarded rail", () => {
      // 1 and 3 are famous and unrated; a "Best on the shelf" that led with them would be a lie.
      expect(bestAlbums(rows).map((a) => a.id)).toEqual([2]);
    });

    it("each rail links to the browse order of the same name", () => {
      const out = composeMusicExplore({ albums: rows, artists: [], seed: 1 });
      const popular = out.rails.find((r) => r.key === "popular")!;
      const best = out.rails.find((r) => r.key === "best")!;

      expect(popular.title).toBe("Most popular");
      expect(popular.more).toEqual({ href: "/music?items=items&sort=popular" });
      expect(best.title).toBe("Best on the shelf");
      expect(best.more).toEqual({ href: "/music?items=items&sort=rated" });
    });

    it("the best-regarded rail simply does not appear while nothing is rated", () => {
      // The honest empty state: an external rating source has not answered yet.
      const unrated = [album(1, { popularity: 80 }), album(2, { popularity: 40 })];
      const out = composeMusicExplore({ albums: unrated, artists: [], seed: 1 });

      expect(out.rails.find((r) => r.key === "best")).toBeUndefined();
      expect(out.rails.find((r) => r.key === "popular")!.items).toHaveLength(2);
    });
  });

  describe("the genre rail", () => {
    // Genres arrive merged across three sources (file tags, MusicBrainz, Last.fm), which is exactly
    // why the folding below matters: the sources disagree about capitalisation constantly.
    const g = (id: number, genres: string[], over: Partial<MusicAlbumRow> = {}) =>
      album(id, { genres, ...over });

    it("counts a genre across albums and keeps the spelling the library uses most", () => {
      const rows = [
        g(1, ["Indie Rock"]), g(2, ["indie rock"]), g(3, ["Indie Rock"]), g(4, ["INDIE ROCK"]),
      ];
      const shelves = genreShelves(rows);

      // One card, not four: folding is case-insensitive or the shelf splits into near-duplicates.
      expect(shelves).toHaveLength(1);
      expect(shelves[0].count).toBe(4);
      // "Indie Rock" appears twice, the others once each — the library's own habit wins.
      expect(shelves[0].name).toBe("Indie Rock");
    });

    it("folds the hyphen too, because the sources disagree about it constantly", () => {
      // Measured on the real library: "Hip-Hop" covered 185 albums and "Hip Hop" 168 — one genre
      // wearing two cards and ranking below where it belongs.
      const rows = [
        g(1, ["Hip-Hop"]), g(2, ["Hip Hop"]), g(3, ["Hip-Hop"]), g(4, ["hip hop"]), g(5, ["Hip-Hop"]),
      ];
      const shelves = genreShelves(rows);

      expect(shelves).toHaveLength(1);
      expect(shelves[0].count).toBe(5);
      expect(shelves[0].name).toBe("Hip-Hop");
    });

    it("leaves the ampersand alone", () => {
      // Folding & would merge "R&B" with anything else starting in R.
      const rows = [
        ...Array.from({ length: 4 }, (_, i) => g(i + 1, ["R&B"])),
        ...Array.from({ length: 4 }, (_, i) => g(i + 10, ["Rock"])),
      ];
      expect(genreShelves(rows).map((s) => s.name).sort()).toEqual(["R&B", "Rock"]);
    });

    it("drops a genre only one or two records claim", () => {
      const rows = [
        ...Array.from({ length: 5 }, (_, i) => g(i + 1, ["Post-Rock"])),
        g(90, ["Zeuhl"]),
        g(91, ["Zeuhl"]),
      ];
      const names = genreShelves(rows).map((s) => s.name);

      // A genre two records claim is a micro-tag, not a way into the collection.
      expect(names).toContain("Post-Rock");
      expect(names).not.toContain("Zeuhl");
    });

    it("wears the best-regarded record that actually HAS art as its face", () => {
      const rows = [
        g(1, ["Dub"], { rating: 90, hasArt: false }),   // best, but no art to show
        g(2, ["Dub"], { rating: 70, hasArt: true }),
        g(3, ["Dub"], { rating: 40, hasArt: true }),
        g(4, ["Dub"], { rating: 10, hasArt: true }),
      ];
      // A face-less card falls back to a placeholder and says nothing about what the genre sounds
      // like, so the highest-rated album WITH art wins rather than the highest-rated album.
      expect(genreShelves(rows)[0].face?.id).toBe(2);
    });

    it("orders by how much of the library the genre covers, and is a standing fact", () => {
      const rows = [
        ...Array.from({ length: 9 }, (_, i) => g(i + 1, ["Soul"])),
        ...Array.from({ length: 4 }, (_, i) => g(i + 20, ["Techno"])),
      ];
      expect(genreShelves(rows).map((s) => s.name)).toEqual(["Soul", "Techno"]);

      const out = composeMusicExplore({ albums: rows, artists: [], seed: 3 });
      const rail = out.rails.find((r) => r.key === "genres")!;
      expect(rail.title).toBe("Sounds on the shelf");
      // Not shuffled: which genres the collection is mostly made of is a fact, not a roll.
      expect(MUSIC_UNSEEDED_RAILS.has("genres")).toBe(true);
    });

    it("makes group cards that open the browse's genre facet", () => {
      const rows = Array.from({ length: 6 }, (_, i) => g(i + 1, ["Krautrock"]));
      const rail = composeMusicExplore({ albums: rows, artists: [], seed: 1 })
        .rails.find((r) => r.key === "genres")!;
      const card = rail.items[0];

      expect(card.kind).toBe("genre");
      // groupKey is what onOpenGroup hands to musicGenreHref.
      expect(card.groupKey).toBe("Krautrock");
      expect(musicGenreHref("Krautrock")).toBe("/music?f=genre%3AKrautrock");
    });

    it("encodes a genre whose name is not URL-safe", () => {
      // "Drum & Bass" and "R&B" are real genres and a bare & would truncate the query.
      expect(musicGenreHref("Drum & Bass")).toBe("/music?f=genre%3ADrum+%26+Bass");
    });

    it("disappears rather than lying when nothing is tagged yet", () => {
      // A shelf fetched before the genre pass ran carries no `genres` at all; an empty rail is
      // dropped by exploreRail, which is the honest empty state.
      const out = composeMusicExplore({ albums: [album(1), album(2)], artists: [], seed: 1 });
      expect(out.rails.find((r) => r.key === "genres")).toBeUndefined();
    });
  });
});
