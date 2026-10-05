import {
  MOVIES_MORE,
  MOVIES_UNSEEDED_RAILS,
  composeMoviesExplore,
  moviesFacetHref,
  pickFranchiseRun,
  toChannelCard,
  toContinueCard,
  toFranchiseCard,
  movieDoors,
  movieDoorHref,
  pickColumnDecade,
  pickColumnMood,
  pickDirector,
} from "./moviesExplore";
import { toCard, type MovieCardRow } from "../../catalog/sources/moviesSource";
import { titleHeroDetail } from "./MoviesExplorePage";

const row = (id: number, over: Partial<MovieCardRow> = {}): MovieCardRow =>
  ({ id, kind: "movie", title: `Title ${id}`, simpleTitle: `Title ${id}`, releaseDate: "1995-01-01", posterVersion: 2, ...over });

describe("Pages/Browse/moviesExplore — the Movies Explore composition (R9 S7)", () => {
  it("names its rails and drops the ones with nothing in them", () => {
    const out = composeMoviesExplore({
      random: [row(1), row(2), row(3), row(4), row(5), row(6), row(7)],
      recent: [row(20)],
      continueWatching: [{ card: row(30), percent: 42 }],
      recommendations: [{ card: row(40), reason: "Because you liked Heat" }],
      suggested: [row(45), row(46)],
      franchiseGroups: [{ key: "mcu", label: "MCU", totalItems: 34, items: [row(50)] }],
      franchiseRun: { defaultFranchise: "alien", franchises: [{ value: "alien", count: 4, items: [{ id: 60, kind: "movie", title: "Alien", year: 1979 }] }] },
      lineup: [{ id: 9, name: "Noir 24/7", now: { title: "The Third Man", posterId: 11, posterVersion: 1, kind: "movie" } }],
      seed: 7,
    });
    expect(out.rails.map((r) => r.key)).toEqual([
      "continue", "now-on-tv", "ways-in", "recent", "for-you", "suggested", "franchises", "franchise-run", "random",
    ]);
    // The franchise run is a FOCUS plate now: the franchise's name, beside its members in order.
    const runRail = out.rails.find((r) => r.key === "franchise-run")!;
    expect(runRail.kind).toBe("focus");
    expect(runRail.focus).toMatchObject({ name: "Alien", href: "/?f=franchise%3Aalien" });
    expect(out.rails.find((r) => r.key === "continue")!.items[0].progress).toBe(42);
    // The suggestions keep the order they were handed in (newest first) and lead to the Suggested list.
    const suggested = out.rails.find((r) => r.key === "suggested")!;
    expect(suggested.items.map((i) => i.id)).toEqual([45, 46]);
    expect(suggested.more?.href).toBe(MOVIES_MORE.suggested);
    expect(MOVIES_UNSEEDED_RAILS.has("suggested")).toBe(true);
    expect(out.spotlight).toHaveLength(5);
    // The hero takes the best-rated five of the shuffle (all unrated here, so the first five, in order);
    // the grid rail gets the rest, never a duplicate.
    expect(out.rails.find((r) => r.key === "random")!.items.map((i) => i.id)).toEqual([6, 7]);
    const rated = composeMoviesExplore({ random: [row(1, { imdbRating: 6 }), row(2, { imdbRating: 8.5 }), row(3), row(4, { imdbRating: 7 }), row(5), row(6), row(7, { imdbRating: 9 })] });
    expect(rated.spotlight.map((c) => c.id)).toEqual([7, 2, 4, 1, 3]);
    expect(rated.rails.find((r) => r.key === "random")!.items.map((i) => i.id)).toEqual([5, 6]);
    expect(out.seed).toBe(7);

    // With nothing but the shuffle, only "Ways in" remains — its tabs draw (skeleton tiles) until an axis lands.
    const empty = composeMoviesExplore({ random: [row(1)] });
    expect(empty.rails.map((r) => r.key)).toEqual(["ways-in"]);
    expect(empty.rails[0].axes!.every((a) => a.doors === undefined)).toBe(true);
  });

  it("routes a franchise GROUP card to the browse with the matching facet", () => {
    const card = toFranchiseCard({ key: "studio-ghibli", label: "Studio Ghibli", totalItems: 22, items: [row(3)] })!;
    expect(card.kind).toBe("franchise");
    expect(card.groupKey).toBe("studio-ghibli");
    expect(card.count).toBe(22);
    // What the page does with `onOpenGroup(group, "franchise")`:
    expect(moviesFacetHref("franchise", card.groupKey!)).toBe("/?f=franchise%3Astudio-ghibli");
    // …and a person chip from the sheet lands on the People facet.
    expect(moviesFacetHref("actor", "Al Pacino")).toBe("/?f=person%3AAl+Pacino");
    expect(moviesFacetHref("franchise", "")).toBeNull();
  });

  it("each rail's More → is one of the section's own URLs", () => {
    const out = composeMoviesExplore({
      random: [row(1)],
      recent: [row(2)],
      franchiseGroups: [{ key: "mcu", label: "MCU", totalItems: 3, items: [row(4)] }],
      lineup: [{ id: 9, name: "Noir" }],
    });
    const more = Object.fromEntries(out.rails.map((r) => [r.key, r.more?.href]));
    expect(more["now-on-tv"]).toBe("/channels");
    expect(more.recent).toBe(MOVIES_MORE.recent);
    expect(more.franchises).toBe("/?view=shelf&group=franchise");
  });

  it("a continue card shows how far in you are and which episode", () => {
    const card = toContinueCard({ card: row(5, { kind: "series", title: "Hannibal" }), percent: 63, note: "S2E4 · Takiawase" })!;
    expect(card.kind).toBe("series");
    expect(card.badges?.[0]).toMatchObject({ label: "63%" });
    expect(card.subtitle).toBe("S2E4 · Takiawase");
    // "Keep watching" is what it IS — a shuffle would be nonsense.
    expect(MOVIES_UNSEEDED_RAILS.has("continue")).toBe(true);
  });

  it("a channel card wears the poster of what is on right now and is marked LIVE", () => {
    const card = toChannelCard({ id: 4, name: "Noir 24/7", category: "Classics", viewers: 3, now: { title: "The Third Man", posterId: 77, posterVersion: 5, kind: "movie" } })!;
    expect(card.kind).toBe("channel");
    expect(card.title).toBe("Noir 24/7");
    expect(card.subtitle).toBe("The Third Man");
    expect(card.label).toBe("3 watching");
    expect(card.imageUrl).toContain("77");
    expect(card.badges?.[0].tone).toBe("live");
  });

  it("the doors: biggest first, decades chronological, tiny groups dropped, each a browse URL", () => {
    const g = (key: string, totalItems: number) => ({ key, label: key, totalItems, items: [row(totalItems)] });
    expect(movieDoors("mood", [g("bleak", 600), g("cozy", 3), g("playful", 1500)]).map((d) => d.key)).toEqual(["playful", "bleak"]);
    const decades = movieDoors("decade", [g("1990", 1000), g("1950", 300)]);
    expect(decades.map((d) => d.key)).toEqual(["1950", "1990"]);
    expect(decades[0].href).toBe("/?y=1950-1959");
    expect(movieDoorHref("mood", "dreamlike")).toBe("/?f=mood%3Adreamlike");
    expect(movieDoors("genre", [g("Drama", 3000)])[0].covers).toHaveLength(1);
  });

  it("the new modules: a director focus, a ranked ten the critics have also scored, three quick columns", () => {
    const out = composeMoviesExplore({
      random: [row(1)],
      directors: [{ key: "Agnès Varda", label: "Agnès Varda", totalItems: 9, items: [row(70), row(71), row(72)] }],
      top: [row(80, { rtTomatometer: null }), row(81, { rtTomatometer: 96 })],
      critics: [row(90, { rtTomatometer: 100 })],
      decade: { decade: "1970", rows: [row(91)] },
      mood: { mood: "dreamlike", rows: [row(92)] },
      seed: 3,
    });
    const director = out.rails.find((r) => r.key === "director")!;
    expect(director.kind).toBe("focus");
    expect(director.focus).toMatchObject({ name: "Agnès Varda", count: 9 });
    expect(out.rails.find((r) => r.key === "top")!.items.map((i) => i.id)).toEqual([81]);
    const quick = out.rails.find((r) => r.key === "quick")!;
    expect(quick.columns!.map((c) => c.title)).toEqual(["Critics' favourites", "Best of the 1970s", "In a dreamlike mood"]);
    expect(quick.columns![1].more?.href).toBe("/?y=1970-1979");
    // A pick needs at least three titles to be worth a plate, and the seed chooses deterministically.
    expect(pickDirector([{ key: "x", label: "x", totalItems: 2, items: [row(1), row(2)] }], 1)).toBeNull();
    expect(pickColumnDecade(5)).toBe(pickColumnDecade(5));
    expect(pickColumnMood([{ value: "rare", count: 4 }], 1)).toBeNull();
  });

  it("the marquee's detail never prints OMDB's literal N/A", () => {
    const d = titleHeroDetail(toCard(row(1, { releaseDate: "1994-01-01" })), { director: "N/A", runtime: "1 h 30 min", rating: "N/A", genre: "Animation, Action", plot: "A hero." });
    expect(d.meta).toEqual(["1994", "1 h 30 min"]);
    expect(d.tags).toEqual(["Animation", "Action"]);
    expect(d.synopsis).toBe("A hero.");
  });

  it("the franchise run picks the endpoint's own default and falls back to the first", () => {
    const items = [{ id: 1, kind: "movie", title: "A" }];
    expect(pickFranchiseRun({ defaultFranchise: "b", franchises: [{ value: "a", count: 2, items }, { value: "b", count: 2, items }] })!.value).toBe("b");
    expect(pickFranchiseRun({ franchises: [{ value: "a", count: 2, items }] })!.value).toBe("a");
    expect(pickFranchiseRun({ franchises: [] })).toBeNull();
    expect(pickFranchiseRun(null)).toBeNull();
  });
});
