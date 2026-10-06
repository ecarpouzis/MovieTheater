/**
 * The touch-pad layout model: what a layout IS, the built-in default per system, and how a room picks
 * which layout to show. Pure — no DOM, no React — so every rule here is unit-tested.
 *
 * Geometry: a control's centre (x, y) is a FRACTION of the layer (the full player surface: the whole
 * screen in fullscreen), and its size `s` is a fraction of min(layerW, layerH) so a button stays round
 * and thumb-sized on any screen shape. A layout is stored per screen-shape BUCKET (landscape /
 * portrait), because a thumb position that's right on a phone held sideways is wrong held upright.
 *
 * Built-in layouts come in named PRESETS (touchPresets.ts): a Genesis has "3 buttons" and "6 buttons", an
 * arcade game has one per control style. A player can PICK a preset for one game or for a whole system;
 * picks live beside the layouts in the same blob (`picks`, keyed WITHOUT a bucket — a preset is generated
 * for whatever shape the screen is). "custom" as a pick means "my edited layout".
 *
 * Which layout a room shows (resolveLayout):
 *   1. the game's pick (picks["game:<system>/<gameKey>"]) — a preset, or "custom" = the edited layout;
 *   2. game:<system>/<gameKey>|<bucket> — a per-game edited layout (filenames repeat across systems, so the
 *      system is part of the key);
 *   3. the system's pick (picks["sys:<inputSystem>"]) when it names a preset;
 *   4. sys:<inputSystem>|<bucket>       — the player's edited layout for this system;
 *   5. the SUGGESTED preset — for an arcade game, the one its MAME control profile calls for (a 4-way stick
 *      for Pac-Man, two rows for Street Fighter, a trackball zone for Centipede); otherwise the system's
 *      first preset — generated for the layer's actual aspect.
 */
import { PAD } from "../cloudRetroClient";
import { touchSpecFor, type BitName, type FaceButton, type SystemTouchSpec } from "./touchSystems";
import type { TouchPreset } from "./touchPresets";

export type Bucket = "landscape" | "portrait";
export type StickOutput = "left" | "right" | "dpad";
export type ButtonMode = "press" | "toggle" | "turbo";
export type ButtonShape = "circle" | "pill" | "square";
export type RoomAction = "menu" | "quickSave" | "quickLoad" | "rewind" | "fastForward" | "reset";

/** A button can press RetroPad bits AND/OR push an axis (the N64's C-buttons are right-stick pushes). */
export interface AxisPush { i: 0 | 1 | 2 | 3; v: -1 | 1 }

interface Base { id: string; x: number; y: number; s: number; opacity?: number; label?: string }
export interface ButtonControl extends Base { kind: "button"; bits: BitName[]; axis?: AxisPush; shape: ButtonShape; mode: ButtonMode }
/** ways 4 = only ever one direction at a time (a 4-way joystick); absent = 8-way. */
export interface DpadControl extends Base { kind: "dpad"; ways?: 4 }
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

/**
 * layouts: edited layouts by `sys:…|bucket` / `game:…|bucket`. picks: the chosen preset id (or "custom")
 * by `sys:<inputSystem>` / `game:<system>/<gameKey>` — no bucket.
 */
export interface LayoutStore { v: 1; layouts: Record<string, Layout>; picks?: Record<string, string> }

/** The pick value meaning "my edited layout" rather than a built-in preset. */
export const CUSTOM_PICK = "custom";

export const ACTION_LABEL: Record<RoomAction, string> = {
  menu: "☰", quickSave: "Save", quickLoad: "Load", rewind: "⏪", fastForward: "⏩", reset: "Reset",
};

export const bucketFor = (w: number, h: number): Bucket => (w >= h ? "landscape" : "portrait");

export const systemKey = (inputSystem: string, bucket: Bucket) => `sys:${String(inputSystem || "").toLowerCase()}|${bucket}`;
export const gameKeyFor = (system: string, gameKey: string, bucket: Bucket) =>
  `game:${String(system || "").toLowerCase()}/${gameKey}|${bucket}`;

export const systemPickKey = (inputSystem: string) => `sys:${String(inputSystem || "").toLowerCase()}`;
export const gamePickKey = (system: string, gameKey: string) => `game:${String(system || "").toLowerCase()}/${gameKey}`;

