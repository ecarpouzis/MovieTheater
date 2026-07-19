// Per-system button-mapping visualizer data. Turns cloudRetroClient.js's PROFILES (already the
// source of truth for physical->RetroPad remapping) into plain rows a UI can render: "your South
// face button sends RetroPad B, which this system's core reads as its Cross/Jump/whatever button."
//
// No SVG/controller artwork here on purpose — matches the existing plain-antd aesthetic of the
// Controllers panel and avoids a per-brand icon-art maintenance burden nobody asked for.
import { PAD, profileFor } from "./cloudRetroClient";
import { effectiveFaceSwap } from "./controllerIdentity";

// RetroPad bit name -> physical Gamepad-API button index in the UNSWAPPED (Nintendo/PlayStation)
// layout, i.e. DEFAULT_GAMEPAD's own shape read in reverse. Only positions 0-3 (the face buttons)
// are ever affected by faceSwap; everything else is physically fixed.
const PHYSICAL_LAYOUT = [
  { pos: 0, physicalLabel: "South face button" },
  { pos: 1, physicalLabel: "East face button" },
  { pos: 2, physicalLabel: "West face button" },
  { pos: 3, physicalLabel: "North face button" },
  { pos: 4, physicalLabel: "Left shoulder" },
  { pos: 5, physicalLabel: "Right shoulder" },
  { pos: 6, physicalLabel: "Left trigger" },
  { pos: 7, physicalLabel: "Right trigger" },
  { pos: 8, physicalLabel: "Select / Back" },
  { pos: 9, physicalLabel: "Start" },
  { pos: 10, physicalLabel: "Left stick click" },
  { pos: 11, physicalLabel: "Right stick click" },
  { pos: 12, physicalLabel: "D-pad Up" },
  { pos: 13, physicalLabel: "D-pad Down" },
  { pos: 14, physicalLabel: "D-pad Left" },
  { pos: 15, physicalLabel: "D-pad Right" },
];

// RetroPad bit index -> name, built once from PAD ({B:0, Y:1, ...}) on first use. Lazy (rather
// than computed at module top-level) because cloudRetroClient.js re-exports mappingRowsFor from
// this file, which makes this a circular import — touching PAD before cloudRetroClient.js has
// finished evaluating its own top-level body would hit it mid-initialization (TDZ). Deferring to
// first CALL (well after the whole module graph has loaded) sidesteps that entirely.
let bitNamesCache = null;
function bitNames() {
  if (!bitNamesCache) bitNamesCache = Object.fromEntries(Object.entries(PAD).map(([name, bit]) => [bit, name]));
  return bitNamesCache;
}

// Friendly fallback for a RetroPad bit when a system has no curated native name for it.
const GENERIC_BIT_LABEL = {
  B: "B", Y: "Y", SELECT: "Select", START: "Start",
  UP: "Up", DOWN: "Down", LEFT: "Left", RIGHT: "Right",
  A: "A", X: "X", L: "L", R: "R", L2: "L2", R2: "R2", L3: "L3", R3: "R3",
};

// Per-system RetroPad-bit -> console-native-name overrides, sourced directly from the comments
// already documented next to each PROFILES entry in cloudRetroClient.js (not new claims — this
// just makes that existing knowledge machine-readable). Systems not listed here use the
// DEFAULT profile unchanged, so GENERIC_BIT_LABEL is already accurate for them.
export const SYSTEM_BUTTON_LABELS = {
  ps1: {
    B: "Cross", A: "Circle", Y: "Square", X: "Triangle",
    L: "L1", R: "R1", L2: "L2", R2: "R2",
  },
  psp: {
    B: "Cross", A: "Circle", Y: "Square", X: "Triangle",
  },
  n64: {
    B: "A (accelerate / confirm)", A: "B", L2: "Z trigger",
  },
  gc: {
    A: "A (confirm)", B: "B", R: "Z trigger",
    L2: "L (analog trigger)", R2: "R (analog trigger)",
  },
  dc: {
    B: "A", A: "B", Y: "X", X: "Y",
    L2: "Left analog trigger", R2: "Right analog trigger",
  },
  wii: {
    B: "Wiimote A", A: "Wiimote B", X: "Nunchuk C", Y: "Nunchuk Z",
    L: "Wiimote −", R: "Wiimote +", L2: "Swing / shake (hold)",
  },
};

/**
 * Labeled mapping rows for `system`, reflecting `gp`'s effective face-button convention (auto-
 * detected family, or the manual override) so the displayed physical->console mapping matches
 * what will actually happen when that pad is used. `gp` may be null/undefined (no pad connected
 * yet) — face buttons then show the unswapped (Nintendo/PlayStation) default.
 * `customProfile` optionally overrides button mappings (maps button index -> RetroPad bit).
 */
export function mappingRowsFor(system, gp, customProfile = {}) {
  const profile = profileFor(system);
  const labels = SYSTEM_BUTTON_LABELS[(system || "").toLowerCase()] || {};
  const swap = gp ? effectiveFaceSwap(gp) : false;
  return PHYSICAL_LAYOUT.map(({ pos, physicalLabel }) => {
    const effectivePos = swap && pos < 4 ? pos ^ 1 : pos;
    let bit = profile.gamepad[effectivePos];
    // Apply custom gamepad profile if it remaps this button
    if (customProfile && customProfile[effectivePos] !== undefined) {
      bit = customProfile[effectivePos];
    }
    const bitName = bit !== undefined ? bitNames()[bit] : undefined;
    const consoleLabel = bitName ? (labels[bitName] || GENERIC_BIT_LABEL[bitName] || bitName) : "—";
    return { physicalLabel, bitName, consoleLabel, physicalButtonIndex: effectivePos };
  });
}
