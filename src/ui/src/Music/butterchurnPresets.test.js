import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import {
  DEFAULT_POOL,
  POOLS,
  __resetPresetCaches,
  fetchPreset,
  loadPresetIndex,
  pickRandom,
  presetsInPool,
  searchPresets,
  splitPresetName,
} from "./butterchurnPresets";

// The visualizer went from a bundled 100-preset pack to ~1,750 individually-fetched static presets.
// Everything that used to be "it's just an object in the bundle" is now a network call against a
// path that, on this SPA, answers 200 with index.html when it's wrong — so the guards below are the
// point of this file, not a formality.

const INDEX = [
  { s: "geiss-waterfall", n: "Geiss - Waterfall", t: 0 },
  { s: "rovastar-fractopia", n: "Rovastar - Fractopia", t: 1 },
  { s: "flexi-dark-matter", n: "flexi - dark matter", t: 2 },
  { s: "unnamed-thing", n: "unnamed thing", t: 2 },
];

const PRESET = { baseVals: { zoom: 1 }, pixel_eqs_str: "", warp: "", comp: "", waves: [], shapes: [] };

function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, text: () => Promise.resolve(typeof body === "string" ? body : JSON.stringify(body)) };
}

let fetchMock;

beforeEach(() => {
  __resetPresetCaches();
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("splitPresetName", () => {
  it("splits an 'author - title' preset name", () => {
    expect(splitPresetName("Geiss - Waterfall")).toEqual({ author: "Geiss", title: "Waterfall" });
  });

  it("splits on the FIRST separator so a title keeps its own dashes", () => {
    expect(splitPresetName("martin - mandelbox - high speed")).toEqual({
      author: "martin",
      title: "mandelbox - high speed",
    });
  });

  it("treats a name with no author as all title", () => {
    expect(splitPresetName("124")).toEqual({ author: "", title: "124" });
  });

  it("survives a missing name", () => {
    expect(splitPresetName(undefined)).toEqual({ author: "", title: "" });
  });
});

describe("presetsInPool", () => {
  it("featured is the base pack only", () => {
    expect(presetsInPool(INDEX, "featured", new Set()).map((p) => p.s)).toEqual(["geiss-waterfall"]);
  });

  it("classic includes featured — the tiers nest rather than partition", () => {
    expect(presetsInPool(INDEX, "classic", new Set()).map((p) => p.s)).toEqual([
      "geiss-waterfall",
      "rovastar-fractopia",
    ]);
  });

  it("everything is everything", () => {
    expect(presetsInPool(INDEX, "all", new Set())).toHaveLength(4);
  });

  it("favorites ignores tier entirely — starring an archive preset must keep it reachable", () => {
    const favorites = new Set(["flexi-dark-matter"]);
    expect(presetsInPool(INDEX, "favorites", favorites).map((p) => p.s)).toEqual(["flexi-dark-matter"]);
  });

  it("accepts favorites as a plain array too", () => {
    expect(presetsInPool(INDEX, "favorites", ["geiss-waterfall"])).toHaveLength(1);
  });

  it("has a default pool that is one of the declared pools", () => {
    expect(POOLS.some((p) => p.id === DEFAULT_POOL)).toBe(true);
  });
});

describe("searchPresets", () => {
  it("returns everything for an empty query", () => {
    expect(searchPresets(INDEX, "")).toHaveLength(4);
  });

  it("matches case-insensitively", () => {
    expect(searchPresets(INDEX, "GEISS").map((p) => p.s)).toEqual(["geiss-waterfall"]);
  });

  it("requires every term, in any order — 'waterfall geiss' still finds it", () => {
    expect(searchPresets(INDEX, "waterfall geiss").map((p) => p.s)).toEqual(["geiss-waterfall"]);
  });

  it("matches across the author/title separator", () => {
    expect(searchPresets(INDEX, "dark")).toHaveLength(1);
  });

  it("returns nothing when a term misses", () => {
    expect(searchPresets(INDEX, "geiss nonsense")).toEqual([]);
  });
});

describe("pickRandom", () => {
  it("returns null for an empty list", () => {
    expect(pickRandom([], null)).toBeNull();
  });

  it("returns the only entry even when it is the one to avoid", () => {
    expect(pickRandom([INDEX[0]], "geiss-waterfall")).toBe(INDEX[0]);
  });

  it("avoids repeating the current preset", () => {
    for (let i = 0; i < 30; i += 1) {
      expect(pickRandom(INDEX, "geiss-waterfall").s).not.toBe("geiss-waterfall");
    }
  });
});

describe("loadPresetIndex", () => {
  it("fetches the catalogue once and shares it", async () => {
    fetchMock.mockResolvedValue(jsonResponse({ presets: INDEX }));
    const [a, b] = await Promise.all([loadPresetIndex(), loadPresetIndex()]);
    expect(a).toEqual(INDEX);
    expect(b).toBe(a);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe("/butterchurn/index.json");
  });

  it("rejects the SPA fallback rather than treating index.html as a catalogue", async () => {
    // The static host answers unknown paths with 200 + index.html, so 'response.ok' proves nothing.
    fetchMock.mockResolvedValue(jsonResponse("<!doctype html><html></html>"));
    await expect(loadPresetIndex()).rejects.toThrow(/did not return JSON/);
  });

  it("rejects an index with no presets", async () => {
    fetchMock.mockResolvedValue(jsonResponse({ presets: [] }));
    await expect(loadPresetIndex()).rejects.toThrow(/empty/);
  });

  it("does not cache a failure — the next open retries", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse("nope", { ok: false, status: 500 }));
    await expect(loadPresetIndex()).rejects.toThrow(/HTTP 500/);
    fetchMock.mockResolvedValueOnce(jsonResponse({ presets: INDEX }));
    await expect(loadPresetIndex()).resolves.toEqual(INDEX);
  });
});

