/**
 * Touch → RetroPad. Pure geometry: given a layout, the layer's size and the fingers currently down,
 * produce the frame the touch pad is holding ({mask, axes}) plus which controls are lit. TouchControls
 * owns the DOM and the pointer events; everything that decides WHAT a touch means lives here, so it is
 * unit-tested without a browser.
 */
import { PAD } from "../cloudRetroClient";
import type { Control, Layout, StickOutput } from "./touchLayout";

export interface LayerSize { w: number; h: number }

/** A finger that is down. (x, y) is its current position, (ox, oy) where it landed — px, layer-relative. */
export interface Finger { controlId: string; x: number; y: number; ox: number; oy: number }

export interface EngineState {
  fingers: Map<number, Finger>;
  /** Toggle-mode buttons currently latched on. */
  toggled: Set<string>;
  /** Turbo phase: turbo buttons press only while this is true (the caller flips it ~15 Hz). */
  turboOn: boolean;
}

export interface Frame { mask: number; axes: [number, number, number, number]; lit: Set<string> }

export const MAX_AXIS = 32767;
const unit = (size: LayerSize) => Math.min(size.w, size.h);

/** Centre + radius (px) of a round control. */
export function geom(c: Control, size: LayerSize) {
  return { cx: c.x * size.w, cy: c.y * size.h, r: (c.s * unit(size)) / 2 };
}

/** Floating-stick radius for regions (and floating sticks, whose ring is drawn where the thumb lands). */
export const floatingRadius = (c: Control, size: LayerSize) =>
  c.kind === "region" ? 0.12 * unit(size) : (c.s * unit(size)) / 2;

/** Hit slop: a touch this far outside a button's drawn circle still counts (thumbs are imprecise). */
const SLOP = 1.15;

function contains(c: Control, size: LayerSize, x: number, y: number): number {
  if (c.kind === "region") {
    const x0 = (c.x - c.w / 2) * size.w, x1 = (c.x + c.w / 2) * size.w;
    const y0 = (c.y - c.h / 2) * size.h, y1 = (c.y + c.h / 2) * size.h;
    return x >= x0 && x <= x1 && y >= y0 && y <= y1 ? Number.MAX_SAFE_INTEGER : -1; // lowest priority
  }
  const { cx, cy, r } = geom(c, size);
  const d = Math.hypot(x - cx, y - cy);
  return d <= r * SLOP ? d / Math.max(1, r) : -1;
}

/**
 * The control under (x, y): the nearest-centred round control whose (slop-padded) circle contains it,
 * else a region containing it. `filter` narrows the candidates (slide re-hits buttons only).
 */
export function hitTest(layout: Layout, size: LayerSize, x: number, y: number, filter?: (c: Control) => boolean): Control | null {
  let best: Control | null = null;
  let bestScore = Infinity;
  for (const c of layout.controls) {
    if (filter && !filter(c)) continue;
    const score = contains(c, size, x, y);
    if (score >= 0 && score < bestScore) { best = c; bestScore = score; }
  }
  return best;
}

const bit = (name: keyof typeof PAD) => 1 << PAD[name];

/**
 * D-pad from a vector (8-way unless `ways` = 4). Inside 25 % of the radius is neutral (a resting thumb presses nothing);
 * outside, each of the eight 45° sectors maps to its direction — so a diagonal is as easy to hit as a
 * cardinal, like a real cross pad rocked onto its corner.
 */
export function dpadMask(dx: number, dy: number, r: number, ways: 4 | 8 = 8): number {
  if (Math.hypot(dx, dy) < r * 0.25) return 0;
  // 4-way (Pac-Man, Donkey Kong): the dominant axis wins outright, so a thumb drifting off the cardinal
  // never presses a diagonal — on a 4-way cabinet a diagonal is "no new direction", which stalls a turn.
  if (ways === 4) {
    if (Math.abs(dx) >= Math.abs(dy)) return dx > 0 ? bit("RIGHT") : bit("LEFT");
    return dy > 0 ? bit("DOWN") : bit("UP");
  }
  const sector = Math.round(Math.atan2(dy, dx) / (Math.PI / 4)); // −4..4, 0 = right, 2 = down
  switch ((sector + 8) % 8) {
    case 0: return bit("RIGHT");
    case 1: return bit("RIGHT") | bit("DOWN");
    case 2: return bit("DOWN");
    case 3: return bit("DOWN") | bit("LEFT");
    case 4: return bit("LEFT");
    case 5: return bit("LEFT") | bit("UP");
    case 6: return bit("UP");
    default: return bit("UP") | bit("RIGHT");
  }
}