/**
 * source: "game"/"system" = an edited layout at that scope; "preset" = a preset the player picked;
 * "default" = the suggested preset nobody picked. presetId = the preset on screen (null for an edited
 * layout). pickScope = where the deciding pick lives, if a pick decided it.
 */
export interface ResolvedLayout {
  layout: Layout;
  source: "game" | "system" | "preset" | "default";
  presetId: string | null;
  pickScope: "game" | "system" | null;
}

export interface ResolveArgs {
  system: string; inputSystem: string; gameKey?: string | null; bucket: Bucket; aspect: number;
  /** This room's presets (presetsFor) — absent = the system's built-in spec as the only preset. */
  presets?: TouchPreset[];
  /** Which preset to show when nothing was picked or edited. Absent/unknown = the first. */
  suggested?: string | null;
  /** Where the game picture sits on the layer — generated layouts keep the minor buttons off it. */
  picture?: PictureRect | null;
}

export function resolveLayout(store: LayoutStore | null | undefined, a: ResolveArgs): ResolvedLayout {
  const { system, inputSystem, gameKey, bucket, aspect, picture } = a;
  const all = store?.layouts || {};
  const picks = store?.picks || {};
  const presets = a.presets && a.presets.length ? a.presets : null;
  const presetById = (id: string | null | undefined) => (id && presets ? presets.find((p) => p.id === id) || null : null);
  const fromPreset = (p: TouchPreset, source: ResolvedLayout["source"], pickScope: ResolvedLayout["pickScope"]): ResolvedLayout =>
    ({ layout: defaultLayout(inputSystem, bucket, aspect, p.spec, picture), source, presetId: p.id, pickScope });
  const gameLayout = gameKey ? all[gameKeyFor(system, gameKey, bucket)] : undefined;
  const sysLayout = all[systemKey(inputSystem, bucket)];

  const gamePick = gameKey ? picks[gamePickKey(system, gameKey)] : undefined;
  if (gamePick === CUSTOM_PICK) {
    if (gameLayout) return { layout: gameLayout, source: "game", presetId: null, pickScope: "game" };
    if (sysLayout) return { layout: sysLayout, source: "system", presetId: null, pickScope: "game" };
  } else {
    const p = presetById(gamePick);
    if (p) return fromPreset(p, "preset", "game");
  }
  if (gameLayout) return { layout: gameLayout, source: "game", presetId: null, pickScope: null };

  const sysPick = picks[systemPickKey(inputSystem)];
  const sp = sysPick !== CUSTOM_PICK ? presetById(sysPick) : null;
  if (sp) return fromPreset(sp, "preset", "system");
  if (sysLayout) return { layout: sysLayout, source: "system", presetId: null, pickScope: sysPick === CUSTOM_PICK ? "system" : null };

  const suggested = presetById(a.suggested) || (presets ? presets[0] : null);
  if (suggested) return fromPreset(suggested, "default", null);
  return { layout: defaultLayout(inputSystem, bucket, aspect, undefined, picture), source: "default", presetId: null, pickScope: null };
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
  shoulderS: number; smallS: number;
}

const LANDSCAPE: Anchors = {
  primary: [0.13, 0.6, 0.32], secondary: [0.28, 0.83, 0.24],
  face: [0.87, 0.6, 0.36], faceWithR: [0.87, 0.56, 0.32], rstick: [0.72, 0.85, 0.24],
  // Nothing in the top-right corner: the room's fullscreen ✕ and ☰ live there.
  L: [0.07, 0.34], R: [0.93, 0.34], L2: [0.07, 0.2], R2: [0.93, 0.2],
  select: [0.43, 0.94], start: [0.57, 0.94],
  shoulderS: 0.13, smallS: 0.1,
};

// Select/Start (Coin/Start) start at the bottom edge: they're pressed a few times a game, so they get the
// space furthest from the picture and the thumbs. Where a system's secondary stick / C-buttons already
// own the bottom edge, settle() walks them to the nearest free spot (the overlap check in touchPresets.test.ts).
const PORTRAIT: Anchors = {
  primary: [0.22, 0.76, 0.34], secondary: [0.38, 0.93, 0.22],
  face: [0.78, 0.76, 0.36], faceWithR: [0.78, 0.73, 0.32], rstick: [0.62, 0.93, 0.22],
  L: [0.09, 0.6], R: [0.91, 0.6], L2: [0.26, 0.6], R2: [0.74, 0.6],
  select: [0.42, 0.955], start: [0.58, 0.955],
  shoulderS: 0.16, smallS: 0.12,
};

