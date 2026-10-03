/**
 * The touch-pad layout model: what a layout IS, the built-in default per system, and how a room picks
 * which layout to show. Pure — no DOM, no React — so every rule here is unit-tested.
 *
 * Geometry: a control's centre (x, y) is a FRACTION of the layer (the full player surface: the whole
 * screen in fullscreen), and its size `s` is a fraction of min(layerW, layerH) so a button stays round
 * and thumb-sized on any screen shape. A layout is stored per screen-shape BUCKET (landscape /
 * portrait), because a thumb position that's right on a phone held sideways is wrong held upright.
 *
 * Which layout a room shows (resolveLayout):
 *   1. game:<system>/<gameKey>|<bucket> — a per-game override (filenames repeat across systems, so the
 *      system is part of the key);
 *   2. sys:<inputSystem>|<bucket>       — the player's layout for this system;
 *   3. the built-in default for that system, generated for the layer's actual aspect.
 */
import { PAD } from "../cloudRetroClient";
import { touchSpecFor, type BitName, type FaceButton, type SystemTouchSpec } from "./touchSystems";

export type Bucket = "landscape" | "portrait";
export type StickOutput = "left" | "right" | "dpad";
export type ButtonMode = "press" | "toggle" | "turbo";
export type ButtonShape = "circle" | "pill" | "square";
export type RoomAction = "menu" | "quickSave" | "quickLoad" | "rewind" | "fastForward" | "reset";

/** A button can press RetroPad bits AND/OR push an axis (the N64's C-buttons are right-stick pushes). */
export interface AxisPush { i: 0 | 1 | 2 | 3; v: -1 | 1 }

interface Base { id: string; x: number; y: number; s: number; opacity?: number; label?: string }
export interface ButtonControl extends Base { kind: "button"; bits: BitName[]; axis?: AxisPush; shape: ButtonShape; mode: ButtonMode }
export interface DpadControl extends Base { kind: "dpad" }
export interface StickControl extends Base { kind: "stick"; output: StickOutput; floating: boolean; deadzone: number }
/** An invisible-by-default floating-stick zone: touching anywhere in it spawns a stick under the thumb. w/h are layer fractions. */
export interface RegionControl extends Base { kind: "region"; w: number; h: number; output: StickOutput; deadzone: number }
export interface ActionControl extends Base { kind: "action"; action: RoomAction }
export type Control = ButtonControl | DpadControl | StickControl | RegionControl | ActionControl;

export interface Layout {
  v: 1;
  controls: Control[];
  /** Global opacity multiplier, 0..1. */
  opacity: number;
  /** After this long with no touch, drop to `to` opacity. null = never fade. */
  idleFade: { afterMs: number; to: number } | null;
  haptics: boolean;
  /** Briefly outline an invisible control when it's pressed (so you can learn where it is). */
  flashOnPress: boolean;
  /** A thumb that starts on a button can slide onto a neighbouring button (face-button rolls). */
  slide: boolean;
}

export interface LayoutStore { v: 1; layouts: Record<string, Layout> }

export const ACTION_LABEL: Record<RoomAction, string> = {
  menu: "☰", quickSave: "Save", quickLoad: "Load", rewind: "⏪", fastForward: "⏩", reset: "Reset",
};

export const bucketFor = (w: number, h: number): Bucket => (w >= h ? "landscape" : "portrait");

export const systemKey = (inputSystem: string, bucket: Bucket) => `sys:${String(inputSystem || "").toLowerCase()}|${bucket}`;
export const gameKeyFor = (system: string, gameKey: string, bucket: Bucket) =>
  `game:${String(system || "").toLowerCase()}/${gameKey}|${bucket}`;

export interface ResolvedLayout { layout: Layout; source: "game" | "system" | "default" }

export function resolveLayout(
  store: LayoutStore | null | undefined,
  { system, inputSystem, gameKey, bucket, aspect }: { system: string; inputSystem: string; gameKey?: string | null; bucket: Bucket; aspect: number },
): ResolvedLayout {
  const all = store?.layouts || {};
  if (gameKey) {
    const g = all[gameKeyFor(system, gameKey, bucket)];
    if (g) return { layout: g, source: "game" };
  }
  const s = all[systemKey(inputSystem, bucket)];
  if (s) return { layout: s, source: "system" };
  return { layout: defaultLayout(inputSystem, bucket, aspect), source: "default" };
}

// ── Defaults ───────────────────────────────────────────────────────────────────────────────────────