/**
 * Analog stick from a vector: a radial deadzone (fraction of the radius) and the remaining travel
 * rescaled so the edge of the deadzone reads as 0 rather than jumping to `deadzone`. Clamped to the
 * ring — dragging past it holds full deflection in that direction.
 */
export function stickAxes(dx: number, dy: number, r: number, deadzone: number): [number, number] {
  const d = Math.hypot(dx, dy);
  if (r <= 0 || d <= r * deadzone) return [0, 0];
  const mag = Math.min(1, (d / r - deadzone) / (1 - deadzone));
  return [Math.trunc((dx / d) * mag * MAX_AXIS), Math.trunc((dy / d) * mag * MAX_AXIS)];
}

/** A stick whose output is "dpad": past half deflection, its 8-way direction presses the d-pad bits. */
export function axesToDpad(ax: number, ay: number): number {
  if (Math.hypot(ax, ay) < MAX_AXIS * 0.5) return 0;
  return dpadMask(ax, ay, MAX_AXIS);
}

function applyStick(out: Frame, output: StickOutput, ax: number, ay: number) {
  if (output === "dpad") { out.mask |= axesToDpad(ax, ay); return; }
  const i = output === "left" ? 0 : 2;
  // Two thumbs on two sticks of the same output: the larger deflection wins per axis.
  if (Math.abs(ax) > Math.abs(out.axes[i])) out.axes[i] = ax;
  if (Math.abs(ay) > Math.abs(out.axes[i + 1])) out.axes[i + 1] = ay;
}

const clampAxis = (n: number) => Math.max(-MAX_AXIS, Math.min(MAX_AXIS, n));

/** The frame the touch pad is holding right now. Action controls light up but press no bits. */
export function compose(layout: Layout, size: LayerSize, state: EngineState): Frame {
  const out: Frame = { mask: 0, axes: [0, 0, 0, 0], lit: new Set() };
  const byId = new Map(layout.controls.map((c) => [c.id, c] as const));
  const pushes: [number, number, number, number] = [0, 0, 0, 0];
  const pressButton = (c: Control) => {
    if (c.kind !== "button") return;
    for (const b of c.bits) out.mask |= bit(b);
    if (c.axis) pushes[c.axis.i] += c.axis.v * MAX_AXIS;
  };

  for (const f of state.fingers.values()) {
    const c = byId.get(f.controlId);
    if (!c) continue;
    out.lit.add(c.id);
    switch (c.kind) {
      case "button":
        if (c.mode === "press" || (c.mode === "turbo" && state.turboOn)) pressButton(c);
        break;
      case "dpad": {
        const { cx, cy, r } = geom(c, size);
        out.mask |= dpadMask(f.x - cx, f.y - cy, r, c.ways === 4 ? 4 : 8);
        break;
      }
      case "stick": {
        const { cx, cy, r } = geom(c, size);
        const [ox, oy] = c.floating ? [f.ox, f.oy] : [cx, cy];
        const [ax, ay] = stickAxes(f.x - ox, f.y - oy, r, c.deadzone);
        applyStick(out, c.output, ax, ay);
        break;
      }
      case "region": {
        const [ax, ay] = stickAxes(f.x - f.ox, f.y - f.oy, floatingRadius(c, size), c.deadzone);
        applyStick(out, c.output, ax, ay);
        break;
      }
      default: break; // actions: lit only
    }
  }
  // Latched toggles press whether or not a finger is on them.
  for (const id of state.toggled) {
    const c = byId.get(id);
    if (c && c.kind === "button" && c.mode === "toggle") { pressButton(c); out.lit.add(id); }
  }
  for (let i = 0; i < 4; i++) {
    if (pushes[i] === 0) continue;
    const p = clampAxis(pushes[i]);
    if (Math.abs(p) > Math.abs(out.axes[i])) out.axes[i] = p;
  }
  return out;
}

/** Stick knob offset (px, relative to its ring centre) for drawing — the clamped finger vector. */
export function knobOffset(f: Finger, c: Control, size: LayerSize): { ox: number; oy: number; dx: number; dy: number } {
  const r = floatingRadius(c, size);
  const { cx, cy } = geom(c, size);
  const floating = c.kind === "region" || (c.kind === "stick" && c.floating);
  const ox = floating ? f.ox : cx, oy = floating ? f.oy : cy;
  let dx = f.x - ox, dy = f.y - oy;
  const d = Math.hypot(dx, dy);
  if (d > r) { dx = (dx / d) * r; dy = (dy / d) * r; }
  return { ox, oy, dx, dy };
}