// The DS/3DS picture IS a touchscreen, so in landscape everything is pushed into the side margins.
const LANDSCAPE_MARGINS: Partial<Anchors> = {
  primary: [0.11, 0.55, 0.26], secondary: [0.11, 0.85, 0.2],
  face: [0.89, 0.58, 0.28], faceWithR: [0.89, 0.58, 0.28],
  select: [0.8, 0.93], start: [0.91, 0.93],
  L: [0.07, 0.25], R: [0.93, 0.25],
};

/** Face-button offsets in cluster units (each in −1..1 of the cluster's half-size), and the button size as a fraction of the cluster. */
function faceOffsets(arr: SystemTouchSpec["face"], count: number): { off: [number, number][]; size: number } {
  switch (arr) {
    case "one": return { off: [[0, 0]], size: 0.6 };
    case "two": return { off: [[-0.42, 0.28], [0.42, -0.28]], size: 0.5 };
    case "three": return { off: [[-0.9, 0.3], [0, 0.05], [0.9, -0.2]], size: 0.42 };
    case "six": return {
      off: [[-0.72, 0.42], [0, 0.3], [0.72, 0.18], [-0.72, -0.32], [0, -0.44], [0.72, -0.56]].slice(0, count) as [number, number][],
      size: 0.38,
    };
    case "gc": return { off: [[0, 0.05], [-0.8, 0.55], [0.82, -0.05], [-0.05, -0.82]], size: 0.42 };
    // A cabinet's slightly arched rows: the middle button rides a touch higher, as under a real hand.
    case "row3": return { off: [[-0.9, 0.1], [0, -0.06], [0.9, 0.02]], size: 0.42 };
    case "rows6": return {
      off: [[-0.72, -0.36], [0, -0.48], [0.72, -0.42], [-0.72, 0.42], [0, 0.3], [0.72, 0.36]].slice(0, count) as [number, number][],
      size: 0.38,
    };
    case "rows8": return {
      off: [[-0.9, -0.33], [-0.3, -0.45], [0.3, -0.42], [0.9, -0.36], [-0.9, 0.39], [-0.3, 0.27], [0.3, 0.3], [0.9, 0.36]]
        .slice(0, count) as [number, number][],
      size: 0.27,
    };
    case "arc4": return { off: [[-0.96, 0.42], [-0.32, 0.1], [0.32, -0.12], [0.96, -0.26]], size: 0.33 };
    case "n64": return { off: [[0.3, 0.38], [-0.62, -0.3]], size: 0.46 };
    case "four":
    default: return { off: [[0, 0.62], [0.62, 0], [-0.62, 0], [0, -0.62]], size: 0.38 };
  }
}

let idSeq = 0;
/** A fresh control id. Stable enough for one layout; ids are only compared within a layout. */
export const newControlId = (prefix: string) => `${prefix}-${Date.now().toString(36)}-${(idSeq++).toString(36)}`;

const clamp01 = (n: number) => Math.max(0, Math.min(1, n));

/** Floating zones: [x, y, w, h] as layer fractions. The left zone stops short of the right-hand buttons. */
const ZONES: Record<Bucket, { left: [number, number, number, number]; right: [number, number, number, number]; leftWide: [number, number, number, number] }> = {
  landscape: { left: [0.2, 0.62, 0.4, 0.68], right: [0.8, 0.62, 0.4, 0.68], leftWide: [0.33, 0.62, 0.62, 0.68] },
  portrait: { left: [0.25, 0.8, 0.5, 0.36], right: [0.75, 0.8, 0.5, 0.36], leftWide: [0.34, 0.8, 0.64, 0.36] },
};

/** The game picture's box on the layer, as fractions (x0,y0 = top-left). */
export interface PictureRect { x0: number; y0: number; x1: number; y1: number }

/**
 * The minor buttons — shoulders, Select/Start/Coin, mode pills — aren't pinned: each settles at the spot
 * nearest its anchor that collides with nothing already placed (nor the room's ✕/☰ corner) and, when the
 * screen has any free space, stays OFF the picture. A shoulder may only drift a little (it's played
 * mid-game); a Select/Start may go much further (it's pressed a few times a game). Geometry in units of
 * the layer's height; `u` = min(w, h).
 */
