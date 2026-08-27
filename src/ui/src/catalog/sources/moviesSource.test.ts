import { MOVIE_GROUPS, createMoviesSource, hueOf, movieGroupsFor, scopeOf, toCard, withPage } from "./moviesSource";

const row = (id: number, extra: Record<string, unknown> = {}) => ({ id, kind: "movie", title: `Title ${id}`, simpleTitle: `title ${id}`, releaseDate: "1994-10-14T00:00:00", imdbRating: 8.7, rtTomatometer: 92, rating: "R", posterVersion: 3, ...extra });

function mockFetch(handler: (url: string) => unknown) {
  const calls: string[] = [];
  const fn = vi.fn(async (url: string, init?: { signal?: AbortSignal }) => {
    if (init?.signal?.aborted) throw Object.assign(new Error("aborted"), { name: "AbortError" });
    calls.push(url);
    const body = handler(url);
    return { ok: body != null, status: body != null ? 200 : 404, json: async () => body };
  });
  vi.stubGlobal("fetch", fn);
  return calls;
}

afterEach(() => vi.unstubAllGlobals());

describe("catalog/moviesSource — the search URL is the scope", () => {
  it("reads the type browse, a filtered browse, the rating browse (flat only) and rejects non-catalog searches", () => {
    expect(scopeOf({ url: "/API/GetMoviesByType?type=Movies%2CSeries&sort=alpha", infinite: true })).toEqual({ types: "Movies,Series", mode: null, value: null, sort: "alpha", seed: null, groupable: true, fq: [] });
    expect(scopeOf({ url: "/API/BrowseGenre?genres=Horror&types=Movies&sort=random&seed=5", infinite: true })).toEqual({ types: "Movies", mode: "genre", value: "Horror", sort: "random", seed: "5", groupable: true, fq: [] });
    // The facet rail's scope (R9 S2): every non-paging param IS the filter and rides onto the grouped calls.
    expect(scopeOf({ url: "/API/Browse?types=Movies&genre=Crime&exGenre=Horror&tag=mood%3Atense&my=seen&sort=alpha&seed=9", infinite: true }))
      .toEqual({ types: "Movies", mode: null, value: null, sort: "alpha", seed: "9", groupable: true, fq: [["genre", "Crime"], ["exGenre", "Horror"], ["tag", "mood:tense"], ["my", "seen"]] });
    expect(scopeOf({ url: "/API/BrowsePerson?q=Someone&sort=imdb", infinite: true })?.mode).toBe("actor");
    expect(scopeOf({ url: "/API/GetMoviesByRating?ratingIds=3&types=Movies&sort=alpha", infinite: true })).toMatchObject({ groupable: false, sort: "alpha" });
    expect(scopeOf({ url: "/API/GetRandomMovies" })).toBeNull();
    expect(scopeOf({ url: null, pending: true })).toBeNull();
    expect(scopeOf({ movieIds: [1, 2], infinite: true })).toBeNull();
    expect(withPage("/API/BrowseTitle?q=a&sort=alpha", 2, 60)).toBe("/API/BrowseTitle?q=a&sort=alpha&page=2&pageSize=60");
  });

  it("maps a row onto a card: kind-scoped key and poster, year, badges, a stable hue", () => {
    const c = toCard(row(7, { kind: "series", ratingEstimated: true }));
    expect(c.key).toBe("series:7");
    expect(c.imageUrl).toBe("/SeriesImage/7?v=3");
    expect(c.imageThumbUrl).toBe("/SeriesImageThumb/7?v=3");
    expect(c.year).toBe(1994);
    expect(c.label).toBe("1994");
    expect(c.subtitle).toBe("Series");
    expect(c.rating).toBe(87);
    expect(c.badges?.map((b) => b.label)).toEqual(["IMDb 8.7", "RT 92%", "R ~"]);
    expect(hueOf("Title 7")).toBe(hueOf("Title 7"));
    expect(toCard({ id: 3, kind: "misc", title: "Clip" }).imageUrl).toBe("/MiscImage/3");
  });
});

