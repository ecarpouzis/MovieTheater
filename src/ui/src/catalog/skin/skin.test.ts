import {
  applySectionSkin, backdropExtraFor, crossFamilyPick, getSectionSkin, registerSectionSkin,
  resolveBackdrop, resolveType, sectionSkinStyle, skinTweakExtras, skinTokens, skinViewFor, siteTheme,
  type SectionSkin,
} from "./skin";
import "./sectionSkins";
import { loadTweaks, storageKeyFor } from "../tweaks/useTweaks";

const TEST: SectionSkin = {
  backdrops: {
    site: { family: "any", label: "Site", color: "var(--content-bg)", siteDefault: true },
    day: { family: "light", label: "Day", bg: "#ffffff", ink: "#111111", sub: "#666666", line: "#eeeeee", chrome: "#fefefe" },
    night: { family: "dark", label: "Night", bg: "#101010", card: "#181818", ink: "#eeeeee", sub: "#999999", line: "#222222", chrome: "#121212", scene: "#101010 url(\"/x.svg\")" },
  },
  defaults: { light: "site", dark: "site" },
  types: {
    site: { label: "Site", display: "", header: "", mono: "", tracking: "", weight: "", siteDefault: true },
    slab: { label: "Slab", display: '"Slab"', header: '"Slab"', mono: "monospace", tracking: "0em", weight: "700" },
  },
  defaultType: "site",
  perView: true,
  tokenPrefix: "test",
};

beforeEach(() => {
  window.localStorage.clear();
  document.documentElement.removeAttribute("data-theme");
  registerSectionSkin("skin-test", TEST);
});

