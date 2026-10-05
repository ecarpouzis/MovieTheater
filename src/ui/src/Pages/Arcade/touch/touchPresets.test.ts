import { describe, it, expect } from "vitest";
import { PAD } from "../cloudRetroClient";
import { SYSTEM_LABEL } from "../arcadeSystems";
import { compose } from "./touchEngine";
import {
  CUSTOM_PICK, defaultLayout, gameKeyFor, gamePickKey, parseStore, resolveLayout, sanitizeLayout, serializeStore,
  systemKey, systemPickKey, withLayout, withPick, fitStore, MAX_PICKS, MAX_STORE_CHARS, type ButtonControl, type Layout, type LayoutStore,
} from "./touchLayout";
import { arcadeButtonMap, parseControlProfile, presetsFor, suggestedPreset } from "./touchPresets";

const BUCKETS = [["landscape", 19.5 / 9], ["portrait", 9 / 19.5]] as const;
const bitOf = (l: Layout, label: string) => (l.controls.find((c) => c.kind === "button" && c.label === label) as ButtonControl | undefined)?.bits;

describe("control profiles", () => {
  it("parses what the arcade-controls CLI writes, and nothing else", () => {
    expect(parseControlProfile("joy8/6/sf")).toEqual({ kind: "joy8", buttons: 6, sf: true });
    expect(parseControlProfile("joy4/1")).toEqual({ kind: "joy4", buttons: 1, sf: false });
    expect(parseControlProfile("trackball/0")).toEqual({ kind: "trackball", buttons: 0, sf: false });
    expect(parseControlProfile("JOY8/2")).toEqual({ kind: "joy8", buttons: 2, sf: false });
    expect(parseControlProfile("joy9/2")).toBeNull();
    expect(parseControlProfile("")).toBeNull();
    expect(parseControlProfile(null)).toBeNull();
  });
});

describe("presetsFor", () => {
  const contexts = [{}, { controls: "joy8/6/sf" }, { controls: "joy4/1" }, { controls: "twin/0" }, { controls: "trackball/3" }, { controls: "joy8/8" }];

  it("every system has at least one preset, with unique ids, that builds a sane layout in both shapes", () => {
    for (const sys of Object.keys(SYSTEM_LABEL)) {
      for (const ctx of contexts) {
        const presets = presetsFor(sys, ctx);
        expect(presets.length, sys).toBeGreaterThan(0);
        expect(new Set(presets.map((p) => p.id)).size, `${sys} ids`).toBe(presets.length);
        expect(presets.map((p) => p.id)).toContain(suggestedPreset(sys, ctx));
        for (const p of presets) {
          for (const [bucket, aspect] of BUCKETS) {
            const l = defaultLayout(sys, bucket, aspect, p.spec);
            expect(sanitizeLayout(l), `${sys}/${p.id}/${bucket}`).toEqual(l);
            const ids = l.controls.map((c) => c.id);
            expect(new Set(ids).size, `${sys}/${p.id} duplicate ids`).toBe(ids.length);
            for (const c of l.controls) {
              expect(c.x, `${sys}/${p.id}/${c.id}`).toBeGreaterThanOrEqual(0); expect(c.x).toBeLessThanOrEqual(1);
              expect(c.y, `${sys}/${p.id}/${c.id}`).toBeGreaterThanOrEqual(0); expect(c.y).toBeLessThanOrEqual(1);
              if (c.kind === "button") for (const b of c.bits) expect(PAD[b], `${sys}/${p.id} ${b}`).toBeTypeOf("number");
            }
          }
        }
      }
    }
  });

  it("console systems with nothing to switch to keep a single standard preset", () => {
    expect(presetsFor("nes").map((p) => p.id)).toEqual(["standard"]);
    expect(suggestedPreset("nes")).toBe("standard");
  });

  it("the Genesis six-button preset puts X Y Z on the bits Genesis Plus GX reads (L, X, R)", () => {
    const six = presetsFor("genesis").find((p) => p.id === "six")!;
    const l = defaultLayout("genesis", "landscape", 2, six.spec);
    expect([bitOf(l, "X"), bitOf(l, "Y"), bitOf(l, "Z")]).toEqual([["L"], ["X"], ["R"]]);
    expect([bitOf(l, "A"), bitOf(l, "B"), bitOf(l, "C")]).toEqual([["Y"], ["B"], ["A"]]);
  });

  it("the PC Engine six-button preset carries the core's 2⇄6 switch (L2)", () => {
    const six = presetsFor("pce").find((p) => p.id === "six")!;
    expect(bitOf(defaultLayout("pce", "landscape", 2, six.spec), "2⇄6")).toEqual(["L2"]);
  });
});