describe("fetchPreset", () => {
  it("fetches a preset by slug", async () => {
    fetchMock.mockResolvedValue(jsonResponse(PRESET));
    await expect(fetchPreset("geiss-waterfall")).resolves.toEqual(PRESET);
    expect(fetchMock.mock.calls[0][0]).toBe("/butterchurn/presets/geiss-waterfall.json");
  });

  it("caches, so shuffling back to a preset costs nothing", async () => {
    fetchMock.mockResolvedValue(jsonResponse(PRESET));
    await fetchPreset("geiss-waterfall");
    await fetchPreset("geiss-waterfall");
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("shares one request between concurrent callers", async () => {
    fetchMock.mockResolvedValue(jsonResponse(PRESET));
    await Promise.all([fetchPreset("x"), fetchPreset("x"), fetchPreset("x")]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("rejects HTML instead of handing index.html to loadPreset", async () => {
    fetchMock.mockResolvedValue(jsonResponse("<!doctype html>"));
    await expect(fetchPreset("missing")).rejects.toThrow(/did not return JSON/);
  });

  it("rejects JSON that is not a butterchurn preset", async () => {
    fetchMock.mockResolvedValue(jsonResponse({ hello: "world" }));
    await expect(fetchPreset("wrong")).rejects.toThrow(/did not return a butterchurn preset/);
  });

  it("accepts a preset whose shaders are empty strings (Milkdrop 1 presets have no custom shader)", async () => {
    fetchMock.mockResolvedValue(jsonResponse({ baseVals: {}, pixel_eqs_str: "", warp: "", comp: "" }));
    await expect(fetchPreset("md1")).resolves.toBeTruthy();
  });

  it("does not cache a failure", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse("boom", { ok: false, status: 404 }));
    await expect(fetchPreset("flaky")).rejects.toThrow(/HTTP 404/);
    fetchMock.mockResolvedValueOnce(jsonResponse(PRESET));
    await expect(fetchPreset("flaky")).resolves.toEqual(PRESET);
  });
});
