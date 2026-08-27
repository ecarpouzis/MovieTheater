import {
  ARCADE_MORE,
  ARCADE_UNSEEDED_RAILS,
  arcadeSystemHref,
  composeArcadeExplore,
  pickSpinSystem,
  timeAgo,
  toRecentCard,
  toRoomCard,
  toSystemCard,
} from "./arcadeExplore";
import type { ArcadeGameRow } from "../../catalog/sources/arcadeSource";

const game = (over: Partial<ArcadeGameRow> = {}): ArcadeGameRow =>
  ({ key: "n64|GoldenEye", title: "007 - GoldenEye", system: "n64", year: 1997, rating: 92, hasBoxArt: true, artId: 7, versions: [{ id: 7, label: "USA" }, { id: 8, label: "Japan" }], ...over } as ArcadeGameRow);

const NOW = Date.parse("2026-08-27T12:00:00Z");
const recentRow = (over = {}) => ({
  game: game(),
  lastPlayedUtc: new Date(NOW - 3 * 3600 * 1000).toISOString(),
  saveCount: 2,
  playedVersionId: 8,
  ...over,
});

describe("Pages/Arcade/arcadeExplore — the Arcade Explore composition (R9 S7)", () => {
  it("names its rails and drops the ones with nothing in them", () => {
    const out = composeArcadeExplore({
      recent: [recentRow()],
      rooms: [{ roomCode: "ABCD", game: { id: 7, title: "GoldenEye", system: "n64" }, players: ["Eric"], maxPlayers: 4, seatsFree: 3 }],
      trophies: [{ gameId: 7, title: "GoldenEye", system: "n64", earnedCount: 4, points: 30, lastUnlockedUtc: new Date(NOW).toISOString() }],
      systems: [{ value: "n64", count: 300 }, { value: "ps2", count: 900 }],
      top: [game({ key: "a" }), game({ key: "b" }), game({ key: "c" }), game({ key: "d" }), game({ key: "e" }), game({ key: "f" })],
      spin: { system: "ps2", games: [game({ key: "ps2|ico", system: "ps2" })] },
      seed: 4,
    }, NOW);
    expect(out.rails.map((r) => r.key)).toEqual(["recent", "live", "trophies", "systems", "top", "spin"]);
    expect(out.spotlight).toHaveLength(5);

    const bare = composeArcadeExplore({});
    expect(bare.rails).toHaveLength(0);
  });

  // ── Ported from `ArcadeBrowse.test.js`' RecentlyPlayed block, which pinned the LOBBY strip ──

  it("a player with no history gets no Recently played rail at all", () => {
    const out = composeArcadeExplore({ recent: [], top: [game()] });
    expect(out.rails.find((r) => r.key === "recent")).toBeUndefined();
  });

  it("a recently-played card opens on the version the save belongs to, and says how long ago", () => {
    // Saves are keyed on the ROM ROW, so the card's id must be `playedVersionId` — otherwise Start
    // would look for a save on the card's default version (7 here) and find none.
    const card = toRecentCard(recentRow(), NOW)!;
    expect(card.id).toBe(8);
    expect(card.label).toBe("3h ago");
    // …and that id is what the page puts in `/arcade?game=`.
    expect(`/arcade?game=${card.id}`).toBe("/arcade?game=8");
  });

  it("a malformed recent row costs a tile, never the page", () => {
    expect(toRecentCard({ game: undefined as never })).toBeNull();
    const out = composeArcadeExplore({ recent: [{ game: undefined as never }, recentRow()] }, NOW);
    expect(out.rails.find((r) => r.key === "recent")!.items).toHaveLength(1);
  });

  it("timeAgo is coarse, and says nothing when there is nothing to say", () => {
    expect(timeAgo(new Date(NOW - 20 * 1000).toISOString(), NOW)).toBe("just now");
    expect(timeAgo(new Date(NOW - 20 * 60 * 1000).toISOString(), NOW)).toBe("20m ago");
    expect(timeAgo(new Date(NOW - 50 * 3600 * 1000).toISOString(), NOW)).toBe("2d ago");
    expect(timeAgo(null)).toBe("");
  });

  // ── Routing ──

  it("a console GROUP card lands on the lobby with the system facet — the carousel's own filter", () => {
    const card = toSystemCard({ value: "ps2", count: 900 }, new Map())!;
    expect(card.kind).toBe("system");
    expect(card.groupKey).toBe("ps2");
    expect(card.count).toBe(900);
    expect(arcadeSystemHref(card.groupKey!)).toBe("/arcade?f=system%3Aps2");
  });

  it("a live-room card carries its room code, so the page joins instead of opening the game", () => {
    const card = toRoomCard({ roomCode: "ABCD", game: { id: 7, title: "GoldenEye", system: "n64" }, players: ["Eric", "Sam"], maxPlayers: 4 })!;
    expect((card.raw as { roomCode: string }).roomCode).toBe("ABCD");
    expect(card.label).toBe("2/4 playing");
    expect(card.badges?.[0].tone).toBe("live");
    expect(ARCADE_MORE.live).toBe("/arcade");
  });

  it("the spin picks its console from the seed, deterministically", () => {
    const systems = [{ value: "n64", count: 3 }, { value: "ps2", count: 9 }, { value: "snes", count: 5 }];
    expect(pickSpinSystem(systems, 4)).toBe(pickSpinSystem(systems, 4));
    expect(pickSpinSystem(systems, 4)).not.toBe(pickSpinSystem(systems, 5));
    expect(pickSpinSystem([], 4)).toBeNull();
    // Only the spin re-rolls; everything else reports a current fact.
    expect(ARCADE_UNSEEDED_RAILS.has("spin")).toBe(false);
    expect(ARCADE_UNSEEDED_RAILS.has("recent")).toBe(true);
  });
});