describe("arcade presets", () => {
  it("number buttons in each core's real order (FBNeo/flycast 5 = R, libretro MAME 5 = L)", () => {
    expect(arcadeButtonMap("arcade").map.slice(0, 6)).toEqual(["B", "A", "Y", "X", "R", "L"]);
    expect(arcadeButtonMap("naomi").map.slice(0, 6)).toEqual(["B", "A", "Y", "X", "R", "L"]);
    expect(arcadeButtonMap("arcade", "mame_libretro").map).toEqual(["B", "A", "Y", "X", "L", "R"]);
    const b6 = presetsFor("arcade").find((p) => p.id === "b6")!;
    const l = defaultLayout("arcade", "landscape", 2, b6.spec);
    expect([bitOf(l, "5"), bitOf(l, "6")]).toEqual([["R"], ["L"]]);
  });

  it("suggests the preset the cabinet calls for", () => {
    expect(suggestedPreset("arcade", { controls: "joy4/0" })).toBe("joy4");
    expect(suggestedPreset("arcade", { controls: "joy8/6/sf" })).toBe("sf");
    expect(suggestedPreset("arcade", { controls: "joy8/6" })).toBe("b6");
    expect(suggestedPreset("arcade", { controls: "joy8/2" })).toBe("b2");
    expect(suggestedPreset("arcade", { controls: "twin/0" })).toBe("twin");
    expect(suggestedPreset("arcade", { controls: "trackball/1" })).toBe("trackball");
    expect(suggestedPreset("arcade", { controls: "spinner/1" })).toBe("spinner");
    expect(suggestedPreset("arcade", { controls: "gun/2" })).toBe("gun");
    expect(suggestedPreset("arcade", { controls: "other/0" })).toBe("b4");
    expect(suggestedPreset("arcade", {})).toBe("b4");
    // flycast has no Street Fighter layout: a 6-button NAOMI fighter gets numbered rows.
    expect(suggestedPreset("naomi", { controls: "joy8/6/sf" })).toBe("b6");
    expect(presetsFor("naomi").some((p) => p.id === "sf")).toBe(false);
  });

  it("a game with an unusual button count gets a preset with exactly that many", () => {
    const ids = presetsFor("arcade", { controls: "joy8/5" }).map((p) => p.id);
    expect(ids).toContain("b5");
    const b5 = presetsFor("arcade", { controls: "joy8/5" }).find((p) => p.id === "b5")!;
    expect(b5.spec.faceButtons).toHaveLength(5);
  });

  it("Street Fighter rows press FBNeo's SF bits: punches Y X L, kicks B A R", () => {
    const sf = presetsFor("arcade", { controls: "joy8/6/sf" }).find((p) => p.id === "sf")!;
    const l = defaultLayout("arcade", "landscape", 2, sf.spec);
    expect(["LP", "MP", "HP", "LK", "MK", "HK"].map((x) => bitOf(l, x)?.[0])).toEqual(["Y", "X", "L", "B", "A", "R"]);
    const lp = l.controls.find((c) => c.label === "LP")!, lk = l.controls.find((c) => c.label === "LK")!;
    expect(lp.y).toBeLessThan(lk.y); // punches on the top row
  });

  it("the 4-way preset's d-pad never presses a diagonal", () => {
    const joy4 = presetsFor("arcade", { controls: "joy4/1" }).find((p) => p.id === "joy4")!;
    const l = defaultLayout("arcade", "landscape", 2, joy4.spec);
    const pad = l.controls.find((c) => c.kind === "dpad")!;
    expect(pad.kind === "dpad" && pad.ways).toBe(4);
    const size = { w: 2000, h: 1000 };
    const cx = pad.x * size.w, cy = pad.y * size.h;
    const f = compose(l, size, { fingers: new Map([[1, { controlId: pad.id, x: cx + 60, y: cy - 50, ox: cx, oy: cy }]]), toggled: new Set(), turboOn: false });
    expect(f.mask).toBe(1 << PAD.RIGHT);
  });

  it("twin stick moves with the d-pad on the left and fires with the right stick", () => {
    const twin = presetsFor("arcade", { controls: "twin/0" }).find((p) => p.id === "twin")!;
    const l = defaultLayout("arcade", "landscape", 2, twin.spec);
    const zones = l.controls.filter((c) => c.kind === "region");
    expect(zones.map((z) => z.kind === "region" && z.output)).toEqual(["dpad", "right"]);
    expect(l.controls.some((c) => c.kind === "dpad")).toBe(false);
  });
});