interface SettleCtx { W: number; H: number; u: number; pic: PictureRect | null; placed: Control[] }
const SETTLE_GAP = 0.03;   // u — clear space kept between two controls
const SETTLE_STEP = 0.03;  // u — search grid
const CHROME = { w: 0.3, h: 0.15 }; // u — the room's ✕ and ☰ in the top-right corner (two 44 px buttons, 12 px in)

function settle(c: ButtonControl, ctx: SettleCtx, reach: number) {
  const { W, H, u, pic } = ctx;
  const r = (c.s * u) / 2;
  const hw = r, hh = c.shape === "pill" ? r * 0.55 : r;
  const hits = (x: number, y: number) => {
    if (x - hw < 0 || x + hw > W || y - hh < 0 || y + hh > H) return true;
    if (x + hw > W - CHROME.w * u && y - hh < CHROME.h * u) return true;
    for (const o of ctx.placed) {
      if (o.kind === "region") continue;
      if (Math.hypot(x - o.x * W, y - o.y * H) < r + (o.s * u) / 2 + SETTLE_GAP * u) return true;
    }
    return false;
  };
  const onPicture = (x: number, y: number) =>
    !!pic && x + hw > pic.x0 * W && x - hw < pic.x1 * W && y + hh > pic.y0 * H && y - hh < pic.y1 * H;
  const x0 = c.x * W, y0 = c.y * H;
  if (!hits(x0, y0) && !onPicture(x0, y0)) return;
  const step = SETTLE_STEP * u;
  const n = Math.ceil(reach / SETTLE_STEP);
  let best: [number, number] | null = hits(x0, y0) ? null : [x0, y0];
  let bestScore = best ? 1e6 : Infinity;
  for (let i = -n; i <= n; i++) {
    for (let j = -n; j <= n; j++) {
      const d = Math.hypot(i, j);
      if (d > n) continue;
      const x = x0 + i * step, y = y0 + j * step;
      const score = d * step + (onPicture(x, y) ? 1e6 : 0);
      if (score >= bestScore || hits(x, y)) continue;
      best = [x, y]; bestScore = score;
    }
  }
  if (best && (best[0] !== x0 || best[1] !== y0)) { c.x = clamp01(best[0] / W); c.y = clamp01(best[1] / H); }
}

/**
 * The built-in layout for an input system, generated for the layer's actual aspect (w/h). `specIn` = a
 * preset's spec (touchPresets.ts); absent = the system's standard spec. `picture` = where the game's
 * picture sits on the layer (absent = unknown, treated as nowhere in particular).
 *
 * No ☰ on the pad: the room's own ☰ (RoomOverlay, beside the fullscreen ✕) is the one menu, and a tap
 * on empty space brings it back when it has faded. A player can still add a pad ☰ in the editor.
 */
