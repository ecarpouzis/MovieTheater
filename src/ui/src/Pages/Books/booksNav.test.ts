import { booksNavGroups, booksSection, booksViewLabel, isKidAccount, kidAllowedPath } from "./booksNav";

describe("Books/booksNav — the section index and the kid pinning", () => {
  it("maps URLs onto views, the reader included, unknown → browse", () => {
    expect(booksSection("/books")).toBe("browse");
    expect(booksSection("/books/")).toBe("browse");
    expect(booksSection("/books/explore?seed=3")).toBe("explore");
    expect(booksSection("/books/read/12")).toBe("read");
    expect(booksSection("/books/nope")).toBe("browse");
    expect(booksViewLabel("shelf")).toBe("Shelf");
  });

  it("a kid account is a ceiling of 0 without admin, and may only be in Kids, the shelf, or a reader", () => {
    expect(isKidAccount({ booksMaturityCeiling: 0 })).toBe(true);
    expect(isKidAccount({ booksMaturityCeiling: 0, isAdmin: true })).toBe(false);
    expect(isKidAccount({ booksMaturityCeiling: 3 })).toBe(false);
    expect(kidAllowedPath("/books/kids")).toBe(true);
    expect(kidAllowedPath("/books/shelf?tab=read")).toBe(true);
    expect(kidAllowedPath("/books/read/9")).toBe(true);
    expect(kidAllowedPath("/books")).toBe(false);
    expect(kidAllowedPath("/books/admin")).toBe(false);
  });

  it("the index is gated: nothing without the grant, Kids+Shelf for a kid, Admin only for admins, counts attached", () => {
    expect(booksNavGroups({ booksAccess: false })).toEqual([]);
    const kid = booksNavGroups({ booksAccess: true, booksMaturityCeiling: 0 }, { continueReading: 2 });
    expect(kid.flatMap((g) => g.views.map((v) => v.key))).toEqual(["kids", "shelf"]);
    expect(kid[0].views[1]).toMatchObject({ count: 2, waiting: true });
    const adult = booksNavGroups({ booksAccess: true, booksMaturityCeiling: 3 }, { catalog: 118926, novels: 22084 });
    expect(adult.map((g) => g.key)).toEqual(["library", "yours"]);
    expect(adult[0].views.map((v) => v.key)).toEqual(["explore", "browse", "novels", "kids"]);
    expect(adult[0].views[1].count).toBe(118926);
    const admin = booksNavGroups({ booksAccess: true, booksMaturityCeiling: 3, isAdmin: true });
    expect(admin.map((g) => g.key)).toEqual(["library", "yours", "operate"]);
  });
});