describe("catalog/moviesSource — paging", () => {
  const search = { url: "/API/GetMoviesByType?type=Movies&sort=alpha", lettersUrl: "/API/BrowseLetters?type=Movies", infinite: true, sort: "alpha" };

  it("pages the flat envelope and carries page 1's total across the later pages that report -1", async () => {
    const calls = mockFetch((url) => (url.includes("page=1&") ? { movies: [row(1), row(2)], totalCount: 123 } : { movies: [row(61)], totalCount: -1 }));
    const source = createMoviesSource({ search, onOpen: vi.fn(), onBrowse: vi.fn() })!;
    expect(source.currentSort).toBe("alpha");
    expect(source.supports).toContain("shelf");
    const first = await source.fetchFlatBand(0, 60, "alpha");
    expect(first.total).toBe(123);
    expect(first.items.map((i) => i.key)).toEqual(["movie:1", "movie:2"]);
    const second = await source.fetchFlatBand(60, 60, "alpha");
    expect(second.total).toBe(123);
    expect(calls).toEqual(["/API/GetMoviesByType?type=Movies&sort=alpha&page=1&pageSize=60", "/API/GetMoviesByType?type=Movies&sort=alpha&page=2&pageSize=60"]);
    const letters = await source.letters!("alpha");
    expect(calls[2]).toBe("/API/BrowseLetters?type=Movies");
    expect(letters).toEqual([]);
  });

  it("pages groups under the same scope, tags cards with their group, and asks for more of one group by key", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("singleGroupKey=Horror")) return { totalGroups: 9, groups: [{ key: "Horror", label: "Horror", totalItems: 70, items: [row(99)] }] };
      if (url.includes("/API/BrowseGroupLetters")) return { totalGroups: 9, letters: [{ letter: "H", firstIndex: 3 }] };
      return { totalGroups: 9, groups: [{ key: "Action", label: "Action", totalItems: 40, renderTotal: 40, items: [row(1), row(2)] }] };
    });
    const onBrowse = vi.fn();
    const onOpen = vi.fn();
    const source = createMoviesSource({ search: { url: "/API/BrowseGenre?genres=Horror&types=Movies&sort=random&seed=5", infinite: true }, onOpen, onBrowse })!;
    const band = await source.fetchGroupBand!(0, 20, 48, "decade", "random");
    expect(calls[0]).toBe("/API/BrowseGroups?types=Movies&mode=genre&value=Horror&sort=random&seed=5&groupBy=decade&groupsSkip=0&groupsTop=20&perGroupTop=48");
    expect(band.totalGroups).toBe(9);
    expect(band.groups[0].items.map((i) => i.groupKey)).toEqual(["Action", "Action"]);
    const more = await source.fetchGroupMore!("Horror", 48, 48, "genre", "random");
    expect(calls[1]).toContain("singleGroupKey=Horror&perGroupSkip=48&perGroupTop=48");
    expect(more).toMatchObject({ total: 70 });
    expect(more.items[0].key).toBe("movie:99");
    expect(await source.groupLetters!("genre", "random")).toEqual([{ letter: "H", firstIndex: 3 }]);
    expect(calls[2]).toBe("/API/BrowseGroupLetters?types=Movies&mode=genre&value=Horror&groupBy=genre");

    source.onOpen(band.groups[0].items[0]);
    expect(onOpen).toHaveBeenCalledWith(1, "movie");
    source.onOpenGroup!(band.groups[0], "genre");
    expect(onBrowse).toHaveBeenCalledWith("genre", "Action");
    source.onOpenGroup!({ key: "1990", label: "1990s", totalItems: 1, renderTotal: 1, items: [] }, "decade");
    expect(onBrowse).toHaveBeenCalledTimes(1);
  });

  it("the directory walks every franchise head in pages of 50 and lists a franchise's titles", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("singleGroupKey=")) return { groups: [{ key: "alien", label: "Alien", totalItems: 6, items: [row(5)] }] };
      const skip = Number(/groupsSkip=(\d+)/.exec(url)?.[1] ?? 0);
      const n = skip === 0 ? 50 : 10;
      return { totalGroups: 60, groups: Array.from({ length: n }, (_, i) => ({ key: `f${skip + i}`, label: `F${skip + i}`, totalItems: 3, items: [row(skip + i)] })) };
    });
    const source = createMoviesSource({ search, onOpen: vi.fn(), onBrowse: vi.fn() })!;
    const roots = await source.directory!.roots();
    expect(roots).toHaveLength(60);
    expect(roots[0]).toMatchObject({ id: "f0", label: "F0", count: 3, imageUrl: "/ImageThumb/0?v=3" });
    expect(calls[0]).toContain("groupBy=franchise&groupsSkip=0&groupsTop=50&perGroupTop=1");
    expect(calls[1]).toContain("groupsSkip=50");
    expect(calls).toHaveLength(2);
    const page = await source.directory!.items("alien", 0, 500);
    expect(page).toMatchObject({ total: 6 });
    expect(page.items[0].key).toBe("movie:5");
  });

  it("offers the audited axis set — no letter, and my-lists only for a signed-in reader (R9 S8)", () => {
    const values = MOVIE_GROUPS.map((g) => g.value);
    expect(values).toEqual(["genre", "decade", "franchise", "type", "director", "mpa", "subgenre", "mood", "era", "setting", "my"]);
    // The letter axis was retired: the A–Z strip is the letter axis.
    expect(values).not.toContain("letter");
    expect(movieGroupsFor(false).map((g) => g.value)).not.toContain("my");
    expect(movieGroupsFor(true).map((g) => g.value)).toContain("my");

    const source = createMoviesSource({ search, signedIn: true, onOpen: vi.fn(), onBrowse: vi.fn() })!;
    expect(source.groups.map((g) => g.value)).toEqual(values);
    expect(createMoviesSource({ search, onOpen: vi.fn(), onBrowse: vi.fn() })!.groups.map((g) => g.value)).not.toContain("my");
  });

  it("every axis header scopes in place: its own facet, the year range, or the `my` flag", () => {
    const onScope = vi.fn();
    const source = createMoviesSource({ search, signedIn: true, onOpen: vi.fn(), onBrowse: vi.fn(), onScope })!;
    const g = (key: string) => ({ key, label: key, totalItems: 1, renderTotal: 1, items: [] });

    source.onOpenGroup!(g("Action"), "genre");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "genre", value: "Action" }, group: "decade" });
    source.onOpenGroup!(g("Series"), "type");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "type", value: "Series" }, group: "genre" });
    // A director shelf drills into the PEOPLE facet — the rail has no director-only facet.
    source.onOpenGroup!(g("Stanley Kubrick"), "director");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "person", value: "Stanley Kubrick" }, group: "genre" });
    source.onOpenGroup!(g("4"), "mpa");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "mpa", value: "4" }, group: "genre" });
    source.onOpenGroup!(g("cozy"), "mood");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "mood", value: "cozy" }, group: "genre" });
    source.onOpenGroup!(g("neo-noir"), "subgenre");
    expect(onScope).toHaveBeenLastCalledWith({ facet: { key: "subgenre", value: "neo-noir" }, group: "genre" });
    source.onOpenGroup!(g("1990"), "decade");
    expect(onScope).toHaveBeenLastCalledWith({ years: [1990, 1999], group: "genre" });
    source.onOpenGroup!(g("seen"), "my");
    expect(onScope).toHaveBeenLastCalledWith({ flag: "seen", group: "genre" });
  });

  it("tags a group with the axis it came from, so the Newspaper's eyebrow is never invented", async () => {
    mockFetch(() => ({ totalGroups: 1, groups: [{ key: "cozy", label: "Cozy", totalItems: 2, items: [row(1)] }] }));
    const source = createMoviesSource({ search, onOpen: vi.fn(), onBrowse: vi.fn() })!;
    const band = await source.fetchGroupBand!(0, 20, 48, "mood", "alpha");
    expect(band.groups[0].detail).toEqual({ kicker: "Mood" });
  });

  it("a flat-only scope offers no grouped views, groups or directory", () => {
    const source = createMoviesSource({ search: { url: "/API/GetMoviesByRating?ratingIds=3&sort=alpha", infinite: true }, onOpen: vi.fn(), onBrowse: vi.fn() })!;
    expect(source.supports).toEqual(["grid", "wall", "list"]);
    expect(source.groups).toEqual([]);
    expect(source.fetchGroupBand).toBeUndefined();
    expect(source.directory).toBeUndefined();
    expect(createMoviesSource({ search: { url: "/API/GetRandomMovies" }, onOpen: vi.fn(), onBrowse: vi.fn() })).toBeNull();
  });
});