describe("resolveLayout with presets and picks", () => {
  const base = { system: "arcade", inputSystem: "arcade", gameKey: "pacman", bucket: "landscape" as const, aspect: 2 };
  const presets = presetsFor("arcade", { controls: "joy4/0" });
  const edited: Layout = { ...defaultLayout("arcade", "landscape", 2), opacity: 0.33 };
  const empty: LayoutStore = { v: 1, layouts: {} };

  it("shows the suggested preset when nothing was picked or edited", () => {
    const r = resolveLayout(empty, { ...base, presets, suggested: "joy4" });
    expect([r.source, r.presetId, r.pickScope]).toEqual(["default", "joy4", null]);
  });

  it("a game pick beats a system pick beats an edited system layout", () => {
    let s = withLayout(empty, systemKey("arcade", "landscape"), edited);
    expect(resolveLayout(s, { ...base, presets, suggested: "joy4" }).source).toBe("system"); // legacy edit still wins over a suggestion
    s = withPick(s, systemPickKey("arcade"), "b6");
    expect(resolveLayout(s, { ...base, presets, suggested: "joy4" })).toMatchObject({ source: "preset", presetId: "b6", pickScope: "system" });
    s = withPick(s, gamePickKey("arcade", "pacman"), "joy4");
    expect(resolveLayout(s, { ...base, presets, suggested: "joy4" })).toMatchObject({ source: "preset", presetId: "joy4", pickScope: "game" });
  });

  it("'custom' as a pick brings back the edited layout", () => {
    let s = withLayout(empty, gameKeyFor("arcade", "pacman", "landscape"), edited);
    s = withPick(s, systemPickKey("arcade"), "b2");
    expect(resolveLayout(s, { ...base, presets }).source).toBe("game"); // game edit beats a SYSTEM pick
    s = withPick(s, gamePickKey("arcade", "pacman"), "b1");
    expect(resolveLayout(s, { ...base, presets }).presetId).toBe("b1");
    s = withPick(s, gamePickKey("arcade", "pacman"), CUSTOM_PICK);
    expect(resolveLayout(s, { ...base, presets }).layout.opacity).toBe(0.33);
  });

  it("a pick naming a preset this room doesn't have falls through", () => {
    const s = withPick(empty, gamePickKey("arcade", "pacman"), "nope");
    expect(resolveLayout(s, { ...base, presets, suggested: "joy4" }).presetId).toBe("joy4");
  });
});