interface Anchors {
  primary: [number, number, number];   // the main movement control: x, y, size
  secondary: [number, number, number]; // the other one, when a system has both d-pad and stick
  face: [number, number, number];      // face cluster centre + cluster size
  rstick: [number, number, number];
  faceWithR: [number, number, number]; // face cluster when a right stick/C-cluster takes the lower slot
  L: [number, number]; R: [number, number]; L2: [number, number]; R2: [number, number];
  select: [number, number]; start: [number, number];
  menu: [number, number];
  shoulderS: number; smallS: number; menuS: number;
}

const LANDSCAPE: Anchors = {
  primary: [0.13, 0.6, 0.32], secondary: [0.28, 0.83, 0.24],
  face: [0.87, 0.6, 0.36], faceWithR: [0.87, 0.56, 0.32], rstick: [0.72, 0.85, 0.24],
  // Nothing in the top-right corner: the room's fullscreen ✕ and ☰ live there.
  L: [0.07, 0.34], R: [0.93, 0.34], L2: [0.07, 0.2], R2: [0.93, 0.2],
  select: [0.43, 0.94], start: [0.57, 0.94], menu: [0.5, 0.06],
  shoulderS: 0.13, smallS: 0.1, menuS: 0.09,
};

const PORTRAIT: Anchors = {
  primary: [0.22, 0.76, 0.34], secondary: [0.4, 0.92, 0.22],
  face: [0.78, 0.76, 0.36], faceWithR: [0.78, 0.73, 0.32], rstick: [0.6, 0.92, 0.22],
  L: [0.09, 0.6], R: [0.91, 0.6], L2: [0.26, 0.6], R2: [0.74, 0.6],
  select: [0.42, 0.95], start: [0.58, 0.95], menu: [0.5, 0.6],
  shoulderS: 0.16, smallS: 0.12, menuS: 0.1,
};

// The DS/3DS picture IS a touchscreen, so in landscape everything is pushed into the side margins.
const LANDSCAPE_MARGINS: Partial<Anchors> = {
  primary: [0.11, 0.55, 0.26], secondary: [0.11, 0.85, 0.2],
  face: [0.89, 0.58, 0.28], faceWithR: [0.89, 0.58, 0.28],
  select: [0.8, 0.93], start: [0.91, 0.93], menu: [0.06, 0.08],
  L: [0.07, 0.25], R: [0.93, 0.25],
};

/** Face-button offsets in cluster units (each in −1..1 of the cluster's half-size), and the button size as a fraction of the cluster. */
function faceOffsets(arr: SystemTouchSpec["face"], count: number): { off: [number, number][]; size: number } {
  switch (arr) {
    case "one": return { off: [[0, 0]], size: 0.6 };
    case "two": return { off: [[-0.42, 0.28], [0.42, -0.28]], size: 0.5 };
    case "three": return { off: [[-0.72, 0.3], [0, 0.05], [0.72, -0.2]], size: 0.42 };
    case "six": return {
      off: [[-0.72, 0.42], [0, 0.3], [0.72, 0.18], [-0.72, -0.32], [0, -0.44], [0.72, -0.56]].slice(0, count) as [number, number][],
      size: 0.38,
    };
    case "gc": return { off: [[0, 0.05], [-0.68, 0.48], [0.68, -0.05], [-0.05, -0.68]], size: 0.42 };
    case "n64": return { off: [[0.3, 0.38], [-0.62, -0.3]], size: 0.46 };
    case "four":
    default: return { off: [[0, 0.62], [0.62, 0], [-0.62, 0], [0, -0.62]], size: 0.38 };
  }
}

let idSeq = 0;
/** A fresh control id. Stable enough for one layout; ids are only compared within a layout. */
export const newControlId = (prefix: string) => `${prefix}-${Date.now().toString(36)}-${(idSeq++).toString(36)}`;

const clamp01 = (n: number) => Math.max(0, Math.min(1, n));