export function defaultLayout(inputSystem: string, bucket: Bucket, aspect: number, specIn?: SystemTouchSpec, picture?: PictureRect | null): Layout {
  const spec = specIn || touchSpecFor(inputSystem);
  const A: Anchors = bucket === "landscape" && spec.marginsOnly ? { ...LANDSCAPE, ...LANDSCAPE_MARGINS } : bucket === "landscape" ? LANDSCAPE : PORTRAIT;
  const a = aspect > 0 ? aspect : bucket === "landscape" ? 16 / 9 : 9 / 16;
  // One min-unit as a fraction of the layer's width / height.
  const ux = a >= 1 ? 1 / a : 1;
  const uy = a >= 1 ? 1 : a;
  const controls: Control[] = [];

  // Movement: the primary control gets the prime spot; when both exist the other sits below/inside. A
  // left ZONE replaces both — it IS the movement (a paddle, a trackball, twin-stick move, gun aim).
  const Z = ZONES[bucket];
  if (spec.leftZone) {
    // Nothing on the right but one or two buttons → the zone can take the wider share of the screen.
    const wide = !spec.rightZone && !spec.rightStick && spec.faceButtons.length <= 2;
    const [x, y, w, h] = wide ? Z.leftWide : Z.left;
    controls.push({ id: "lzone", kind: "region", label: spec.leftZone.label, output: spec.leftZone.output, deadzone: 0.08, x, y, w, h, s: 0.2, opacity: 0.35 });
  } else {
    const stickAt = spec.primary === "stick" || !spec.dpad ? A.primary : A.secondary;
    const dpadAt = spec.primary === "dpad" || !spec.leftStick ? A.primary : A.secondary;
    if (spec.dpad) controls.push({ id: "dpad", kind: "dpad", x: dpadAt[0], y: dpadAt[1], s: dpadAt[2], ...(spec.dpadWays === 4 ? { ways: 4 as const } : null) });
    if (spec.leftStick) controls.push({ id: "lstick", kind: "stick", x: stickAt[0], y: stickAt[1], s: stickAt[2], output: "left", floating: false, deadzone: 0.12 });
  }
  if (spec.rightZone) {
    const [x, y, w, h] = Z.right;
    controls.push({ id: "rzone", kind: "region", label: spec.rightZone.label, output: spec.rightZone.output, deadzone: 0.08, x, y, w, h, s: 0.2, opacity: 0.35 });
  }

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
  // A wide cluster (a row of three, two rows of four) can poke past the right edge on a narrow screen held
  // upright: slide the whole cluster inward until its outermost button is fully on screen.
  const btnR = (faceAt[2] * size * ux) / 2;
  const used = spec.faceButtons.map((_, i) => off[i] || [0, 0]);
  const maxX = used.length ? Math.max(...used.map((o) => faceAt[0] + o[0] * half * ux)) + btnR : 0;
  const shiftX = Math.min(0, 0.995 - maxX);
  spec.faceButtons.forEach((b, i) => {
    const o = off[i] || [0, 0];
    controls.push({
      id: `face-${b.bit}`, kind: "button", label: b.label, bits: [b.bit], shape: "circle", mode: "press",
      x: clamp01(faceAt[0] + shiftX + o[0] * half * ux), y: clamp01(faceAt[1] + o[1] * half * uy), s: faceAt[2] * size,
    });
  });

  // The minor buttons, settled one at a time around what's already placed.
  const ctx: SettleCtx = { W: a, H: 1, u: Math.min(a, 1), pic: picture || null, placed: controls };
  const minor = (c: ButtonControl, reach: number) => { settle(c, ctx, reach); controls.push(c); };
  for (const k of ["L", "R", "L2", "R2"] as const) {
    const label = spec.shoulders[k];
    if (!label) continue;
    const [x, y] = A[k];
    minor({ id: `sh-${k}`, kind: "button", label, bits: [k], shape: "pill", mode: "press", x, y, s: A.shoulderS }, 0.3);
  }
  // A lone Start (a Genesis) takes the middle of the pair's spot.
  const pairMid = (A.select[0] + A.start[0]) / 2;
  if (spec.select) minor({ id: "select", kind: "button", label: spec.select, bits: ["SELECT"], shape: "pill", mode: "press", x: spec.start ? A.select[0] : pairMid, y: A.select[1], s: A.smallS }, 1.2);
  if (spec.start) minor({ id: "start", kind: "button", label: spec.start, bits: ["START"], shape: "pill", mode: "press", x: spec.select ? A.start[0] : pairMid, y: A.start[1], s: A.smallS }, 1.2);
  // Pills sit beside Select/Start, centred on them: a pad-mode switch is pressed once, so it stays out of the
  // thumbs' way. Above them when they're in the bottom half (where they usually are); below otherwise.
  const sel = controls.find((c) => c.id === "select"), sta = controls.find((c) => c.id === "start");
  const rowX = sel && sta ? (sel.x + sta.x) / 2 : (sel ?? sta)?.x ?? pairMid;
  const rowY = sel?.y ?? sta?.y ?? A.select[1];
  (spec.pills || []).forEach((b, i, arr) => {
    const step = A.smallS * 1.3 * ux;
    minor({
      id: `pill-${b.bit}`, kind: "button", label: b.label, bits: [b.bit], shape: "pill", mode: "press",
      x: clamp01(rowX + (i - (arr.length - 1) / 2) * step), y: clamp01(rowY + (rowY > 0.5 ? -1 : 1) * A.smallS * 0.9 * uy), s: A.smallS,
    }, 1.2);
  });

  return { v: 1, controls, opacity: 0.7, idleFade: null, haptics: true, flashOnPress: true, slide: true };
}

