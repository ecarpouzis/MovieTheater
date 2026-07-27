// Controller identity: classify a physical Gamepad into a "family" (DualSense/DualShock4/Xbox/
// Switch Pro/generic) from navigator.getGamepads()[i].id, and use that to auto-pick the
// face-button label convention instead of the old single machine-wide manual toggle.
//
// Chrome/Firefox already normalize most pads to the W3C Standard Gamepad mapping, so physical
// button POSITIONS (0 south, 1 east, 2 west, 3 north, ...) already line up across brands — the
// remaining brand difference is purely which face button the manufacturer LABELS as "primary"
// (Nintendo/PlayStation put confirm on the position DEFAULT_GAMEPAD already targets; Xbox mirrors
// it). See cloudRetroClient.js's PROFILES comments for why the unswapped default is already
// Nintendo/PlayStation-correct.
//
// gp.id examples (verified shape on Chrome/Windows):
//   "DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)"
//   "Xbox Wireless Controller (STANDARD GAMEPAD Vendor: 045e Product: 0b13)"
//   "Pro Controller (STANDARD GAMEPAD Vendor: 057e Product: 2009)"
// Firefox and some platforms omit the Vendor/Product parenthetical entirely, hence the
// name-substring fallback on every entry below.

const CONTROLLER_FAMILIES = [
  {
    key: "dualsense",
    label: "DualSense",
    swapFaceButtons: false,
    match: (id) => /vendor:\s*054c\s*product:\s*0ce6/i.test(id) || /dualsense/i.test(id),
  },
  {
    key: "dualshock4",
    label: "DualShock 4",
    swapFaceButtons: false,
    match: (id) => /vendor:\s*054c\s*product:\s*(05c4|09cc)/i.test(id) || /dualshock/i.test(id),
  },
  {
    // Any other Sony-vendor pad (054c) that isn't specifically matched above — still a
    // PlayStation-layout pad, so the same no-swap convention applies.
    key: "playstation",
    label: "PlayStation controller",
    swapFaceButtons: false,
    match: (id) => /vendor:\s*054c/i.test(id) || /playstation|dualshock|dualsense/i.test(id),
  },
  {
    key: "switchpro",
    label: "Switch Pro Controller",
    swapFaceButtons: false,
    match: (id) => /vendor:\s*057e/i.test(id) || /switch pro|joy-con|nintendo/i.test(id),
  },
  {
    key: "xbox",
    label: "Xbox controller",
    swapFaceButtons: true,
    match: (id) => /vendor:\s*045e/i.test(id) || /xbox|xinput/i.test(id),
  },
  {
    key: "generic",
    label: "Controller",
    swapFaceButtons: false,
    match: () => true, // always matches last — the fallback
  },
];

// gp.id is stable for a given physical pad across polls, so cache the classification instead of
// re-running every regex on every ~60Hz tick (readGamepad()/pumpInput() call this every poll).
const familyCache = new Map();

/** Classify a Gamepad (or a {id} shaped stand-in, e.g. the Controllers panel's pad rows). */
export function controllerFamilyFor(gp) {
  const id = (gp && gp.id) || "";
  let family = familyCache.get(id);
  if (!family) {
    family = CONTROLLER_FAMILIES.find((f) => f.match(id));
    familyCache.set(id, family);
  }
  return family;
}

/** Display label for a connected pad's DETECTED FAMILY, for the Controllers panel and the mapping
 * visualizer (e.g. "DualSense", "Xbox controller", "Controller" for an unrecognized generic pad). */
export function controllerLabelFor(gp) {
  const family = controllerFamilyFor(gp);
  if (family.key !== "generic") return family.label;
  return gp && Number.isInteger(gp.index) ? `Controller ${gp.index + 1}` : "Controller";
}