/** The built-in layout for an input system, generated for the layer's actual aspect (w/h). */
export function defaultLayout(inputSystem: string, bucket: Bucket, aspect: number): Layout {
  const spec = touchSpecFor(inputSystem);
  const A: Anchors = bucket === "landscape" && spec.marginsOnly ? { ...LANDSCAPE, ...LANDSCAPE_MARGINS } : bucket === "landscape" ? LANDSCAPE : PORTRAIT;
  const a = aspect > 0 ? aspect : bucket === "landscape" ? 16 / 9 : 9 / 16;
  // One min-unit as a fraction of the layer's width / height.
  const ux = a >= 1 ? 1 / a : 1;
  const uy = a >= 1 ? 1 : a;
  const controls: Control[] = [];

  // Movement: the primary control gets the prime spot; when both exist the other sits below/inside.
  const stickAt = spec.primary === "stick" || !spec.dpad ? A.primary : A.secondary;
  const dpadAt = spec.primary === "dpad" || !spec.leftStick ? A.primary : A.secondary;
  if (spec.dpad) controls.push({ id: "dpad", kind: "dpad", x: dpadAt[0], y: dpadAt[1], s: dpadAt[2] });
  if (spec.leftStick) controls.push({ id: "lstick", kind: "stick", x: stickAt[0], y: stickAt[1], s: stickAt[2], output: "left", floating: false, deadzone: 0.12 });

  // Right side.
  const faceAt = spec.rightStick ? A.faceWithR : A.face;
  if (spec.rightStick === "stick") {
    controls.push({ id: "rstick", kind: "stick", x: A.rstick[0], y: A.rstick[1], s: A.rstick[2], output: "right", floating: false, deadzone: 0.12 });
  } else if (spec.rightStick === "cbuttons") {
    // Four C-buttons as right-stick pushes, in their own little diamond.
    const [cx, cy, cs] = A.rstick;
    const half = cs / 2;
    const cS = cs * 0.42;
    const cb: [string, number, number, AxisPush][] = [["C↑", 0, -0.62, { i: 3, v: -1 }], ["C↓", 0, 0.62, { i: 3, v: 1 }], ["C←", -0.62, 0, { i: 2, v: -1 }], ["C→", 0.62, 0, { i: 2, v: 1 }]];
    for (const [label, dx, dy, axis] of cb) {
      controls.push({
        id: `c-${label}`, kind: "button", label, bits: [], axis, shape: "circle", mode: "press",
        x: clamp01(cx + dx * half * ux), y: clamp01(cy + dy * half * uy), s: cS,
      });
    }
  }
  const { off, size } = faceOffsets(spec.face, spec.faceButtons.length);
  const half = faceAt[2] / 2;
  spec.faceButtons.forEach((b, i) => {
    const o = off[i] || [0, 0];
    controls.push({
      id: `face-${b.bit}`, kind: "button", label: b.label, bits: [b.bit], shape: "circle", mode: "press",
      x: clamp01(faceAt[0] + o[0] * half * ux), y: clamp01(faceAt[1] + o[1] * half * uy), s: faceAt[2] * size,
    });
  });

  for (const k of ["L", "R", "L2", "R2"] as const) {
    const label = spec.shoulders[k];
    if (!label) continue;
    const [x, y] = A[k];
    controls.push({ id: `sh-${k}`, kind: "button", label, bits: [k], shape: "pill", mode: "press", x, y, s: A.shoulderS });
  }
  if (spec.select) controls.push({ id: "select", kind: "button", label: spec.select, bits: ["SELECT"], shape: "pill", mode: "press", x: A.select[0], y: A.select[1], s: A.smallS });
  if (spec.start) controls.push({ id: "start", kind: "button", label: spec.start, bits: ["START"], shape: "pill", mode: "press", x: A.start[0], y: A.start[1], s: A.smallS });
  controls.push({ id: "menu", kind: "action", action: "menu", x: A.menu[0], y: A.menu[1], s: A.menuS });

  return { v: 1, controls, opacity: 0.7, idleFade: null, haptics: true, flashOnPress: true, slide: true };
}

/** Controls the editor can add for this system: every palette/face/shoulder bit not already on the layout, plus the generic kinds. */
export function addableButtons(inputSystem: string, layout: Layout): FaceButton[] {
  const spec = touchSpecFor(inputSystem);
  const placed = new Set<string>();
  for (const c of layout.controls) if (c.kind === "button" && c.bits.length === 1) placed.add(c.bits[0]);
  const all: FaceButton[] = [...spec.faceButtons];
  for (const [bit, label] of Object.entries(spec.shoulders)) all.push({ bit: bit as BitName, label: label as string });
  if (spec.select) all.push({ bit: "SELECT", label: spec.select });
  if (spec.start) all.push({ bit: "START", label: spec.start });
  all.push(...(spec.palette || []));
  const seen = new Set<string>();
  return all.filter((b) => !placed.has(b.bit) && !seen.has(b.bit) && (seen.add(b.bit), true));
}

// ── Sanitising (a stored blob is user-authored data) ───────────────────────────────────────────────

const BIT_NAMES = new Set(Object.keys(PAD));
const ACTIONS = new Set<RoomAction>(["menu", "quickSave", "quickLoad", "rewind", "fastForward", "reset"]);
const num = (v: unknown, lo: number, hi: number, dflt: number) => {
  const n = typeof v === "number" && Number.isFinite(v) ? v : dflt;
  return Math.max(lo, Math.min(hi, n));
};
const MAX_CONTROLS = 64;

