import { arcadeQuery, coverUrl, createArcadeSource, serverSort, toArcadeCard } from "./arcadeSource";

const row = (n: number, extra: Record<string, unknown> = {}) => ({ key: `snes|game-${n}`, title: `Game ${n}`, system: "snes", artId: 100 + n, artV: "7", hasBoxArt: true, year: 1990 + n, maxPlayers: 2, versionCount: 1, rating: 82, ratingSource: "LaunchBox", genres: "Platform", raAchievements: true, versions: [{ id: 5000 + n }], ...extra });

function mockFetch(handler: (url: string) => unknown) {
  const calls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    calls.push(url);
    const body = handler(url);
    return { ok: body != null, status: body != null ? 200 : 404, json: async () => body };
  }));
  return calls;
}
afterEach(() => vi.unstubAllGlobals());

describe("catalog/arcadeSource — the lobby's filters are the scope", () => {
  it("maps a card: version id, cover URL with its art token, badges; a coverless card gets a tinted tile", () => {
    const c = toArcadeCard(row(1));
    expect(c).toMatchObject({ kind: "game", id: 5001, key: "game:snes|game-1", title: "Game 1", subtitle: "snes", label: "1991", imageUrl: "/ArcadeImage/101?v=7", rating: 82 });
    expect(c.badges?.map((b) => b.label)).toEqual(["★ 82", "👥 2", "🏆"]);
    const bare = toArcadeCard({ key: "nes|x", title: "X", hasBoxArt: false, artId: 5 });
    expect(bare.imageUrl.startsWith("data:image/svg+xml")).toBe(true);
    expect(bare.id).toBeGreaterThanOrEqual(1_000_000_000);
    expect(coverUrl({ key: "k", title: "t", hasBoxArt: true, artId: 9 })).toBe("/ArcadeImage/9");
    expect(serverSort("alpha")).toBe("");
    expect(serverSort("rating")).toBe("rating");
    expect(arcadeQuery({ system: "snes", variant: "all", genre: "", skip: 0, sort: "" })).toBe("?system=snes&skip=0");
  });

  it("pages the lobby endpoint with an absolute skip, carries the total, offers letters only under A–Z", async () => {
    const calls = mockFetch((url) => (url.includes("/API/Arcade/GameLetters") ? { letters: [{ letter: "G", count: 2, offset: 0 }] } : url.includes("skip=0") ? { games: [row(1), row(2)], totalCount: 77 } : { games: [row(3)], totalCount: -1 }));
    const filters = { system: "snes", hideRegions: "jp", maxPlayers: "", variant: "all", genre: "", sort: "", search: "", ra: "" };
    const s = createArcadeSource({ filters, filterKey: "k", onOpen: vi.fn() });
    expect(s.currentSort).toBe("alpha");
    const first = await s.fetchFlatBand(0, 60, "alpha");
    expect(first.total).toBe(77);
    expect(first.items.map((i) => i.id)).toEqual([5001, 5002]);
    expect(calls[0]).toBe("/API/Arcade/Games?system=snes&hideRegions=jp&skip=0&pageSize=60");
    expect((await s.fetchFlatBand(60, 60, "alpha")).total).toBe(77);
    expect(await s.letters!("alpha")).toEqual([{ letter: "G", count: 2, offset: 0 }]);
    expect(calls[2]).toBe("/API/Arcade/GameLetters?system=snes&hideRegions=jp");
    const rated = createArcadeSource({ filters: { ...filters, sort: "rating" }, filterKey: "k2", onOpen: vi.fn() });
    expect(rated.currentSort).toBe("rating");
    expect(rated.letters).toBeUndefined();
  });

  it("groups under the same filters, pulls more of one group, walks systems in the directory, and a header applies the filter", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("singleGroupKey=snes")) return { groups: [{ key: "snes", label: "Super Nintendo", totalItems: 9, items: [row(7)] }] };
      if (url.includes("GameGroupLetters")) return { letters: [{ letter: "S", firstIndex: 4 }] };
      return { totalGroups: 2, groups: [{ key: "genesis", label: "Genesis", totalItems: 3, items: [row(1)] }, { key: "snes", label: "Super Nintendo", totalItems: 9, items: [row(2)] }] };
    });
    const onFilter = vi.fn();
    const onOpen = vi.fn();
    const s = createArcadeSource({ filters: { genre: "Platform", sort: "rating" }, filterKey: "k", onOpen, onFilter });
    const band = await s.fetchGroupBand!(0, 20, 24, "system", "rating");
    expect(calls[0]).toBe("/API/Arcade/GameGroups?genre=Platform&sort=rating&groupBy=system&groupsSkip=0&groupsTop=20&perGroupTop=24");
    expect(band.totalGroups).toBe(2);
    expect(band.groups[1].items[0].groupKey).toBe("snes");
    const more = await s.fetchGroupMore!("snes", 24, 24, "system", "rating");
    expect(calls[1]).toContain("singleGroupKey=snes&perGroupSkip=24&perGroupTop=24");
    expect(more.total).toBe(9);
    expect(await s.groupLetters!("system", "rating")).toEqual([{ letter: "S", firstIndex: 4 }]);
    const roots = await s.directory!.roots();
    expect(roots.map((r) => [r.id, r.count])).toEqual([["genesis", 3], ["snes", 9]]);
    expect(calls[3]).toContain("groupBy=system&groupsSkip=0&groupsTop=50&perGroupTop=1");
    s.onOpen(band.groups[0].items[0]);
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ key: "snes|game-1" }));
    s.onOpenGroup!(band.groups[1], "system");
    expect(onFilter).toHaveBeenCalledWith("system", "snes");
    s.onOpenGroup!({ key: "1990", label: "1990s", totalItems: 1, renderTotal: 1, items: [] }, "decade");
    expect(onFilter).toHaveBeenCalledTimes(1);
  });
});