// ── Face-button convention: auto (per detected family) with a manual per-machine override ───────
// Replaces the old boolean `arcade.faceSwap` ("0"/"1") with a tri-state so "auto-detect" can be
// the default without discarding anyone's prior explicit choice. Migrated once, on first read:
// unset (null) -> "auto" (the new smart default); "1" -> "xbox"; "0" -> "nintendo". The old key
// is left in place (harmless) but never read again after migration.
const MODE_KEY = "arcade.faceSwapMode";
const LEGACY_KEY = "arcade.faceSwap";
const VALID_MODES = new Set(["auto", "xbox", "nintendo"]);

function migrateLegacyMode() {
  try {
    const legacy = localStorage.getItem(LEGACY_KEY);
    const migrated = legacy === "1" ? "xbox" : legacy === "0" ? "nintendo" : "auto";
    localStorage.setItem(MODE_KEY, migrated);
    return migrated;
  } catch {
    return "auto";
  }
}

export function getFaceSwapMode() {
  try {
    const stored = localStorage.getItem(MODE_KEY);
    if (stored && VALID_MODES.has(stored)) return stored;
    return migrateLegacyMode();
  } catch {
    return "auto";
  }
}

export function setFaceSwapMode(mode) {
  const safe = VALID_MODES.has(mode) ? mode : "auto";
  try { localStorage.setItem(MODE_KEY, safe); } catch { /* storage disabled */ }
}

// ── Per-CONTROLLER override (the Controllers panel's per-player checkbox) ────────────────────────
// The mode above is machine-wide, which is the wrong grain the moment two people play on one
// machine with different pads: local multiplayer runs several sessions in ONE browser, so a single
// flag would force both players onto the same convention. This map is the per-pad answer, and it
// beats the machine-wide mode.
//
// Keyed by gp.id (the model string), NOT gp.index. Indices are not stable — a Bluetooth pad that
// idle-sleeps comes back at a different index (the same re-enumeration readGamepad's adoption
// heuristic exists to survive), so an index-keyed choice would silently detach from the controller
// the player set it on, mid-session. Two identical pads share one entry, which is the right answer
// anyway: same model, same printed labels. Shape: { "<pad id>": true|false }.
const PAD_OVERRIDE_KEY = "arcade.padFaceSwap";

let padOverrides = (() => {
  try {
    const stored = localStorage.getItem(PAD_OVERRIDE_KEY);
    return stored ? JSON.parse(stored) : {};
  } catch {
    return {};
  }
})();

// Same reason familyCache exists: effectiveFaceSwap runs per pad on every ~60Hz input poll, so the
// id→key normalization is memoized rather than re-done 60 times a second.
const padKeyCache = new Map();

function padOverrideKeyFor(gp) {
  const raw = (gp && gp.id) || "";
  if (!raw) return gp && Number.isInteger(gp.index) ? `index:${gp.index}` : "";
  let key = padKeyCache.get(raw);
  if (key === undefined) {
    key = raw.trim().toLowerCase();
    padKeyCache.set(raw, key);
  }
  return key;
}

/** This pad's hand-set convention: true (Xbox), false (Nintendo/PlayStation), or undefined (auto). */
export function getPadFaceSwapOverride(gp) {
  const key = padOverrideKeyFor(gp);
  return key ? padOverrides[key] : undefined;
}

/** Set (true/false) or clear (null/undefined) THIS pad's convention. Takes effect on the next input
 *  poll — every session on this machine re-reads it per frame, so no rejoin, no session plumbing. */
export function setPadFaceSwapOverride(gp, on) {
  const key = padOverrideKeyFor(gp);
  if (!key) return;
  if (on === null || on === undefined) delete padOverrides[key];
  else padOverrides[key] = !!on;
  try { localStorage.setItem(PAD_OVERRIDE_KEY, JSON.stringify(padOverrides)); } catch { /* storage disabled */ }
}

/** Whether THIS pad's face buttons (south/east/west/north) should be relabeled Xbox-style.
 *  Precedence: the pad's own hand-set choice > the machine-wide mode > its detected family. */
export function effectiveFaceSwap(gp) {
  const override = getPadFaceSwapOverride(gp);
  if (override !== undefined) return !!override;
  const mode = getFaceSwapMode();
  if (mode === "auto") return controllerFamilyFor(gp).swapFaceButtons;
  return mode === "xbox";
}
