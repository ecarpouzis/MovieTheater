import { BOOKS_BACKDROPS, backdropExtraFor, booksSkinContext, booksThemeStyle, booksTweakExtras, resolveBackdrop } from "./booksTheme";

beforeEach(() => window.localStorage.clear());

describe("Books/booksTheme — the nine backdrops, per view, under the site's light/dark authority", () => {
  it("carries every Long Box backdrop, in the right family, with its scene where it had one", () => {
    expect(Object.keys(BOOKS_BACKDROPS).sort()).toEqual(["archive", "bone", "bookcase", "midnight", "paper", "pulp", "room", "slate", "snow"]);
    expect(BOOKS_BACKDROPS.room.family).toBe("light");
    expect(BOOKS_BACKDROPS.room.scene).toContain("/catalog/room-bg.svg");
    expect(BOOKS_BACKDROPS.archive.scene).toContain("/catalog/archive-bg.svg");
    expect(booksTweakExtras("light")[0].options.map((o) => o.value)).toEqual(["paper", "snow", "bone", "pulp", "room"]);
    expect(booksTweakExtras("dark")[0].options.map((o) => o.value)).toEqual(["slate", "midnight", "archive", "bookcase"]);
    expect(booksTweakExtras("light")[0].perView).toBe(true);
  });

  it("a backdrop from the other family falls back to that family's default", () => {
    expect(resolveBackdrop("bookcase", "light")).toBe("paper");
    expect(resolveBackdrop("bookcase", "dark")).toBe("bookcase");
    expect(resolveBackdrop("nonsense", "dark")).toBe("slate");
  });

  it("the per-view choice wins over the section-wide one, and the style carries the scene", () => {
    const extras = { backdrop: "paper", "backdrop:shelf": "bookcase", display: "editorial" };
    expect(backdropExtraFor(extras, "shelf")).toBe("bookcase");
    expect(backdropExtraFor(extras, "grid")).toBe("paper");
    const shelf = booksThemeStyle(extras, "dark", "shelf");
    expect(shelf["--books-bg"]).toBe(BOOKS_BACKDROPS.bookcase.bg);
    const grid = booksThemeStyle(extras, "light", "grid");
    expect(grid["--books-bg"]).toBe(BOOKS_BACKDROPS.paper.bg);
    expect(booksThemeStyle({ backdrop: "room" }, "light")["--books-scene"]).toContain("room-bg.svg");
    expect(grid["--books-display"]).toContain("Instrument Serif");
  });

  it("resolves the store and the view from the URL, the stored default, then the source's default", () => {
    expect(booksSkinContext("/books", "?view=wall")).toEqual({ store: "books", view: "wall" });
    expect(booksSkinContext("/books", "")).toEqual({ store: "books", view: "extended" });
    expect(booksSkinContext("/books/novels", "")).toEqual({ store: "books-novels", view: "grid" });
    expect(booksSkinContext("/books/kids", "?mode=browse")).toEqual({ store: "books-kids", view: "shelf" });
    expect(booksSkinContext("/books/explore", "?seed=3")).toEqual({ store: "books", view: "extended" });
    window.localStorage.setItem("catalog.view.v1:books", JSON.stringify({ view: "shelf" }));
    expect(booksSkinContext("/books/shelf", "").view).toBe("shelf");
  });
});