/** Controls the editor can add for this system: every palette/face/shoulder bit not already on the layout, plus the generic kinds. */
export function addableButtons(inputSystem: string, layout: Layout, specIn?: SystemTouchSpec): FaceButton[] {
  const spec = specIn || touchSpecFor(inputSystem);
  const placed = new Set<string>();
  for (const c of layout.controls) if (c.kind === "button" && c.bits.length === 1) placed.add(c.bits[0]);
  const all: FaceButton[] = [...spec.faceButtons];
  for (const [bit, label] of Object.entries(spec.shoulders)) all.push({ bit: bit as BitName, label: label as string });
  if (spec.select) all.push({ bit: "SELECT", label: spec.select });
  if (spec.start) all.push({ bit: "START", label: spec.start });
  all.push(...(spec.pills || []), ...(spec.palette || []));
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
    case "dpad": return { ...base, kind: "dpad", ...(raw.ways === 4 ? { ways: 4 as const } : null) };
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
const PICK_KEY_RE = /^(sys:[a-z0-9]+|game:[a-z0-9]+\/.{1,200})$/;
const PICK_VALUE_RE = /^[a-z0-9-]{1,24}$/;
/** Arcade picks are per game, so they accumulate — keep the newest this many (insertion order = pick order). */
export const MAX_PICKS = 300;
/**
 * The server stores the blob verbatim but refuses one over 64 KB (APIController MaxSelfServiceSettingChars);
 * a refused save would leave "saved on this device only" forever. Leave headroom for the JSON envelope.
 */
export const MAX_STORE_CHARS = 60 * 1024;

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
  const picks: Record<string, string> = {};
  if (raw.picks && typeof raw.picks === "object") {
    const ok = Object.entries(raw.picks).filter(([k, v]) => PICK_KEY_RE.test(k) && typeof v === "string" && PICK_VALUE_RE.test(v));
    for (const [k, v] of ok.slice(-MAX_PICKS)) picks[k] = v as string;
  }
  return Object.keys(picks).length ? { v: 1, layouts, picks } : { v: 1, layouts };
}

/** Round geometry to 4 places on the way out — a stored blob of 0.1300000000000001s is just bigger. */
export function serializeStore(store: LayoutStore): string {
  return JSON.stringify(store, (k, v) => (typeof v === "number" && !Number.isInteger(v) ? Math.round(v * 10000) / 10000 : v));
}

export function withLayout(store: LayoutStore, key: string, layout: Layout | null): LayoutStore {
  const layouts = { ...store.layouts };
  if (layout) layouts[key] = layout; else delete layouts[key];
  return store.picks && Object.keys(store.picks).length ? { v: 1, layouts, picks: store.picks } : { v: 1, layouts };
}

/** Set (or with null, clear) one pick. A re-pick moves to the end, so MAX_PICKS trims the stalest. */
export function withPick(store: LayoutStore, key: string, presetId: string | null): LayoutStore {
  const picks = { ...(store.picks || {}) };
  delete picks[key];
  if (presetId) picks[key] = presetId;
  const kept = Object.entries(picks).slice(-MAX_PICKS);
  return kept.length ? { v: 1, layouts: store.layouts, picks: Object.fromEntries(kept) } : { v: 1, layouts: store.layouts };
}

/**
 * Serialize, dropping the OLDEST picks (never a layout — those are the player's work) until the blob fits
 * the server's cap. Returns the store actually written, so the caller keeps what it sent.
 */
export function fitStore(store: LayoutStore): { store: LayoutStore; json: string } {
  let cur = store;
  let json = serializeStore(cur);
  while (json.length > MAX_STORE_CHARS && cur.picks && Object.keys(cur.picks).length) {
    const entries = Object.entries(cur.picks);
    const kept = entries.slice(Math.max(1, Math.ceil(entries.length / 10)));
    cur = kept.length ? { v: 1, layouts: cur.layouts, picks: Object.fromEntries(kept) } : { v: 1, layouts: cur.layouts };
    json = serializeStore(cur);
  }
  return { store: cur, json };
}

/** True when the store holds nothing at all (the account copy can be cleared). */
export const isEmptyStore = (s: LayoutStore) => !Object.keys(s.layouts).length && !Object.keys(s.picks || {}).length;
