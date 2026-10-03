import { describe, it, expect } from "vitest";
import { PAD } from "../cloudRetroClient";
import { axesToDpad, compose, dpadMask, hitTest, MAX_AXIS, stickAxes, type EngineState, type Finger } from "./touchEngine";
import type { Layout } from "./touchLayout";

const bit = (n: keyof typeof PAD) => 1 << PAD[n];
const size = { w: 1000, h: 500 }; // min = 500

const layout: Layout = {
  v: 1, opacity: 1, idleFade: null, haptics: false, flashOnPress: false, slide: true,
  controls: [
    { id: "a", kind: "button", bits: ["A"], shape: "circle", mode: "press", x: 0.9, y: 0.5, s: 0.2 },  // r = 50px at (900, 250)
    { id: "b", kind: "button", bits: ["B"], shape: "circle", mode: "press", x: 0.8, y: 0.6, s: 0.2 },  // (800, 300)
    { id: "turbo", kind: "button", bits: ["Y"], shape: "circle", mode: "turbo", x: 0.8, y: 0.2, s: 0.1 },
    { id: "tog", kind: "button", bits: ["X"], shape: "circle", mode: "toggle", x: 0.9, y: 0.2, s: 0.1 },
    { id: "cup", kind: "button", bits: [], axis: { i: 3, v: -1 }, shape: "circle", mode: "press", x: 0.7, y: 0.1, s: 0.1 },
    { id: "dpad", kind: "dpad", x: 0.15, y: 0.5, s: 0.4 },                                             // r = 100 at (150, 250)
    { id: "ls", kind: "stick", output: "left", floating: false, deadzone: 0.1, x: 0.4, y: 0.7, s: 0.4 }, // (400, 350)
    { id: "zone", kind: "region", output: "right", deadzone: 0, x: 0.5, y: 0.25, w: 0.3, h: 0.5, s: 0.2 },
    { id: "menu", kind: "action", action: "menu", x: 0.5, y: 0.05, s: 0.08 },
  ],
};

const state = (fingers: [string, number, number, number?, number?][], extra: Partial<EngineState> = {}): EngineState => ({
  fingers: new Map(fingers.map(([controlId, x, y, ox, oy], i) => [i, { controlId, x, y, ox: ox ?? x, oy: oy ?? y } as Finger])),
  toggled: new Set(), turboOn: false, ...extra,
});

describe("hitTest", () => {
  it("finds the button under the thumb, with a little slop outside its circle", () => {
    expect(hitTest(layout, size, 900, 250)?.id).toBe("a");
    expect(hitTest(layout, size, 900 + 55, 250)?.id).toBe("a");   // 1.1 r — inside the slop
    expect(hitTest(layout, size, 900 + 70, 250)).toBeNull();        // 1.4 r — out
  });

  it("picks the nearer-centred of two overlapping buttons", () => {
    expect(hitTest(layout, size, 860, 270)?.id).toBe("a");
    expect(hitTest(layout, size, 830, 290)?.id).toBe("b");
  });

  it("ranks a region below any round control inside it", () => {
    expect(hitTest(layout, size, 500, 25)?.id).toBe("menu");
    expect(hitTest(layout, size, 500, 150)?.id).toBe("zone");
  });
});

describe("dpad and stick geometry", () => {
  it("rests neutral near the centre and gives 8 directions", () => {
    expect(dpadMask(10, 0, 100)).toBe(0);
    expect(dpadMask(80, 0, 100)).toBe(bit("RIGHT"));
    expect(dpadMask(0, -80, 100)).toBe(bit("UP"));
    expect(dpadMask(-60, 60, 100)).toBe(bit("LEFT") | bit("DOWN"));
    expect(dpadMask(60, -60, 100)).toBe(bit("UP") | bit("RIGHT"));
  });

  it("rescales past the deadzone and clamps at the ring", () => {
    expect(stickAxes(5, 0, 100, 0.1)).toEqual([0, 0]);
    expect(stickAxes(200, 0, 100, 0.1)).toEqual([MAX_AXIS, 0]);
    const [x] = stickAxes(55, 0, 100, 0.1);
    expect(x).toBe(Math.trunc(0.5 * MAX_AXIS));
  });

  it("turns a stick past half deflection into d-pad bits", () => {
    expect(axesToDpad(10000, 0)).toBe(0);
    expect(axesToDpad(30000, 0)).toBe(bit("RIGHT"));
  });
});

describe("compose", () => {
  it("presses every held button at once (multi-touch)", () => {
    const f = compose(layout, size, state([["a", 900, 250], ["b", 800, 300]]));
    expect(f.mask).toBe(bit("A") | bit("B"));
    expect([...f.lit].sort()).toEqual(["a", "b"]);
  });

  it("reads the d-pad from where the thumb is on it", () => {
    expect(compose(layout, size, state([["dpad", 150, 160]])).mask).toBe(bit("UP"));
  });

  it("deflects a fixed stick from its centre, a region from where the thumb landed", () => {
    const f = compose(layout, size, state([["ls", 600, 350], ["zone", 500, 100, 500, 160]]));
    expect(f.axes[0]).toBe(MAX_AXIS);          // 200 px right of a 100 px stick → full
    expect(f.axes[1]).toBe(0);
    expect(f.axes[3]).toBe(-MAX_AXIS);         // 60 px up from the landing point, ring 60 px → full up
  });

  it("C-buttons push their axis", () => {
    expect(compose(layout, size, state([["cup", 700, 50]])).axes[3]).toBe(-MAX_AXIS);
  });

  it("turbo presses only on the turbo phase; toggles press while latched with no finger", () => {
    expect(compose(layout, size, state([["turbo", 800, 100]])).mask).toBe(0);
    expect(compose(layout, size, state([["turbo", 800, 100]], { turboOn: true })).mask).toBe(bit("Y"));
    const f = compose(layout, size, state([], { toggled: new Set(["tog"]) }));
    expect(f.mask).toBe(bit("X"));
    expect(f.lit.has("tog")).toBe(true);
  });

  it("an action lights up but presses nothing", () => {
    const f = compose(layout, size, state([["menu", 500, 25]]));
    expect(f.mask).toBe(0);
    expect(f.lit.has("menu")).toBe(true);
  });

  it("ignores a finger on a control that was deleted mid-touch", () => {
    expect(compose(layout, size, state([["gone", 1, 1]])).mask).toBe(0);
  });
});
