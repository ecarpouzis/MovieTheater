/**
 * Built-in touch-pad PRESETS: the alternative default layouts a player can switch between from the room's
 * ☰ menu — "3 buttons" vs "6 buttons" on a Genesis, a 4-way stick for Pac-Man, a trackball zone for
 * Centipede, two rows of punches and kicks for Street Fighter. A preset is just a SystemTouchSpec;
 * touchLayout.ts's defaultLayout turns one into a layout for the screen's shape, exactly like the
 * system's standard spec.
 *
 * Preset ids are STABLE (they're stored in the player's picks): rename freely, never re-id.
 *
 * Arcade presets are generated per GAME from its control profile (ArcadeGame.Controls, written by the
 * `arcade-controls` CLI from MAME's -listxml: control type, 4- vs 8-way, button count, and whether FBNeo
 * gives it the Street Fighter layout), and per CORE: the same "button 5" is a different RetroPad bit on
 * FBNeo, flycast and libretro MAME. Each button map below is read from that core's source.
 */
import {
  SEGA_SIX_TOP, THREE_SEGA, fb, retro2, touchSpecFor,
  type BitName, type FaceArrangement, type FaceButton, type SystemTouchSpec,
} from "./touchSystems";

export interface TouchPreset {
  id: string;
  /** Short, shown on the picker chip. */
  name: string;
  /** One line under the picker when this preset is selected (what it's for, any one-time step). */
  note?: string;
  spec: SystemTouchSpec;
}

// ── Arcade control profiles ─────────────────────────────────────────────────────────────────────────

/**
 * What a cabinet's player-1 panel IS, in the shape the touch pad cares about. joy4 covers MAME's 4-way,
 * 2-way and vertical-2 sticks (one direction at a time); stick = an analog/49-way stick; twin = two
 * 8-way sticks (Robotron); spinner = a dial, paddle or steering wheel; other = mahjong/keyboard/gambling
 * panels the touch pad can't model.
 */
export type ControlKind = "joy4" | "joy8" | "stick" | "twin" | "trackball" | "spinner" | "gun" | "buttons" | "other";
export interface ControlProfile { kind: ControlKind; buttons: number; sf: boolean }

const PROFILE_RE = /^(joy4|joy8|stick|twin|trackball|spinner|gun|buttons|other)\/(\d{1,2})(\/sf)?$/;

/** Parse ArcadeGame.Controls ("joy8/6/sf", "joy4/1", "trackball/1", …). Anything else = no profile. */
export function parseControlProfile(raw: string | null | undefined): ControlProfile | null {
  const m = PROFILE_RE.exec(String(raw || "").trim().toLowerCase());
  if (!m) return null;
  return { kind: m[1] as ControlKind, buttons: Math.min(8, Number(m[2])), sf: !!m[3] };
}

/** RetroPad bit for arcade button 1, 2, 3 … (index 0 = button 1). */
type ButtonMap = BitName[];

// FBNeo, classic RetroPad (a RETRO_DEVICE_JOYPAD port becomes RETROPAD_CLASSIC): FIRE01..08 =
// B, A, Y, X, R, L, R2, L2 (libretro/FBNeo src/burner/libretro/retro_input.cpp, RETRO_DEVICE_ID_FIREnn).
const FBNEO: ButtonMap = ["B", "A", "Y", "X", "R", "L", "R2", "L2"];
// FBNeo's Street Fighter layout (bStreetFighterLayout: CPS2 with 5+ buttons, or any driver with 3x-punch
// and 3x-kick macros): punches on the top row Y X L, kicks on the bottom row B A R.
const FBNEO_SF: ButtonMap = ["Y", "X", "L", "B", "A", "R"];
// flycast NAOMI / Atomiswave: "Button 1".."Button 8" = B, A, Y, X, R, L, R2, L2 (shell/libretro/libretro.cpp).
const FLYCAST: ButtonMap = ["B", "A", "Y", "X", "R", "L", "R2", "L2"];
// libretro MAME (the 0.288 set's core, not yet wired): button_mapping[] = B, A, Y, X, L, R by default
// (src/osd/modules/input/input_retro.cpp Input_Binding) — note 5/6 are the reverse of FBNeo's. That
// function also re-orders buttons for a few dozen named games; those aren't modelled here (the editor
// fixes one game; a real fix is a per-game map from the same source when the core lands).
const MAME_LIBRETRO: ButtonMap = ["B", "A", "Y", "X", "L", "R"];

