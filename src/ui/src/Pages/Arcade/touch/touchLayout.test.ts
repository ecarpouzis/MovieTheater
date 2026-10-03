import { describe, it, expect } from "vitest";
import { PAD } from "../cloudRetroClient";
import { SYSTEM_LABEL } from "../arcadeSystems";
import { SYSTEM_TOUCH_SPECS, touchSpecFor, bitChoicesFor } from "./touchSystems";
import {
  addableButtons, bucketFor, defaultLayout, gameKeyFor, parseStore, resolveLayout, sanitizeLayout,
  serializeStore, systemKey, withLayout, type Layout,
} from "./touchLayout";

const BUCKETS = [["landscape", 19.5 / 9], ["portrait", 9 / 19.5]] as const;

describe("touchSystems", () => {
  it("has a spec for every system the arcade knows", () => {
    for (const sys of Object.keys(SYSTEM_LABEL)) expect(SYSTEM_TOUCH_SPECS[sys], sys).toBeTruthy();
  });

  it("only names bits the RetroPad has", () => {
    for (const [sys, spec] of Object.entries(SYSTEM_TOUCH_SPECS)) {
      const bits = [...spec.faceButtons, ...(spec.palette || [])].map((b) => b.bit);
      bits.push(...(Object.keys(spec.shoulders) as never[]));
      for (const b of bits) expect(PAD[b as keyof typeof PAD], `${sys}:${b}`).toBeTypeOf("number");
    }
  });

  it("falls back to a SNES-shaped pad for an unknown system", () => {
    expect(touchSpecFor("nope").faceButtons).toHaveLength(4);
  });

  it("offers every RetroPad bit once in the binding picker, console names first", () => {
    const choices = bitChoicesFor("ps1");
    expect(choices[0]).toEqual({ bit: "B", label: "✕" });
    expect(new Set(choices.map((c) => c.bit)).size).toBe(choices.length);
    expect(choices).toHaveLength(Object.keys(PAD).length);
  });
});

describe("defaultLayout", () => {
  it("builds an on-screen, sane layout for every system in both buckets", () => {
    for (const sys of Object.keys(SYSTEM_TOUCH_SPECS)) {
      for (const [bucket, aspect] of BUCKETS) {
        const l = defaultLayout(sys, bucket, aspect);
        expect(sanitizeLayout(l), `${sys}/${bucket}`).toEqual(l); // round-trips through the sanitizer untouched
        const ids = l.controls.map((c) => c.id);
        expect(new Set(ids).size, `${sys} duplicate ids`).toBe(ids.length);
        for (const c of l.controls) {
          expect(c.x).toBeGreaterThanOrEqual(0); expect(c.x).toBeLessThanOrEqual(1);
          expect(c.y).toBeGreaterThanOrEqual(0); expect(c.y).toBeLessThanOrEqual(1);
        }
        expect(l.controls.some((c) => c.kind === "action" && c.action === "menu"), `${sys} has a menu`).toBe(true);
      }
    }
  });

  it("leaves the top-right corner to the room's fullscreen ✕ and ☰", () => {
    for (const sys of Object.keys(SYSTEM_TOUCH_SPECS)) {
      for (const [bucket, aspect] of BUCKETS) {
        for (const c of defaultLayout(sys, bucket, aspect).controls) {
          // Chrome = two 44 px buttons, 12 px in, at the top right: ~0.84+ across, ~0.16- down on a phone.
          const inCorner = c.x > 0.82 && c.y < (bucket === "landscape" ? 0.17 : 0.08);
          expect(inCorner, `${sys}/${bucket} ${c.id} at ${c.x},${c.y}`).toBe(false);
        }
      }
    }
  });

  it("puts every face button the system has on the pad", () => {
    const l = defaultLayout("snes", "landscape", 2);
    const bits = l.controls.flatMap((c) => (c.kind === "button" ? c.bits : []));
    for (const b of ["B", "A", "Y", "X", "L", "R", "SELECT", "START"]) expect(bits).toContain(b);
  });

  it("gives N64 a stick and four C-buttons that push the right stick", () => {
    const l = defaultLayout("n64", "landscape", 2);
    expect(l.controls.some((c) => c.kind === "stick" && c.output === "left")).toBe(true);
    const cs = l.controls.filter((c) => c.kind === "button" && c.axis);
    expect(cs).toHaveLength(4);
    expect(cs.every((c) => c.kind === "button" && (c.axis!.i === 2 || c.axis!.i === 3))).toBe(true);
    expect(l.controls.some((c) => c.kind === "dpad")).toBe(false);
  });

  it("keeps DS controls in the side margins in landscape (the picture is a touchscreen)", () => {
    const l = defaultLayout("nds", "landscape", 16 / 9);
    // DS picture = 2:3 portrait, centred: on 16:9 it spans x ∈ [0.3125, 0.6875].
    for (const c of l.controls) expect(c.x < 0.31 || c.x > 0.69, `${c.id} at ${c.x}`).toBe(true);
  });

  it("stays circular-ish on any aspect: a face diamond's spread is equal in px both ways", () => {
    const aspect = 2;
    const l = defaultLayout("snes", "landscape", aspect);
    const b = l.controls.find((c) => c.id === "face-B")!, x = l.controls.find((c) => c.id === "face-X")!;
    const a = l.controls.find((c) => c.id === "face-A")!, y = l.controls.find((c) => c.id === "face-Y")!;
    const H = 100, W = H * aspect;
    expect(Math.abs((b.y - x.y) * H - (a.x - y.x) * W)).toBeLessThan(0.5);
  });

  it("lists the palette's unplaced buttons as addable, and not the placed ones", () => {
    const l = defaultLayout("ps1", "landscape", 2);
    expect(addableButtons("ps1", l).map((b) => b.bit)).toEqual(["L3", "R3"]);
  });
});

