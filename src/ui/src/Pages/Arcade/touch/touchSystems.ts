/**
 * What an on-screen pad needs to know about each system: which RetroPad bits the console really has,
 * what the console calls them, and how its face buttons sit under the thumb. touchLayout.ts turns a
 * spec into a default layout; the editor offers every `palette` bit for adding.
 *
 * Labels are the CONSOLE's names for the RetroPad bit the core reads (the same knowledge
 * controllerMapDisplay.js's SYSTEM_BUTTON_LABELS carries, in thumb-sized form). Face positions are
 * positional, like the physical-pad profiles in cloudRetroClient.js: south/east/west/north =
 * B/A/Y/X, so a SNES diamond and a PlayStation diamond put the same bits in the same places.
 *
 * Mappings marked VERIFY are from the core's documented defaults, not a live check on our workers —
 * confirm in a room before relying on the label (the binding itself is editable either way).
 */
import { PAD } from "../cloudRetroClient";

export type BitName = keyof typeof PAD;

/** How the face buttons are arranged. Offsets are in cluster units (−1..1 of the cluster's half-size). */
export type FaceArrangement = "one" | "two" | "three" | "four" | "six" | "gc" | "n64";

export interface FaceButton { bit: BitName; label: string }

export interface SystemTouchSpec {
  /** Left side: which movement controls exist, and which one is primary (gets the prime spot). */
  dpad: boolean;
  leftStick: boolean;
  primary: "dpad" | "stick";
  /** Right stick: a real stick, the N64's four C-buttons (right-stick pushes), or none. */
  rightStick: false | "stick" | "cbuttons";
  face: FaceArrangement;
  faceButtons: FaceButton[];
  /** Shoulder buttons placed by default. */
  shoulders: Partial<Record<"L" | "R" | "L2" | "R2", string>>;
  select?: string;
  start?: string;
  /** Extra bits the editor offers in "+ Add" but the default layout leaves off. */
  palette?: FaceButton[];
  /** Keep everything in the screen margins — the picture itself is a touchscreen (DS/3DS). */
  marginsOnly?: boolean;
  /** v1: the touch pad isn't auto-shown here (mouse/keyboard systems) — it can still be turned on. */
  noAutoShow?: boolean;
}

const fb = (bit: BitName, label: string): FaceButton => ({ bit, label });

const TWO_NINTENDO: FaceButton[] = [fb("B", "B"), fb("A", "A")];
const FOUR_NINTENDO: FaceButton[] = [fb("B", "B"), fb("A", "A"), fb("Y", "Y"), fb("X", "X")];
const FOUR_PLAYSTATION: FaceButton[] = [fb("B", "✕"), fb("A", "○"), fb("Y", "□"), fb("X", "△")];
const FOUR_XBOX: FaceButton[] = [fb("B", "A"), fb("A", "B"), fb("Y", "X"), fb("X", "Y")];
// Genesis Plus GX: Y→A, B→B, A→C; L→X, X→Y, R→Z; Select→Mode.
const THREE_SEGA: FaceButton[] = [fb("Y", "A"), fb("B", "B"), fb("A", "C")];
const SEGA_SIX_TOP: FaceButton[] = [fb("L", "X"), fb("X", "Y"), fb("R", "Z")];
// FBNeo's generic order: B=1, A=2, Y=3, X=4, L=5, R=6.
const FOUR_ARCADE: FaceButton[] = [fb("B", "1"), fb("A", "2"), fb("Y", "3"), fb("X", "4")];
const ARCADE_56: FaceButton[] = [fb("L", "5"), fb("R", "6")];

const retro2 = (over: Partial<SystemTouchSpec> = {}): SystemTouchSpec => ({
  dpad: true, leftStick: false, primary: "dpad", rightStick: false,
  face: "two", faceButtons: TWO_NINTENDO, shoulders: {}, select: "Select", start: "Start", ...over,
});

const L3R3: FaceButton[] = [fb("L3", "L3"), fb("R3", "R3")];

const HEAVY: SystemTouchSpec = {
  dpad: true, leftStick: true, primary: "stick", rightStick: "stick",
  face: "four", faceButtons: FOUR_XBOX,
  shoulders: { L: "LB", R: "RB", L2: "LT", R2: "RT" }, select: "Back", start: "Start", palette: L3R3,
};