/**
 * Render-profile core keys that mean libretro MAME. Empty until the 0.288 core is wired: give its
 * ArcadeGameProfile CoreKey here and every arcade preset follows its button order.
 */
export const MAME_LIBRETRO_CORE_KEYS = new Set<string>(["mame_libretro"]);

export function arcadeButtonMap(system: string, coreKey?: string | null): { map: ButtonMap; core: "fbneo" | "flycast" | "mame" } {
  const sys = String(system || "").toLowerCase();
  if (coreKey && MAME_LIBRETRO_CORE_KEYS.has(coreKey)) return { map: MAME_LIBRETRO, core: "mame" };
  if (sys === "naomi" || sys === "atomiswave") return { map: FLYCAST, core: "flycast" };
  return { map: FBNEO, core: "fbneo" };
}

/** Systems whose touch presets come from the arcade generator. */
const ARCADE_SYSTEMS = new Set(["arcade", "naomi", "atomiswave"]);
export const isArcadeTouchSystem = (inputSystem: string) => ARCADE_SYSTEMS.has(String(inputSystem || "").toLowerCase());

const faceForCount = (n: number): FaceArrangement =>
  n <= 1 ? "one" : n === 2 ? "two" : n === 3 ? "row3" : n === 4 ? "four" : n <= 6 ? "rows6" : "rows8";

function numbered(map: ButtonMap, from: number, to: number, labels?: string[]): FaceButton[] {
  const out: FaceButton[] = [];
  for (let i = from; i < Math.min(to, map.length); i++) out.push(fb(map[i], labels?.[i] ?? String(i + 1)));
  return out;
}

/** An arcade panel: `n` numbered buttons on the right, the rest of the core's buttons in the editor's palette. */
function arcadeSpec(map: ButtonMap, n: number, over: Partial<SystemTouchSpec> = {}, labels?: string[]): SystemTouchSpec {
  const count = Math.max(0, Math.min(n, map.length));
  return retro2({
    face: faceForCount(count),
    faceButtons: numbered(map, 0, count, labels),
    select: "Coin",
    start: "Start",
    palette: numbered(map, count, map.length, labels),
    ...over,
  });
}

function arcadePresets(inputSystem: string, profile: ControlProfile | null, coreKey?: string | null): TouchPreset[] {
  const { map, core } = arcadeButtonMap(inputSystem, coreKey);
  const n = profile && profile.buttons > 0 ? Math.min(profile.buttons, map.length) : 4;
  const counts = [...new Set([1, 2, 3, 4, 6, n])].filter((c) => c <= map.length).sort((a, b) => a - b);
  const out: TouchPreset[] = counts.map((c) => ({
    id: `b${c}`,
    name: c === 1 ? "1 button" : `${c} buttons`,
    spec: arcadeSpec(map, c),
  }));
  // Street Fighter rows: only where the core really lays the game out that way (FBNeo's SF layout). On an
  // unknown game it's still offered — the six bits are the same set, only the labels/positions move.
  if (core === "fbneo" && (!profile || profile.sf)) {
    out.push({
      id: "sf",
      name: "Street Fighter",
      note: "Punches on top (LP MP HP), kicks below (LK MK HK).",
      spec: arcadeSpec(FBNEO_SF, 6, { face: "rows6", palette: [] }, ["LP", "MP", "HP", "LK", "MK", "HK"]),
    });
  }
  const m = Math.max(1, n);
  out.push(
    {
      id: "joy4",
      name: "4-way stick",
      note: "One direction at a time, like Pac-Man's joystick — a sloppy diagonal can't stall a turn.",
      spec: arcadeSpec(map, m, { dpadWays: 4 }),
    },
    {
      id: "twin",
      name: "Twin stick",
      note: "Move on the left, fire in a direction on the right (Robotron, Smash TV).",
      spec: arcadeSpec(map, 0, { dpad: false, leftZone: { output: "dpad", label: "Move" }, rightZone: { output: "right", label: "Fire" } }),
    },
    {
      id: "spinner",
      name: "Spinner / wheel",
      note: "Drag sideways to turn the dial, paddle or wheel — further is faster.",
      spec: arcadeSpec(map, m, { dpad: false, leftZone: { output: "left", label: "Spin ↔" } }),
    },
    {
      id: "trackball",
      name: "Trackball",
      note: "Drag to roll the ball — further is faster.",
      spec: arcadeSpec(map, m, { dpad: false, leftZone: { output: "left", label: "Trackball" } }),
    },
    {
      id: "gun",
      name: "Light gun",
      note: "Aim the crosshair with the stick zone; there's no tap-to-shoot yet.",
      spec: arcadeSpec(map, m, { dpad: false, leftZone: { output: "left", label: "Aim" } }, ["Fire"]),
    },
  );
  return out;
}

