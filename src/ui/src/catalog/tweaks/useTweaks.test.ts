import { act, renderHook } from "@testing-library/react";
import useTweaks, { DEFAULT_TWEAKS, hoverClass, loadTweaks, scaleFor, SCALE_TOUCH_DEFAULT, storageKeyFor } from "./useTweaks";

describe("catalog/useTweaks — device-scoped, per section, per pointer class", () => {
  beforeEach(() => { window.localStorage.clear(); });

  it("starts from the defaults and survives a corrupt entry", () => {
    expect(loadTweaks("movies")).toEqual(DEFAULT_TWEAKS);
    window.localStorage.setItem(storageKeyFor("movies"), "{not json");
    expect(loadTweaks("movies")).toEqual(DEFAULT_TWEAKS);
    window.localStorage.setItem(storageKeyFor("movies"), JSON.stringify({ hover: "bogus", rounded: "yes", scale: { grid: { fine: 99 } } }));
    const t = loadTweaks("movies");
    expect(t.hover).toBe("lift");
    expect(t.rounded).toBe(true);
    expect(t.scale.grid?.fine).toBe(2.5); // clamped
  });

  it("keeps a separate scale per view and per pointer class, with the touch default smaller", () => {
    const t = { ...DEFAULT_TWEAKS, scale: { grid: { fine: 1.4 } } };
    expect(scaleFor(t, "grid", false)).toBe(1.4);
    expect(scaleFor(t, "grid", true)).toBe(SCALE_TOUCH_DEFAULT);
    expect(scaleFor(t, "wall", false)).toBe(1);
  });

  it("writes through to storage under the section's key, and sections do not share", () => {
    const { result } = renderHook(() => useTweaks("music"));
    act(() => { result.current.update({ hover: "zoom", rounded: false }); });
    act(() => { result.current.setCoverScale("wall", 1.25); });
    const stored = JSON.parse(window.localStorage.getItem(storageKeyFor("music")) ?? "{}");
    expect(stored.hover).toBe("zoom");
    expect(stored.rounded).toBe(false);
    expect(stored.scale.wall.fine ?? stored.scale.wall.coarse).toBe(1.25);
    expect(window.localStorage.getItem(storageKeyFor("movies"))).toBeNull();
    expect(result.current.coverScale("wall")).toBe(1.25);
  });

  it("names the per-card hover class in one place, and dim/none emit none", () => {
    expect(hoverClass("lift")).toBe("bx-hover-lift");
    expect(hoverClass("tilt")).toBe("bx-hover-tilt");
    expect(hoverClass("dim")).toBe("");
    expect(hoverClass("none")).toBe("");
  });
});