// A stored blob is untyped user data, read field by field below.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function sanitizeControl(raw: any): Control | null {
  if (!raw || typeof raw !== "object") return null;
  const base = {
    id: typeof raw.id === "string" && raw.id ? raw.id.slice(0, 64) : newControlId("c"),
    x: num(raw.x, 0, 1, 0.5), y: num(raw.y, 0, 1, 0.5), s: num(raw.s, 0.04, 0.6, 0.15),
    ...(raw.opacity != null ? { opacity: num(raw.opacity, 0, 1, 1) } : null),
    ...(typeof raw.label === "string" ? { label: raw.label.slice(0, 16) } : null),
  };
  const output: StickOutput = raw.output === "right" || raw.output === "dpad" ? raw.output : "left";
  switch (raw.kind) {
    case "button": {
      const bits = (Array.isArray(raw.bits) ? raw.bits : []).filter((b: unknown) => typeof b === "string" && BIT_NAMES.has(b)).slice(0, 4) as BitName[];
      const axis = raw.axis && [0, 1, 2, 3].includes(raw.axis.i) && (raw.axis.v === 1 || raw.axis.v === -1) ? { i: raw.axis.i, v: raw.axis.v } as AxisPush : undefined;
      if (bits.length === 0 && !axis) return null;
      return {
        ...base, kind: "button", bits, ...(axis ? { axis } : null),
        shape: raw.shape === "pill" || raw.shape === "square" ? raw.shape : "circle",
        mode: raw.mode === "toggle" || raw.mode === "turbo" ? raw.mode : "press",
      };
    }
    case "dpad": return { ...base, kind: "dpad" };
    case "stick": return { ...base, kind: "stick", output, floating: !!raw.floating, deadzone: num(raw.deadzone, 0, 0.6, 0.12) };
    case "region": return { ...base, kind: "region", output, w: num(raw.w, 0.05, 1, 0.4), h: num(raw.h, 0.05, 1, 0.5), deadzone: num(raw.deadzone, 0, 0.6, 0.12) };
    case "action": return ACTIONS.has(raw.action) ? { ...base, kind: "action", action: raw.action } : null;
    default: return null;
  }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function sanitizeLayout(raw: any): Layout | null {
  if (!raw || typeof raw !== "object" || !Array.isArray(raw.controls)) return null;
  const controls = raw.controls.slice(0, MAX_CONTROLS).map(sanitizeControl).filter(Boolean) as Control[];
  const ids = new Set<string>();
  for (const c of controls) { if (ids.has(c.id)) c.id = newControlId("c"); ids.add(c.id); }
  const fade = raw.idleFade && typeof raw.idleFade === "object"
    ? { afterMs: num(raw.idleFade.afterMs, 500, 60000, 4000), to: num(raw.idleFade.to, 0, 1, 0.15) }
    : null;
  return {
    v: 1, controls, opacity: num(raw.opacity, 0, 1, 0.7), idleFade: fade,
    haptics: raw.haptics !== false, flashOnPress: raw.flashOnPress !== false, slide: raw.slide !== false,
  };
}

const KEY_RE = /^(sys:[a-z0-9]+|game:[a-z0-9]+\/.{1,200})\|(landscape|portrait)$/;

export function parseStore(json: string | null | undefined): LayoutStore {
  const empty: LayoutStore = { v: 1, layouts: {} };
  if (!json) return empty;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  let raw: any;
  try { raw = JSON.parse(json); } catch { return empty; }
  if (!raw || typeof raw !== "object" || !raw.layouts || typeof raw.layouts !== "object") return empty;
  const layouts: Record<string, Layout> = {};
  for (const [k, v] of Object.entries(raw.layouts)) {
    if (!KEY_RE.test(k)) continue;
    const l = sanitizeLayout(v);
    if (l) layouts[k] = l;
  }
  return { v: 1, layouts };
}

/** Round geometry to 4 places on the way out — a stored blob of 0.1300000000000001s is just bigger. */
export function serializeStore(store: LayoutStore): string {
  return JSON.stringify(store, (k, v) => (typeof v === "number" && !Number.isInteger(v) ? Math.round(v * 10000) / 10000 : v));
}

export function withLayout(store: LayoutStore, key: string, layout: Layout | null): LayoutStore {
  const layouts = { ...store.layouts };
  if (layout) layouts[key] = layout; else delete layouts[key];
  return { v: 1, layouts };
}