function arcadeSuggestion(profile: ControlProfile | null, inputSystem: string, coreKey?: string | null): string {
  if (!profile) return "b4";
  const { map, core } = arcadeButtonMap(inputSystem, coreKey);
  const n = Math.max(1, Math.min(profile.buttons || 1, map.length));
  switch (profile.kind) {
    case "joy4": return "joy4";
    case "twin": return "twin";
    case "trackball": return "trackball";
    case "spinner": return "spinner";
    case "gun": return "gun";
    case "other": return "b4";
    default: return profile.sf && core === "fbneo" ? "sf" : `b${n}`;
  }
}

// ── Console presets ─────────────────────────────────────────────────────────────────────────────────

const standard = (name: string, spec: SystemTouchSpec, note?: string): TouchPreset => ({ id: "standard", name, spec, ...(note ? { note } : null) });

/** A floating stick under the left thumb instead of a fixed one (the d-pad stays in "+ Add"). */
const floatingStick = (spec: SystemTouchSpec): TouchPreset => ({
  id: "float",
  name: "Floating stick",
  note: "Touch anywhere on the left to grab the stick right under your thumb.",
  spec: { ...spec, leftZone: { output: "left", label: "Stick" } },
});

/** Swap which movement control gets the prime spot. */
const analogFirst = (spec: SystemTouchSpec, name = "Analog first"): TouchPreset => ({
  id: "analog", name, note: "The analog stick takes the main spot; the d-pad moves below it.", spec: { ...spec, primary: "stick" },
});

// Six face buttons for a fighter: rows6 puts the first three on the top row.
const sixRows = (top: FaceButton[], bottom: FaceButton[], over: Partial<SystemTouchSpec> = {}): Partial<SystemTouchSpec> =>
  ({ face: "rows6", faceButtons: [...top, ...bottom], shoulders: {}, ...over });

