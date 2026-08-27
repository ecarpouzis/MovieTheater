import {
  backdropExtraFor, getSectionSkin, resolveBackdrop, sectionSkinStyle, skinTweakExtras,
} from "../../catalog/skin/skin";
import { BOOKS_BACKDROPS, BOOKS_SKIN, booksSkinContext } from "./booksTheme";

beforeEach(() => window.localStorage.clear());

describe("Books/booksTheme — the nine backdrops, per view, under the site's light/dark authority", () => {
  it("registers the Long Box set for all three Books stores, with its scenes and its --books-* names", () => {
    expect(Object.keys(BOOKS_BACKDROPS).sort()).toEqual(["archive", "bone", "bookcase", "midnight", "paper", "pulp", "room", "slate", "snow"]);
    expect(BOOKS_BACKDROPS.room.family).toBe("light");
    expect(BOOKS_BACKDROPS.room.scene).toContain("/catalog/room-bg.svg");
    expect(BOOKS_BACKDROPS.archive.scene).toContain("/catalog/archive-bg.svg");
    for (const store of ["books", "books-novels", "books-kids"]) expect(getSectionSkin(store)).toBe(BOOKS_SKIN);
    expect(BOOKS_SKIN.tokenPrefix).toBe("books");
    // Books paints `.books-section` itself — the host must not paint a second surface inside it.
    expect(BOOKS_SKIN.paintHost).toBe(false);
  });

  it("offers all nine as one swatch grid, per view, marking the out-of-family ones", () => {
    const [backdrop, type] = skinTweakExtras("books", "light");
    expect(backdrop.render).toBe("swatch");
    expect(backdrop.perView).toBe(true);
    expect(backdrop.options).toHaveLength(9);
    expect(backdrop.options.filter((o) => !o.inactive).map((o) => o.value)).toEqual(["paper", "snow", "bone", "pulp", "room"]);
    expect(skinTweakExtras("books", "dark")[0].options.filter((o) => !o.inactive).map((o) => o.value))
      .toEqual(["slate", "midnight", "archive", "bookcase"]);
    expect(type.options.map((o) => o.value)).toEqual(["pulp", "newsprint", "stencil", "editorial"]);
  });

  it("a backdrop from the other family falls back to that family's default", () => {
    const skin = getSectionSkin("books");
    expect(resolveBackdrop(skin, "bookcase", "light")).toBe("paper");
    expect(resolveBackdrop(skin, "bookcase", "dark")).toBe("bookcase");
    expect(resolveBackdrop(skin, "nonsense", "dark")).toBe("slate");
  });

  it("the per-view choice wins over the section-wide one, and the style carries the scene", () => {
    const extras = { backdrop: "paper", "backdrop:shelf": "bookcase", display: "editorial" };
    expect(backdropExtraFor(extras, "shelf")).toBe("bookcase");
    expect(backdropExtraFor(extras, "grid")).toBe("paper");
    const shelf = sectionSkinStyle("books", extras, "dark", "shelf");
    expect(shelf["--books-bg"]).toBe(BOOKS_BACKDROPS.bookcase.bg);
    const grid = sectionSkinStyle("books", extras, "light", "grid");
    expect(grid["--books-bg"]).toBe(BOOKS_BACKDROPS.paper.bg);
    expect(sectionSkinStyle("books", { backdrop: "room" }, "light")["--books-scene"]).toContain("room-bg.svg");
    expect(grid["--books-display"]).toContain("Instrument Serif");
    // The modal wrap takes the site surface tokens too, so a sheet wears the skin with no CSS.
    expect(grid["--card-surface"]).toBe(BOOKS_BACKDROPS.paper.bg);
    expect(grid["--text-primary"]).toBe(BOOKS_BACKDROPS.paper.ink);
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