describe("picks in the stored blob", () => {
  it("round-trip, are sanitized, and are capped to the newest", () => {
    let s: LayoutStore = { v: 1, layouts: {} };
    s = withPick(s, systemPickKey("genesis"), "six");
    s = withPick(s, gamePickKey("arcade", "sf2"), "sf");
    expect(parseStore(serializeStore(s))).toEqual(s);
    const dirty = JSON.stringify({ v: 1, layouts: {}, picks: { "sys:genesis": "six", "bad key": "x", "sys:snes": "BAD VALUE!", "game:arcade/x": 7 } });
    expect(parseStore(dirty).picks).toEqual({ "sys:genesis": "six" });
    for (let i = 0; i < MAX_PICKS + 5; i++) s = withPick(s, gamePickKey("arcade", `g${i}`), "b2");
    expect(Object.keys(s.picks!)).toHaveLength(MAX_PICKS);
    expect(s.picks![gamePickKey("arcade", `g${MAX_PICKS + 4}`)]).toBe("b2");
    expect(s.picks![systemPickKey("genesis")]).toBeUndefined(); // the stalest went first
  });

  it("a blob over the server's cap sheds its oldest picks, never a layout", () => {
    let s: LayoutStore = { v: 1, layouts: {} };
    for (let i = 0; i < 12; i++) s = withLayout(s, systemKey(`sys${i}`, "landscape"), defaultLayout("ps2", "landscape", 2));
    for (let i = 0; i < MAX_PICKS; i++) s = withPick(s, gamePickKey("snes", `${"x".repeat(150)}-${i}`), "fighter");
    expect(serializeStore(s).length).toBeGreaterThan(MAX_STORE_CHARS);
    const { store, json } = fitStore(s);
    expect(json.length).toBeLessThanOrEqual(MAX_STORE_CHARS);
    expect(Object.keys(store.layouts)).toHaveLength(12);
    expect(store.picks![gamePickKey("snes", `${"x".repeat(150)}-${MAX_PICKS - 1}`)]).toBe("fighter"); // the newest survive
    expect(store.picks![gamePickKey("snes", `${"x".repeat(150)}-0`)]).toBeUndefined();
    expect(fitStore(store).json).toBe(json); // already fits: untouched
  });

  it("clearing the last pick leaves a store with no picks key", () => {
    const s = withPick(withPick({ v: 1, layouts: {} }, "sys:snes", "fighter"), "sys:snes", null);
    expect(s).toEqual({ v: 1, layouts: {} });
  });
});

describe("no preset stacks one control on another", () => {
  // A phone held each way (iPhone 14-ish CSS px). Round controls only — a floating zone is meant to sit
  // under buttons' corners. 0.92 tolerates the hit circles just kissing, never a button half under another.
  it("on a phone, in both shapes, for every system and preset", () => {
    const bad: string[] = [];
    const phones = [["landscape", 844, 390], ["portrait", 390, 844]] as const;
    for (const sys of Object.keys(SYSTEM_LABEL)) {
      for (const ctx of [{}, { controls: "joy8/8" }, { controls: "joy8/6/sf" }, { controls: "joy8/5" }]) {
        for (const p of presetsFor(sys, ctx)) {
          for (const [bucket, w, h] of phones) {
            const l = defaultLayout(sys, bucket, w / h, p.spec);
            const u = Math.min(w, h);
            const round = l.controls.filter((c) => c.kind !== "region");
            for (let i = 0; i < round.length; i++) {
              for (let j = i + 1; j < round.length; j++) {
                const a = round[i], b = round[j];
                const d = Math.hypot((a.x - b.x) * w, (a.y - b.y) * h);
                if (d < ((a.s + b.s) * u / 2) * 0.92) bad.push(`${sys}/${p.id}/${bucket}: ${a.id} × ${b.id}`);
              }
            }
          }
        }
      }
    }
    expect([...new Set(bad)]).toEqual([]);
  });
});

describe("every round control is fully on screen", () => {
  it("on a phone, in both shapes, for every system and preset", () => {
    const off: string[] = [];
    for (const sys of Object.keys(SYSTEM_LABEL)) {
      for (const ctx of [{}, { controls: "joy8/8" }, { controls: "joy8/3" }]) {
        for (const p of presetsFor(sys, ctx)) {
          for (const [bucket, w, h] of [["landscape", 844, 390], ["portrait", 390, 844]] as const) {
            const u = Math.min(w, h);
            for (const c of defaultLayout(sys, bucket, w / h, p.spec).controls) {
              if (c.kind === "region") continue;
              const r = (c.s * u) / 2;
              if (c.x * w - r < -1 || c.x * w + r > w + 1) off.push(`${sys}/${p.id}/${bucket}: ${c.id}`);
            }
          }
        }
      }
    }
    expect([...new Set(off)]).toEqual([]);
  });
});