describe("resolveLayout", () => {
  const custom = (opacity: number): Layout => ({ ...defaultLayout("snes", "landscape", 2), opacity });

  it("prefers the game override, then the system layout, then the default", () => {
    let store = parseStore(null);
    const ctx = { system: "snes", inputSystem: "snes", gameKey: "Super Metroid", bucket: "landscape" as const, aspect: 2 };
    expect(resolveLayout(store, ctx).source).toBe("default");
    store = withLayout(store, systemKey("snes", "landscape"), custom(0.3));
    expect(resolveLayout(store, ctx)).toMatchObject({ source: "system", layout: { opacity: 0.3 } });
    store = withLayout(store, gameKeyFor("snes", "Super Metroid", "landscape"), custom(0.1));
    expect(resolveLayout(store, ctx)).toMatchObject({ source: "game", layout: { opacity: 0.1 } });
    expect(resolveLayout(store, { ...ctx, bucket: "portrait" }).source).toBe("default");
    expect(resolveLayout(store, { ...ctx, gameKey: "Zelda" }).source).toBe("system");
  });

  it("keys the system layout by INPUT system (Wii in GC scheme uses the GC layout)", () => {
    const store = withLayout(parseStore(null), systemKey("gc", "landscape"), custom(0.2));
    const r = resolveLayout(store, { system: "wii", inputSystem: "gc", gameKey: "Project REX", bucket: "landscape", aspect: 2 });
    expect(r.source).toBe("system");
  });

  it("buckets by the layer's shape", () => {
    expect(bucketFor(800, 400)).toBe("landscape");
    expect(bucketFor(400, 800)).toBe("portrait");
  });
});

describe("store parsing", () => {
  it("round-trips through serialize/parse", () => {
    const store = withLayout(parseStore(null), systemKey("n64", "portrait"), defaultLayout("n64", "portrait", 0.5));
    const back = parseStore(serializeStore(store));
    expect(Object.keys(back.layouts)).toEqual(["sys:n64|portrait"]);
    expect(back.layouts["sys:n64|portrait"].controls.length).toBe(store.layouts["sys:n64|portrait"].controls.length);
  });

  it("survives garbage: bad JSON, bad keys, bad controls", () => {
    expect(parseStore("{nope")).toEqual({ v: 1, layouts: {} });
    const raw = JSON.stringify({
      layouts: {
        "evil|landscape": { controls: [] },
        "sys:snes|landscape": {
          controls: [
            { kind: "button", bits: ["B", "NOT_A_BIT"], x: 9, y: -3, s: 5 },
            { kind: "button", bits: [] },                      // presses nothing → dropped
            { kind: "action", action: "formatDisk" },          // unknown action → dropped
            { kind: "stick", output: "sideways", deadzone: 4 },
            "string",
          ],
          opacity: 7,
        },
      },
    });
    const s = parseStore(raw);
    expect(Object.keys(s.layouts)).toEqual(["sys:snes|landscape"]);
    const l = s.layouts["sys:snes|landscape"];
    expect(l.opacity).toBe(1);
    expect(l.controls).toHaveLength(2);
    expect(l.controls[0]).toMatchObject({ kind: "button", bits: ["B"], x: 1, y: 0, s: 0.6 });
    expect(l.controls[1]).toMatchObject({ kind: "stick", output: "left", deadzone: 0.6 });
  });

  it("deleting a key removes the override", () => {
    const k = gameKeyFor("nes", "Contra", "landscape");
    const s = withLayout(withLayout(parseStore(null), k, defaultLayout("nes", "landscape", 2)), k, null);
    expect(s.layouts).toEqual({});
  });
});