function consolePresets(sys: string): TouchPreset[] | null {
  const std = touchSpecFor(sys);
  switch (sys) {
    case "genesis":
    case "segacd":
    case "sega32x":
      return [
        standard("3 buttons", std),
        {
          id: "six",
          name: "6 buttons",
          note: "X Y Z over A B C, for Street Fighter II, Mortal Kombat and other six-button games.",
          // Bottom row A B C, top row X Y Z (Sega order — the "six" arrangement's first three are the bottom row).
          spec: { ...std, face: "six", faceButtons: [...THREE_SEGA, ...SEGA_SIX_TOP], palette: [fb("SELECT", "Mode")] },
        },
      ];
    case "pce":
      return [
        standard("2 buttons", std),
        {
          id: "six",
          name: "6 buttons",
          note: "Tap “2⇄6” once to switch the pad into 6-button mode (Street Fighter II′ CE, Advanced V.G.).",
          // mednafen_pce: III–VI = Y X L R; L2 toggles the Avenue Pad 6 mode in the core (libretro.cpp avpad6).
          spec: {
            ...std,
            // The Avenue Pad 6's own rows: IV V VI over III II I.
            ...sixRows([fb("X", "IV"), fb("L", "V"), fb("R", "VI")], [fb("Y", "III"), fb("B", "II"), fb("A", "I")]),
            palette: [],
            pills: [fb("L2", "2⇄6")],
          },
        },
      ];
    case "snes":
      return [
        standard("Standard", std),
        {
          id: "fighter",
          name: "6-button fighter",
          note: "Y X L over B A R — the Street Fighter II layout on the face, shoulders included.",
          spec: { ...std, ...sixRows([fb("Y", "Y"), fb("X", "X"), fb("L", "L")], [fb("B", "B"), fb("A", "A"), fb("R", "R")]) },
        },
      ];
    case "ps1":
      return [
        standard("Standard", std),
        {
          id: "fighter",
          name: "6-button fighter",
          note: "□ △ R1 over ✕ ○ R2 — Street Fighter Alpha, Darkstalkers, Marvel.",
          spec: {
            ...std,
            ...sixRows([fb("Y", "□"), fb("X", "△"), fb("R", "R1")], [fb("B", "✕"), fb("A", "○"), fb("R2", "R2")]),
            shoulders: { L: "L1", L2: "L2" },
          },
        },
      ];
    case "dc":
      return [
        standard("Standard", std),
        {
          id: "fighter",
          name: "6-button fighter",
          note: "X Y LT over A B RT — Marvel vs. Capcom 2, Street Fighter III, Capcom vs. SNK.",
          spec: { ...std, ...sixRows([fb("Y", "X"), fb("X", "Y"), fb("L2", "LT")], [fb("B", "A"), fb("A", "B"), fb("R2", "RT")]) },
        },
        floatingStick(std),
      ];
    case "neogeo":
      return [
        standard("Diamond", std),
        {
          id: "arc",
          name: "Neo Geo arc",
          note: "A B C D in the rising arc of the Neo Geo pad.",
          spec: { ...std, face: "arc4" },
        },
      ];
    case "a2600":
      return [
        standard("Joystick", std),
        {
          id: "paddle",
          name: "Paddle",
          note: "Drag sideways to turn the paddle — Breakout, Kaboom!, Warlords.",
          spec: { ...std, dpad: false, leftZone: { output: "left", label: "Paddle ↔" } },
        },
      ];
    case "ps2":
      return [standard("D-pad first", std), analogFirst(std)];
    case "psp":
      return [standard("D-pad first", std), analogFirst(std, "Analog nub first")];
    case "n64":
    case "gc":
    case "wii":
    case "3ds":
    case "switch":
    case "wiiu":
    case "ps3":
    case "ps4":
    case "x360":
    case "pc":
    case "capture":
      return [standard("Standard", std), floatingStick(std)];
    default:
      return null;
  }
}

// ── The API ─────────────────────────────────────────────────────────────────────────────────────────

export interface PresetContext {
  /** ArcadeGame.Controls for this room's game (arcade systems only). */
  controls?: string | null;
  /** The render-profile core key the room booted (descriptor.coreKey) — picks the arcade button map. */
  coreKey?: string | null;
}

/** Every preset this room can switch between. Never empty; the first is the system's standard layout. */
export function presetsFor(inputSystem: string, ctx: PresetContext = {}): TouchPreset[] {
  const sys = String(inputSystem || "").toLowerCase();
  if (ARCADE_SYSTEMS.has(sys)) return arcadePresets(sys, parseControlProfile(ctx.controls), ctx.coreKey);
  return consolePresets(sys) || [standard("Standard", touchSpecFor(sys))];
}

/** The preset a room starts on when the player hasn't picked or edited one. */
export function suggestedPreset(inputSystem: string, ctx: PresetContext = {}): string {
  const sys = String(inputSystem || "").toLowerCase();
  if (ARCADE_SYSTEMS.has(sys)) return arcadeSuggestion(parseControlProfile(ctx.controls), sys, ctx.coreKey);
  return "standard";
}

/** Arcade picks default to THIS game (every cabinet differs); console picks default to the whole system. */
export const defaultPickScope = (inputSystem: string): "game" | "system" => (isArcadeTouchSystem(inputSystem) ? "game" : "system");