describe("catalog/skin — register, resolve, apply, persist", () => {
  it("registers a section's set and reads the site theme off <html>", () => {
    expect(getSectionSkin("skin-test")).toBe(TEST);
    expect(getSectionSkin("nothing-here")).toBeUndefined();
    expect(siteTheme()).toBe("light");
    document.documentElement.dataset.theme = "dark";
    expect(siteTheme()).toBe("dark");
  });

  it("falls back across the light/dark family, and `any` fits both", () => {
    expect(resolveBackdrop(TEST, "night", "light")).toBe("site");
    expect(resolveBackdrop(TEST, "night", "dark")).toBe("night");
    expect(resolveBackdrop(TEST, "day", "light")).toBe("day");
    expect(resolveBackdrop(TEST, "site", "dark")).toBe("site");
    expect(resolveBackdrop(TEST, "made-up", "light")).toBe("site");
    expect(resolveType(TEST, "nope")).toBe("site");
    expect(resolveType(TEST, "slab")).toBe("slab");
  });

  it("offers nine-wide swatch rows: all options, the out-of-family ones flagged", () => {
    const [backdrop, type] = skinTweakExtras("skin-test", "light");
    expect(backdrop.render).toBe("swatch");
    expect(backdrop.perView).toBe(true);
    expect(backdrop.options.map((o) => [o.value, o.inactive])).toEqual([["site", false], ["day", false], ["night", true]]);
    expect(backdrop.options[0].color).toBe("var(--content-bg)");
    expect(type.render).toBeUndefined();
    expect(skinTweakExtras("no-such-section", "light")).toEqual([]);
  });

  it("the section's own surface writes NOTHING — a default install is the site, untouched", () => {
    expect(skinTokens("skin-test", {}, "light")).toEqual({});
    expect(sectionSkinStyle("skin-test", {}, "light")).toEqual({});
    expect(skinTokens("skin-test", { backdrop: "night" }, "light")).toEqual({}); // family fallback → site
  });

  it("writes the canonical tokens and the section's prefixed aliases, per view", () => {
    const t = skinTokens("skin-test", { backdrop: "day", "backdrop:shelf": "night", display: "slab" }, "dark", "shelf");
    expect(t["--skin-bg"]).toBe("#101010");
    expect(t["--skin-card"]).toBe("#181818");
    expect(t["--skin-scene"]).toContain("/x.svg");
    expect(t["--test-bg"]).toBe("#101010");
    expect(t["--skin-display"]).toBe('"Slab"');
    expect(t["--test-weight"]).toBe("700");
    // The section-wide choice is what a view with no memory of its own gets.
    expect(skinTokens("skin-test", { backdrop: "day", "backdrop:shelf": "night" }, "light", "grid")["--skin-bg"]).toBe("#ffffff");
    expect(backdropExtraFor({ backdrop: "day", "backdrop:shelf": "night" }, "shelf")).toBe("night");
  });

  it("applies to a root and REMOVES what it no longer sets", () => {
    const root = document.createElement("div");
    applySectionSkin(root, "skin-test", { backdrop: "day" }, "light", "grid");
    expect(root.dataset.catalogSkin).toBe("day");
    expect(root.dataset.skinPaint).toBe("1");
    expect(root.style.getPropertyValue("--skin-bg")).toBe("#ffffff");
    expect(root.style.getPropertyValue("--test-bg")).toBe("#ffffff");
    applySectionSkin(root, "skin-test", { backdrop: "site" }, "light", "grid");
    expect(root.dataset.catalogSkin).toBe("site");
    expect(root.dataset.skinPaint).toBeUndefined();
    expect(root.style.getPropertyValue("--skin-bg")).toBe("");
    expect(root.style.getPropertyValue("--test-bg")).toBe("");
    // A section with no skin leaves the root alone entirely.
    applySectionSkin(root, "no-such-section", {}, "light");
    expect(root.dataset.catalogSkin).toBeUndefined();
  });

  it("a paintHost:false section never paints the host box (Books paints its own root)", () => {
    registerSectionSkin("skin-test-nopaint", { ...TEST, paintHost: false });
    const root = document.createElement("div");
    applySectionSkin(root, "skin-test-nopaint", { backdrop: "day" }, "light", "grid");
    expect(root.dataset.catalogSkin).toBe("day");
    expect(root.dataset.skinPaint).toBeUndefined();
    expect(root.style.getPropertyValue("--skin-bg")).toBe("#ffffff");
  });

  it("the modal style repoints the SITE surface tokens so a sheet takes the skin with no CSS", () => {
    const s = sectionSkinStyle("skin-test", { backdrop: "day", display: "slab" }, "light", "grid");
    expect(s["--card-surface"]).toBe("#ffffff");
    expect(s["--card-border"]).toBe("#eeeeee");
    expect(s["--text-primary"]).toBe("#111111");
    expect(s["--text-muted"]).toBe("#666666");
    expect(s["--font-display"]).toBe('"Slab"');
    expect(s["--ink"]).toBe("#111111");
  });

  it("a cross-family pick is reported so the host can ask the site to switch theme", () => {
    expect(crossFamilyPick("skin-test", "backdrop:grid", "night", "light")).toBe("dark");
    expect(crossFamilyPick("skin-test", "backdrop", "day", "light")).toBeNull();
    expect(crossFamilyPick("skin-test", "backdrop", "site", "light")).toBeNull();
    expect(crossFamilyPick("skin-test", "display", "slab", "light")).toBeNull();
  });

  it("persists through the catalog's own tweaks store, and reads the view from the URL", () => {
    window.localStorage.setItem(storageKeyFor("skin-test"), JSON.stringify({ extras: { "backdrop:wall": "day" } }));
    expect(loadTweaks("skin-test").extras["backdrop:wall"]).toBe("day");
    expect(skinTokens("skin-test", loadTweaks("skin-test").extras, "light", "wall")["--skin-bg"]).toBe("#ffffff");
    expect(skinViewFor("skin-test", "?view=wall")).toBe("wall");
    window.localStorage.setItem("catalog.view.v1:skin-test", JSON.stringify({ view: "list" }));
    expect(skinViewFor("skin-test", "")).toBe("list");
    expect(skinViewFor("no-such-section", "", "extended")).toBe("extended");
  });

  it("a corrupt store is just the defaults — never a throw out of the skin", () => {
    window.localStorage.setItem(storageKeyFor("skin-test"), "{not json");
    expect(loadTweaks("skin-test").extras).toEqual({});
    expect(skinTokens("skin-test", loadTweaks("skin-test").extras, "light", "grid")).toEqual({});
  });
});
