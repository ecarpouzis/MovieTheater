import {
  MOVIES_MORE,
  MOVIES_UNSEEDED_RAILS,
  composeMoviesExplore,
  moviesFacetHref,
  pickFranchiseRun,
  toChannelCard,
  toContinueCard,
  toFranchiseCard,
} from "./moviesExplore";
import type { MovieCardRow } from "../../catalog/sources/moviesSource";

const row = (id: number, over: Partial<MovieCardRow> = {}): MovieCardRow =>
  ({ id, kind: "movie", title: `Title ${id}`, simpleTitle: `Title ${id}`, releaseDate: "1995-01-01", posterVersion: 2, ...over });

describe("Pages/Browse/moviesExplore — the Movies Explore composition (R9 S7)", () => {
  it("names its rails and drops the ones with nothing in them", () => {
    const out = composeMoviesExplore({
      random: [row(1), row(2), row(3), row(4), row(5), row(6), row(7)],
      recent: [row(20)],
      continueWatching: [{ card: row(30), percent: 42 }],
      recommendations: [{ card: row(40), reason: "Because you liked Heat" }],
      franchiseGroups: [{ key: "mcu", label: "MCU", totalItems: 34, items: [row(50)] }],
      franchiseRun: { defaultFranchise: "alien", franchises: [{ value: "alien", count: 4, items: [{ id: 60, kind: "movie", title: "Alien", year: 1979 }] }] },
      lineup: [{ id: 9, name: "Noir 24/7", now: { title: "The Third Man", posterId: 11, posterVersion: 1, kind: "movie" } }],
      seed: 7,
    });
    expect(out.rails.map((r) => r.key)).toEqual([
      "continue", "now-on-tv", "for-you", "recent", "franchises", "franchise-run", "random",
    ]);
    expect(out.spotlight).toHaveLength(5);
    // The hero eats the first five of the shuffle; the grid rail gets the rest, never a duplicate.
    expect(out.rails.find((r) => r.key === "random")!.items.map((i) => i.id)).toEqual([6, 7]);
    expect(out.seed).toBe(7);

    const empty = composeMoviesExplore({ random: [row(1)] });
    expect(empty.rails.map((r) => r.key)).toEqual([]);
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

  it("the franchise run picks the endpoint's own default and falls back to the first", () => {
    const items = [{ id: 1, kind: "movie", title: "A" }];
    expect(pickFranchiseRun({ defaultFranchise: "b", franchises: [{ value: "a", count: 2, items }, { value: "b", count: 2, items }] })!.value).toBe("b");
    expect(pickFranchiseRun({ franchises: [{ value: "a", count: 2, items }] })!.value).toBe("a");
    expect(pickFranchiseRun({ franchises: [] })).toBeNull();
    expect(pickFranchiseRun(null)).toBeNull();
  });
});
