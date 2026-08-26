import { coverItemIdOf, exploreWithLiveArt } from "./booksExploreArt";
import { __resetMediaForTests, __setMediaForTests } from "./booksMedia";
import type { CardItem, ExploreResponse } from "../../catalog/types";

const card = (over: Partial<CardItem>): CardItem => ({
  kind: "comic", id: 1, key: "comic:1", title: "t", aspect: 0.66, imageUrl: "https://host/m/DEAD/thumbs/1.webp", imageThumbUrl: "https://host/m/DEAD/thumbs/1.webp", raw: undefined, ...over,
});
const payload = (): ExploreResponse => ({
  spotlight: [card({ id: 11, key: "comic:11" })],
  rails: [{ key: "top-series", title: "Top", kind: "strip", items: [card({ kind: "series", id: 7, key: "series:7", raw: { cover: { id: 42 } } }), card({ kind: "series", id: 8, key: "series:8", raw: { cover: null } })] }],
});

afterEach(() => __resetMediaForTests());

describe("Books/booksExploreArt — Explore covers come from the browser's live media token", () => {
  it("a series card's cover is its representative issue; an item's is itself", () => {
    expect(coverItemIdOf(card({ id: 5 }))).toBe(5);
    expect(coverItemIdOf(card({ kind: "series", id: 7, raw: { cover: { id: 42 } } }))).toBe(42);
    expect(coverItemIdOf(card({ kind: "series", id: 7, raw: {} }))).toBeNull();
  });

  it("with a live token every URL is rebuilt from it; a coverless series keeps the host's URL", () => {
    __setMediaForTests({ token: "LIVE", baseUrl: "https://books.example", expiresUtc: new Date(Date.now() + 3600_000).toISOString(), username: "u" });
    const out = exploreWithLiveArt(payload());
    expect(out.spotlight[0].imageUrl).toBe("https://books.example/m/LIVE/thumbs/11.webp");
    expect(out.spotlight[0].imageThumbUrl).toBe("https://books.example/m/LIVE/thumbs/11.webp");
    expect(out.rails[0].items[0].imageUrl).toBe("https://books.example/m/LIVE/thumbs/42.webp");
    expect(out.rails[0].items[1].imageUrl).toBe("https://host/m/DEAD/thumbs/1.webp");
  });

  it("without a token the payload passes through untouched", () => {
    const input = payload();
    const out = exploreWithLiveArt(input);
    expect(out.spotlight[0].imageUrl).toBe(input.spotlight[0].imageUrl);
  });
});