export const SYSTEM_TOUCH_SPECS: Record<string, SystemTouchSpec> = {
  nes: retro2(),
  fds: retro2(),
  gb: retro2(),
  gbc: retro2(),
  gba: retro2({ shoulders: { L: "L", R: "R" } }),
  supervision: retro2(),
  pokemini: retro2({ select: undefined, palette: [fb("R", "C"), fb("L", "Shake")] }),
  snes: retro2({ face: "four", faceButtons: FOUR_NINTENDO, shoulders: { L: "L", R: "R" } }),
  nds: retro2({ face: "four", faceButtons: FOUR_NINTENDO, shoulders: { L: "L", R: "R" }, marginsOnly: true }),
  "3ds": {
    dpad: true, leftStick: true, primary: "stick", rightStick: false,
    face: "four", faceButtons: FOUR_NINTENDO, shoulders: { L: "L", R: "R" }, select: "Select", start: "Start",
    palette: [fb("L2", "ZL"), fb("R2", "ZR")], marginsOnly: true,
  },
  vb: retro2({ shoulders: { L: "L", R: "R" } }),
  lynx: retro2({ select: undefined, start: "Pause", shoulders: { L: "Opt 1", R: "Opt 2" } }),
  // mednafen_ngp: B→A, A→B (VERIFY).
  ngpc: retro2({ faceButtons: [fb("B", "A"), fb("A", "B")], select: undefined, start: "Option" }),
  wsc: retro2({ select: undefined, palette: [fb("Y", "Y"), fb("X", "X"), fb("L", "L"), fb("R", "R")] }),
  // mednafen_pce: B = II (left), A = I (right).
  pce: retro2({ faceButtons: [fb("B", "II"), fb("A", "I")], start: "Run",
    palette: [fb("Y", "III"), fb("X", "IV"), fb("L", "V"), fb("R", "VI")] }),
  sms: retro2({ faceButtons: [fb("B", "1"), fb("A", "2")], select: undefined, start: "Pause" }),
  sg1000: retro2({ faceButtons: [fb("B", "1"), fb("A", "2")], select: undefined, start: "Pause" }),
  gg: retro2({ faceButtons: [fb("B", "1"), fb("A", "2")], select: undefined }),
  genesis: retro2({ face: "three", faceButtons: THREE_SEGA, select: undefined, palette: [...SEGA_SIX_TOP, fb("SELECT", "Mode")] }),
  segacd: retro2({ face: "three", faceButtons: THREE_SEGA, select: undefined, palette: [...SEGA_SIX_TOP, fb("SELECT", "Mode")] }),
  sega32x: retro2({ face: "three", faceButtons: THREE_SEGA, select: undefined, palette: [...SEGA_SIX_TOP, fb("SELECT", "Mode")] }),
  // Kronos: Saturn pad as a six-button Sega pad, L/R triggers on L2/R2 (VERIFY).
  saturn: retro2({ face: "six", faceButtons: [...THREE_SEGA, ...SEGA_SIX_TOP], shoulders: { L2: "L", R2: "R" }, select: undefined }),
  "3do": retro2({ face: "three", faceButtons: THREE_SEGA, shoulders: { L: "L", R: "R" }, select: "X", start: "P" }),
  a2600: retro2({ face: "one", faceButtons: [fb("B", "Fire")], start: "Reset" }),
  a7800: retro2({ faceButtons: [fb("B", "1"), fb("A", "2")], start: "Pause" }),
  coleco: retro2({ faceButtons: [fb("B", "L"), fb("A", "R")] }),
  intv: retro2(),
  vectrex: retro2({ face: "four", faceButtons: [fb("B", "1"), fb("A", "2"), fb("Y", "3"), fb("X", "4")], select: undefined }),
  o2em: retro2({ face: "one", faceButtons: [fb("B", "Action")] }),
  channelf: retro2({ face: "four", faceButtons: FOUR_NINTENDO }),
  arcadia: retro2(),
  cdi: retro2({ faceButtons: [fb("B", "1"), fb("A", "2")] }),
  arcade: retro2({ face: "four", faceButtons: FOUR_ARCADE, select: "Coin", palette: ARCADE_56 }),
  naomi: retro2({ face: "four", faceButtons: FOUR_ARCADE, select: "Coin", palette: ARCADE_56 }),
  atomiswave: retro2({ face: "four", faceButtons: FOUR_ARCADE, select: "Coin", palette: ARCADE_56 }),
  // FBNeo Neo Geo: B=A, A=B, Y=C, X=D.
  neogeo: retro2({ face: "four", faceButtons: [fb("B", "A"), fb("A", "B"), fb("Y", "C"), fb("X", "D")], select: "Coin" }),
  ps1: {
    dpad: true, leftStick: false, primary: "dpad", rightStick: false,
    face: "four", faceButtons: FOUR_PLAYSTATION,
    shoulders: { L: "L1", R: "R1", L2: "L2", R2: "R2" }, select: "Select", start: "Start",
    // Dual analog is per game on PS1 — offered, not placed.
    palette: L3R3,
  },
  ps2: {
    dpad: true, leftStick: true, primary: "dpad", rightStick: "stick",
    face: "four", faceButtons: FOUR_PLAYSTATION,
    shoulders: { L: "L1", R: "R1", L2: "L2", R2: "R2" }, select: "Select", start: "Start", palette: L3R3,
  },
  psp: {
    dpad: true, leftStick: true, primary: "dpad", rightStick: false,
    face: "four", faceButtons: FOUR_PLAYSTATION, shoulders: { L: "L", R: "R" }, select: "Select", start: "Start",
  },
  // mupen64plus/parallel: N64 A ← RetroPad B, N64 B ← RetroPad A, Z ← L2, C-buttons = right stick.
  n64: {
    dpad: false, leftStick: true, primary: "stick", rightStick: "cbuttons",
    face: "n64", faceButtons: [fb("B", "A"), fb("A", "B")],
    shoulders: { L: "L", R: "R", L2: "Z" }, start: "Start",
    palette: [fb("UP", "D-pad ↑"), fb("DOWN", "D-pad ↓"), fb("LEFT", "D-pad ←"), fb("RIGHT", "D-pad →")],
  },
  dc: {
    dpad: true, leftStick: true, primary: "stick", rightStick: false,
    face: "four", faceButtons: [fb("B", "A"), fb("A", "B"), fb("Y", "X"), fb("X", "Y")],
    shoulders: { L2: "LT", R2: "RT" }, start: "Start",
  },
  // Dolphin GC: A big in the middle, B low-left, X right, Y top; Z on R; analog L/R on L2/R2; C-stick right.
  gc: {
    dpad: true, leftStick: true, primary: "stick", rightStick: "stick",
    face: "gc", faceButtons: [fb("A", "A"), fb("B", "B"), fb("X", "X"), fb("Y", "Y")],
    shoulders: { L2: "L", R2: "R", R: "Z" }, start: "Start",
  },
  // Wiimote + Nunchuk. The right stick is the IR pointer — offered as a stick, not placed.
  wii: {
    dpad: true, leftStick: true, primary: "stick", rightStick: false,
    face: "four", faceButtons: [fb("B", "A"), fb("A", "B"), fb("X", "C"), fb("Y", "Z")],
    shoulders: { L: "−", R: "+", L2: "Shake" }, palette: [fb("SELECT", "Home")],
  },
  scummvm: retro2({ face: "four", faceButtons: FOUR_NINTENDO, noAutoShow: true }),
  dos: retro2({ face: "four", faceButtons: FOUR_NINTENDO, noAutoShow: true }),
  switch: { ...HEAVY, faceButtons: FOUR_NINTENDO, shoulders: { L: "L", R: "R", L2: "ZL", R2: "ZR" }, select: "−", start: "+" },
  wiiu: { ...HEAVY, faceButtons: FOUR_NINTENDO, shoulders: { L: "L", R: "R", L2: "ZL", R2: "ZR" }, select: "−", start: "+" },
  ps3: { ...HEAVY, faceButtons: FOUR_PLAYSTATION, shoulders: { L: "L1", R: "R1", L2: "L2", R2: "R2" }, select: "Select", start: "Start" },
  ps4: { ...HEAVY, faceButtons: FOUR_PLAYSTATION, shoulders: { L: "L1", R: "R1", L2: "L2", R2: "R2" }, select: "Share", start: "Options" },
  x360: HEAVY,
  pc: HEAVY,
  capture: HEAVY,
};

const FALLBACK_SPEC: SystemTouchSpec = retro2({ face: "four", faceButtons: FOUR_NINTENDO, shoulders: { L: "L", R: "R" } });

/** The spec for an INPUT system (effectiveInputSystem — Wii in GC scheme is "gc"). Unknown → a SNES-shaped pad. */
export function touchSpecFor(system: string | null | undefined): SystemTouchSpec {
  return SYSTEM_TOUCH_SPECS[String(system || "").toLowerCase()] || FALLBACK_SPEC;
}

/** Every bit + console label this system offers (face, shoulders, select/start, palette, d-pad), deduped by bit. */
export function bitChoicesFor(system: string | null | undefined): FaceButton[] {
  const s = touchSpecFor(system);
  const out: FaceButton[] = [];
  const seen = new Set<string>();
  const add = (b: FaceButton) => { if (!seen.has(b.bit)) { seen.add(b.bit); out.push(b); } };
  s.faceButtons.forEach(add);
  for (const [bit, label] of Object.entries(s.shoulders)) add(fb(bit as BitName, label as string));
  if (s.select) add(fb("SELECT", s.select));
  if (s.start) add(fb("START", s.start));
  (s.palette || []).forEach(add);
  for (const [bit, label] of [["UP", "↑"], ["DOWN", "↓"], ["LEFT", "←"], ["RIGHT", "→"]] as const) add(fb(bit, label));
  // Anything the system leaves unnamed still exists on the RetroPad — offer it under its RetroPad name, last.
  for (const bit of Object.keys(PAD) as BitName[]) add(fb(bit, bit === "SELECT" ? "Select" : bit === "START" ? "Start" : bit));
  return out;
}
