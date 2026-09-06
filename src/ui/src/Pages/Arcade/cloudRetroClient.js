// Vendored/owned CloudRetro client shim (docs/arcade-plan.md §7 + Appendix A).
//
// We do NOT iframe CloudRetro's stock web client: it derives its WS URL from window.location and
// force-overwrites the path to /ws, so it can't be pointed at our tokened gateway URL without a patch
// anyway. Instead this implements the small protocol ourselves against the gateway-provided descriptor.
//
// Flow (Appendix A3): open WS → INIT (t=4, gives ICE servers) → WebRTC setup (t=100/101) →
// GAME_START (t=104) → seat (t=108) → inputs over the pre-negotiated DataChannel. Media (VP8/Opus)
// arrives as WebRTC tracks attached to the <video>. The signaling WS goes quiet after setup.
//
// The retropad wire format below is CONFIRMED against CloudRetro master source (2026-07-02:
// web/js/input/retropad.js + web/js/input/keys.js): five int16s — [buttons, lx, ly, rx, ry] —
// sent on change only, no player-index byte. See encodeInput() for the details.

import { effectiveFaceSwap } from "./controllerIdentity";
import { createChordWatcher, resolveChords } from "./controllerChords";
import { createInputTape, createTapePlayer, THUMB_W, THUMB_H } from "./inputTape";

// Packet types (Appendix A2).
const T = {
  LATENCY: 3,
  INIT: 4,
  INIT_WEBRTC: 100,
  SIGNAL: 101,
  GAME_START: 104,
  GAME_QUIT: 105,
  GAME_SAVE: 106,
  GAME_LOAD: 107,
  SET_PLAYER_INDEX: 108,
  NO_FREE_SLOTS: 112,
  GAME_RESET: 113,
  // Hold-to-engage time controls (fork t=114/115): {active:true} on chord press, false on release.
  // The worker releases both on ANY player's disconnect, so a tab closed mid-hold can't wedge the
  // room at 4x.
  GAME_FAST_FORWARD: 114,
  GAME_REWIND: 115,
  APP_VIDEO_CHANGE: 150,
  // RetroAchievements unlock, pushed by the worker (Phase 1) when rcheevos fires an achievement so the room
  // can toast it live. Carries { id, title, description?, points, hardcore }. Inbound only.
  ACHIEVEMENT_UNLOCK: 160,
  RUMBLE: 170, // worker -> this SEAT's connection: the core's rumble state for its pad (perf program P11)
};

// Button → bit positions, CONFIRMED against CloudRetro's JOYPAD_KEYS order (web/js/input/keys.js):
// [B, Y, SELECT, START, UP, DOWN, LEFT, RIGHT, A, X, L, R, L2, R2, L3, R3] — the standard RetroPad order.
export const PAD = { B: 0, Y: 1, SELECT: 2, START: 3, UP: 4, DOWN: 5, LEFT: 6, RIGHT: 7, A: 8, X: 9, L: 10, R: 11, L2: 12, R2: 13, L3: 14, R3: 15 };

// ── Per-system input profiles ────────────────────────────────────────────────────────────────────
// The RetroPad bit layout (PAD) is fixed, but each libretro core maps those bits to native console
// buttons its own way — so the physical→RetroPad map that "feels right" differs per system, and
// CloudRetro exposes no RetroArch remap menu. We reorder here, keyed by the game's `system`.
// RetroPad is itself modeled on the SNES pad, so the DEFAULT positional map (physical south→RetroPad
// B, east→A, west→Y, north→X) is already Nintendo-correct for SNES/NES and is the fallback.

// physical Gamepad-API button index → RetroPad bit. Face indices are 0 south, 1 east, 2 west, 3 north.
const DEFAULT_GAMEPAD = {
  0: PAD.B, 1: PAD.A, 2: PAD.Y, 3: PAD.X,
  4: PAD.L, 5: PAD.R, 6: PAD.L2, 7: PAD.R2,
  8: PAD.SELECT, 9: PAD.START, 10: PAD.L3, 11: PAD.R3,
  12: PAD.UP, 13: PAD.DOWN, 14: PAD.LEFT, 15: PAD.RIGHT,
};

// Keyboard fallback → RetroPad. The ARROW KEYS are deliberately NOT here: they're the keyboard's
// LEFT STICK (handled centrally in the input pump so 3D games can steer) and additionally fold into
// the d-pad per system — see keyboardArrowsDriveDpad. Putting them here would hard-wire arrows→d-pad
// for every system and reproduce the gamepad double-bind on the keyboard (N64 view-pan, Smash taunt).
const DEFAULT_KEYMAP = {
  KeyZ: PAD.B, KeyX: PAD.A, KeyA: PAD.Y, KeyS: PAD.X,
  Enter: PAD.START, ShiftRight: PAD.SELECT, ShiftLeft: PAD.SELECT,
  KeyQ: PAD.L, KeyW: PAD.R,
};

// The keyboard arrow keys, as left-stick directions. Fixed (not per-profile): every system steers
// its left stick the same way. Whether they ALSO press the d-pad is decided per system at runtime.
const ARROW_DIR = { ArrowUp: "up", ArrowDown: "down", ArrowLeft: "left", ArrowRight: "right" };

const PROFILES = {
  default: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: DEFAULT_KEYMAP,
    // Pure-dpad 2D core: the left stick FOLDS into the d-pad (see readGamepad) so an analog-only
    // pad can still steer. Harmless here because stick and d-pad both mean "move" on a 2D game.
    foldStickToDpad: true,
    hint: "Gamepad recommended. Keyboard: arrows = move, Z X A S = buttons, Q W = L/R, Enter = Start, Shift = Select.",
  },
  // ScummVM: the one system here that is primarily a MOUSE game (it joins MOUSE_SYSTEMS, so the real
  // cursor is streamed via RETRO_DEVICE_MOUSE relative deltas — see that set's comment for why POINTER
  // doesn't work here) AND the one core that embeds its own GUI, reachable via the Global Main Menu
  // bound to L3/R3 in config.worker-gl.yaml (scummvm_mapper_l3/r3 -> RETROK_F5/F7).
  //
  // The default keymap has NOTHING on L3/R3 — no keyboard key reaches them — so that menu was pressable
  // only on a gamepad, i.e. unreachable for exactly the players most likely to be here. M for Menu.
  // Scoped to this system rather than added to DEFAULT_KEYMAP: L3/R3 are stick-clicks with real
  // meaning on the 3D consoles, and the SELECT+L3/R3 quick-save chords live on them everywhere.
  scummvm: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: { ...DEFAULT_KEYMAP, KeyM: PAD.L3, KeyN: PAD.R3 },
    // Same as default: a 2D core where stick and d-pad both mean "move the cursor".
    foldStickToDpad: true,
    hint: "Mouse recommended — point and click straight on the picture. M = ScummVM menu (save/load, options). Keyboard: arrows move the cursor, Q W = left/right click, Enter = Start.",
  },
  // N64: mupen64plus-next maps N64 A ← RetroPad **B** and N64 B ← RetroPad A (verified live in
  // Bomberman 64's menus: PAD.B confirms, PAD.A backs out — the earlier assumption here was
  // inverted, which put "back" on the bottom button and made every N64 menu feel broken). So the
  // bottom physical button sends PAD.B (→ N64 A, accelerate/confirm) and east sends PAD.A (→ N64 B).
  // C-buttons ride the RIGHT ANALOG STICK (the core's default) — the pad's right stick, or
  // I/J/K/L on the keyboard. Z is a trigger (LT/E), L/R the bumpers.
  n64: {
    gamepad: {
      ...DEFAULT_GAMEPAD,
      0: PAD.B, // south → N64 A (accelerate / primary / menu-confirm)
      1: PAD.A, // east  → N64 B
    },
    keymap: {
      ...DEFAULT_KEYMAP,
      KeyX: PAD.B, KeyZ: PAD.A, // N64 A (accelerate/confirm) on X, N64 B on Z
      Space: PAD.B,             // big primary key too
      KeyE: PAD.L2,             // Z trigger on E for keyboards without easy triggers
    },
    // Keyboard drive for the right analog stick = N64 C-buttons (camera).
    rstick: { up: "KeyI", down: "KeyK", left: "KeyJ", right: "KeyL" },
    // Analog-native: the N64 reads the control stick and the d-pad as DISTINCT inputs, so the left
    // stick must NOT also press the d-pad — folding double-binds them (Goldeneye pans the view up
    // while you walk forward, because d-pad-up ≠ stick-up). The real stick rides the frame axes.
    foldStickToDpad: false,
    // Keyboard: arrows are the CONTROL STICK only, not the d-pad — the same double-bind reaches the
    // keyboard otherwise (arrows = move AND pan). The d-pad is a rarely-used distinct function here.
    keyboardArrowsAsStickOnly: true,
    hint: "Gamepad recommended (right stick = C-buttons). Keyboard: arrows = steer/move, X = A (accelerate), Z = B, I J K L = C-buttons, E = Z, Q W = L/R, Enter = Start.",
  },
  // PSP: ppsspp maps PSP Cross ← RetroPad B, Circle ← A, Square ← Y, Triangle ← X — so the DEFAULT
  // positional map is already correct (south → Cross/confirm). Analog nub = left stick; L/R are the
  // shoulder buttons. Just a tailored hint + Q/W on the shoulders.
  psp: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: { ...DEFAULT_KEYMAP },
    // Analog-native (the PSP nub is the left stick): don't fold it into the d-pad, or nub-and-dpad
    // games get both inputs at once. The physical d-pad still reaches the PSP d-pad directly.
    foldStickToDpad: false,
    hint: "Gamepad recommended (left stick = analog nub). Keyboard: arrows = D-pad, Z = Cross, X = Circle, A = Square, S = Triangle, Q W = L/R, Enter = Start, Shift = Select.",
  },
  // PS2: pcsx2 maps PS2 Cross ← RetroPad B, Circle ← A, Square ← Y, Triangle ← X — same as ps1, so
  // the DEFAULT positional map already fits (south → Cross/confirm). It gets its OWN entry (rather
  // than falling through to `default`) for one reason: the DualShock 2 is analog-native, so the
  // left stick must NOT fold into the d-pad the way a 2D core's does.
  ps2: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: {
      ...DEFAULT_KEYMAP,
      KeyE: PAD.L2, KeyR: PAD.R2, // L2/R2 on E/R so A/S keep Square/Triangle
    },
    foldStickToDpad: false,
    hint: "Gamepad recommended (both sticks work — DualShock). Keyboard: arrows = D-pad/left stick, Z = Cross, X = Circle, A = Square, S = Triangle, Q W = L1/R1, E R = L2/R2, Enter = Start, Shift = Select.",
  },
  // PS1: pcsx_rearmed maps PS Cross ← RetroPad B, Circle ← A, Square ← Y, Triangle ← X — so the DEFAULT
  // positional map is already correct (south → Cross/confirm). With the DualShock pad type (config.yaml),
  // both analog sticks ride the frame (encodeInput lx/ly/rx/ry) — the left stick drives games like Ape
  // Escape that demand an analog controller; L1/L2/R1/R2 are the shoulders/triggers. Just a tailored hint.
  ps1: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: {
      ...DEFAULT_KEYMAP,             // Z=Cross(B), X=Circle(A), A=Square(Y), S=Triangle(X), Q/W=L1/R1
      KeyE: PAD.L2, KeyR: PAD.R2,    // L2/R2 on E/R so A/S KEEP Square/Triangle — SotN's main attack
    },
    // Analog-native (DualShock): the left stick drives games like Ape Escape and must stay OFF the
    // d-pad, or those double-bind. The physical d-pad still reaches the PS1 d-pad directly.
    foldStickToDpad: false,
    hint: "Gamepad recommended (both sticks work — DualShock). Keyboard: arrows = D-pad/left stick, Z = Cross, X = Circle, A = Square, S = Triangle, Q W = L1/R1, E R = L2/R2, Enter = Start, Shift = Select.",
  },
  // Dreamcast: flycast maps DC A ← RetroPad B, B ← A, X ← Y, Y ← X (south → A/confirm, so the default
  // positional map fits). The DC triggers are ANALOG L/R → RetroPad L2/R2, not the bumpers, so put the
  // keyboard triggers on L2/R2. Analog stick = left stick.
  dc: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: {
      ...DEFAULT_KEYMAP,
      KeyQ: PAD.L2, KeyW: PAD.R2, // DC analog triggers
    },
    // Analog-native (the DC pad's analog stick is the primary movement input): no d-pad fold.
    foldStickToDpad: false,
    hint: "Gamepad recommended (left stick = analog; triggers = L/R). Keyboard: arrows = move, Z = A, X = B, A = X, S = Y, Q W = triggers, Enter = Start.",
  },
  // GameCube: dolphin_libretro maps GC buttons to the matching RetroPad letters (GC A ← RetroPad A,
  // B ← B, X ← X, Y ← Y). GC A is the big primary/confirm button, so — as with the n64 A/B fix — put it
  // on the physical SOUTH button (south → PAD.A → GC A, east → PAD.B → GC B) instead of the naive
  // positional map, which would strand "confirm" on the east button. The GC C-STICK rides the RIGHT
  // ANALOG STICK (camera in most games) — the pad's right stick, or I/J/K/L on the keyboard. Z is a
  // trigger (E on the keyboard). GC L/R are analog triggers → RetroPad L2/R2, so the keyboard triggers
  // sit there. NOTE: face + Z mapping is unverified live yet (mirrors the n64 reasoning) — confirm with
  // the test-roms skill on a menu-heavy title (e.g. Melee) and adjust the swap if it feels inverted.
  gc: {
    gamepad: {
      ...DEFAULT_GAMEPAD,
      0: PAD.A, // south → GC A (primary / confirm)
      1: PAD.B, // east  → GC B
    },
    keymap: {
      ...DEFAULT_KEYMAP,
      KeyX: PAD.A, KeyZ: PAD.B, // GC A (confirm) on X, GC B on Z
      Space: PAD.A,             // big primary key too
      KeyQ: PAD.L2, KeyW: PAD.R2, // GC analog L/R triggers
      KeyE: PAD.R,              // GC Z trigger
    },
    // Keyboard drive for the right analog stick = GC C-stick (camera).
    rstick: { up: "KeyI", down: "KeyK", left: "KeyJ", right: "KeyL" },
    // Analog-native: the GC reads the control stick and the d-pad as DISTINCT inputs, so the left
    // stick must NOT also press the d-pad — folding double-binds them (in Smash the d-pad is TAUNT,
    // so pushing the stick made the character taunt). The real stick rides the frame axes.
    foldStickToDpad: false,
    // Keyboard: arrows are the CONTROL STICK only — otherwise the keyboard taunts in Smash too.
    keyboardArrowsAsStickOnly: true,
    hint: "Gamepad recommended (right stick = C-stick; triggers = L/R). Keyboard: arrows = move, X = A (confirm), Z = B, I J K L = C-stick, Q W = L/R triggers, E = Z, Enter = Start.",
  },
  // Wii, per-port device Wiimote+Nunchuk (config.worker-gl.yaml `hid` → RETRO_DEVICE_WIIMOTE_NC).
  // dolphin_libretro binds this straight to RetroPad letters (Wiimote A ← RetroPad B, Wiimote B ← A,
  // Nunchuk C ← X, Nunchuk Z ← Y, Wiimote -/+ ← L/R) — the DEFAULT face-button map already lines up
  // (south=primary, same as every other system here), so no override needed there, unlike n64/gc.
  // LEFT STICK is the Nunchuk analog (primary movement input for most games); RIGHT STICK is the Wiimote
  // IR pointer (dolphin_ir_mode, core side). Swing gestures / Nunchuk shake sit behind L2
  // (dolphin_swing_modifier: "L2"; Nunchuk shake unconditionally on L2 per core's hardcoded binding).
  // Issue: after player assignment (t=108), input can stop if core/port state diverges. If affected,
  // try test-roms skill to verify frame-by-frame input or check config.worker-gl.yaml port binding.
  wii: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: {
      ...DEFAULT_KEYMAP,
      KeyE: PAD.L2, // hold = swing / Nunchuk shake
    },
    // Keyboard drive for the right analog stick = Wiimote IR pointer (dolphin_ir_mode reads it).
    // LEFT STICK (axes 0/1) is Nunchuk analog — already handled by readGamepad's axis mapping.
    rstick: { up: "KeyI", down: "KeyK", left: "KeyJ", right: "KeyL" },
    // Analog-native (the Nunchuk stick is the movement input): no d-pad fold, or the stick also
    // presses the Wii d-pad — which is a distinct input (in GC-mode Smash it's the TAUNT).
    foldStickToDpad: false,
    // Keyboard: arrows are the Nunchuk STICK only, not the Wii d-pad (same taunt double-bind).
    keyboardArrowsAsStickOnly: true,
    hint: "Gamepad recommended (left stick = Nunchuk movement; right stick = Wiimote pointer; hold L2 to swing). Keyboard: arrows = move, Z = A (confirm), X = B, A = Nunchuk Z, S = Nunchuk C, I J K L = pointer, E = swing/shake, Enter = 1, Shift = 2.",
  },
  // HEAVY/CAPTURE LANE (switch/ps3/ps4/wiiu/x360/pc — arcadeSystems.HEAVY_LANE_SYSTEMS). These are
  // native apps driven by a real ViGEm X360 pad, not libretro cores, so the RetroPad bits are just a
  // transport: the worker replays them onto the virtual pad and the GAME reads a modern dual-analog
  // controller. Every one of these consoles is analog-native and reads the stick and the d-pad as
  // DISTINCT inputs — on Bloodborne the d-pad is item/spell/weapon quick-select, so folding made
  // walking cycle your items (reported 2026-08-02).
  //
  // These had NO profile entry at all and therefore fell through to `default`, which folds. That is
  // the exact hole ps2 was given its own entry to close; the heavy lane arrived later and reopened
  // it for six more systems at once. Assigned by spread below so a new heavy system inherits it.
  ...Object.fromEntries(
    ["switch", "ps3", "ps4", "wiiu", "x360", "pc", "capture"].map((sys) => [sys, {
      gamepad: DEFAULT_GAMEPAD,
      keymap: {
        ...DEFAULT_KEYMAP,
        KeyE: PAD.L2, KeyR: PAD.R2, // triggers on E/R so A/S keep the west/north face buttons
      },
      // Keyboard drive for the right analog stick = the camera on every one of these.
      rstick: { up: "KeyI", down: "KeyK", left: "KeyJ", right: "KeyL" },
      // Analog-native: never fold the left stick into the d-pad (see the block comment above).
      foldStickToDpad: false,
      // Keyboard: arrows are the LEFT STICK only. The d-pad is a distinct function here (item select),
      // so arrows must not press it — the keyboard twin of the same double-bind.
      keyboardArrowsAsStickOnly: true,
      hint: "Gamepad strongly recommended (both sticks + triggers). Keyboard: arrows = move, I J K L = camera, Z X A S = face buttons, Q W = shoulders, E R = triggers, Enter = Start, Shift = Select.",
    }]),
  ),
};

export function profileFor(system) {
  return PROFILES[(system || "").toLowerCase()] || PROFILES.default;
}

// Two Wii SD-loader BrawlEx mods are configured worker-side (config.worker-gl.yaml `hid4rom`) to
// DEFAULT to RETRO_DEVICE_GC_ON_WII — real GameCube controllers, like real Brawl — instead of the
// Wiimote+Nunchuk every other Wii title defaults to. This set is now ONLY the default-GC list: the
// GameCube/Wiimote picker is offered on every Wii title, but when a room sends no explicit scheme
// (older invite link / absent param) these keys fall back to "gc" and all others to "wiimote".
// Keyed by CloudRetroGameKey, not title, to match the worker's own hid4rom lookup (romName = ROM
// filename sans extension).
const GC_ON_WII_GAME_KEYS = new Set(["Project REX", "Super Smash Bros Infinite"]);

// Resolves the system to use for INPUT purposes (button/stick profile, mapping-tool preview,
// custom-remap storage). For a Wii game the room's controller scheme decides it; every non-Wii
// system is unaffected. `controllerScheme` is the room's resolved choice ("gc"/"wiimote"/"" — from
// descriptor.wsUrl's ctrlscheme param, which Join/ClaimSeat echo onto EVERY player's descriptor,
// not just the creator's, because it changes what button bits every client must send). An explicit
// "gc" uses the GameCube RetroPad profile for ANY Wii title (matching the worker's hidGc fallback);
// "wiimote" uses the Wii profile; "" falls back to the per-game default (the BrawlEx mods → gc,
// everything else → wiimote), so a scheme-less older link still behaves as before.
export function effectiveInputSystem(system, gameKey, controllerScheme) {
  if (system !== "wii") return system;
  if (controllerScheme === "gc") return "gc";
  if (controllerScheme === "wiimote") return system;
  return GC_ON_WII_GAME_KEYS.has(gameKey) ? "gc" : system;
}

// Small purpose-built export (rather than exposing the generic strFromWsUrl helper below) — the
// room's resolved Wii controller scheme, read off a descriptor the same way startGame() does.
// ArcadeRoomPage.js needs this to feed effectiveInputSystem for its mapping/rebind panel and hint.
export function controllerSchemeFromWsUrl(wsUrl) {
  return strFromWsUrl(wsUrl, "ctrlscheme");
}

// ── Local multiplayer: pad ownership across sessions ─────────────────────────────────────────────
// One browser can hold SEVERAL CloudRetro connections (the primary + one input-only session per extra
// local controller — the wire protocol routes input by connection, so an extra pad needs an extra
// connection). Each extra session is PINNED to one Gamepad-API index; this registry is how the primary
// session's adopt-any-active-pad heuristic knows to leave those pads alone.
const claimedPadIndexes = new Set();

// ── Face-button swap (Nintendo/PlayStation ↔ Xbox layout) ────────────────────────────────────────
// The per-system PROFILES map by physical POSITION (Gamepad-API 0 south, 1 east, 2 west, 3 north),
// which is right for one label layout and backwards for the other: Nintendo/PlayStation pads put
// confirm on the position DEFAULT_GAMEPAD already targets, Xbox pads mirror it. This used to be one
// manual machine-wide boolean; it's now auto-detected PER PAD from the controller's own identity
// (controllerIdentity.js classifies gp.id into a family — DualSense/DualShock4/Xbox/Switch Pro/
// generic), with a manual override for pads that misreport — machine-wide as a default, and
// PER PAD (getPadFaceSwapOverride/setPadFaceSwapOverride) as the one that actually wins, so local
// multiplayer on one machine can mix an Xbox pad and a Switch pad and get both right. Barrel-
// exported here so ArcadeRoomPage.js keeps importing the whole arcade-shim surface from one module.
export {
  controllerFamilyFor,
  controllerLabelFor,
  getFaceSwapMode,
  setFaceSwapMode,
  getPadFaceSwapOverride,
  setPadFaceSwapOverride,
  effectiveFaceSwap,
} from "./controllerIdentity";
export { mappingRowsFor, SYSTEM_BUTTON_LABELS } from "./controllerMapDisplay";

// ── Streamed-pad guard (heavy lane, docs/arcade-heavy-lane-plan.md §6.3) ─────────────────────────
// When this machine hosts Moonlight/Apollo streams, every guest controller is forwarded as a ViGEm
// virtual Xbox 360 pad — which the Gamepad API cannot tell apart from a REAL Xbox pad (both report
// XInput with the stock VID/PID; the ROOT\ViGEmBus enumerator is invisible to browsers). Without a
// guard, a streamed guest mashing buttons gets auto-adopted into a CloudRetro seat here. The host
// machine's saving grace is a deliberate invariant: Apollo is configured `gamepad = x360` host-wide
// and the host's physical pads are non-Xbox (Pro Controller / DualSense), so ON THAT MACHINE
// XInput ⇒ streamed. This machine-wide toggle (enable it only on the stream host) makes the two
// AUTOMATIC paths — the primary's fluid adoption and the press-a-button detector — skip XInput
// pads. Explicit assignment in the Controllers panel still works on any pad: a deliberate override.
// A stable, opaque id for THIS browser, sent with every arcade session so the worker's link
// measurements can be filed per DEVICE rather than per user (ABR quality plan, Phase 0). One person's
// wired desktop, tablet and phone must never share link history: a rate proven on the desktop, applied
// to the Wi-Fi tablet, is exactly the collapse the conservative bitrate opener exists to prevent.
//
// Deliberately NOT the site's `mt-device-token` — that one keys Jellyfin transcode directories, and
// tying a streaming-quality identity to it would silently couple two unrelated subsystems.
// Random and meaningless by construction: it identifies a browser profile to itself, nothing more.
const ARCADE_DEVICE_ID_KEY = "arcade.deviceId";
let cachedArcadeDeviceId = null;
export function arcadeDeviceId() {
  if (cachedArcadeDeviceId) return cachedArcadeDeviceId;
  let id = "";
  try {
    id = localStorage.getItem(ARCADE_DEVICE_ID_KEY) || "";
    if (!id) {
      id = window.crypto?.randomUUID?.() || `d${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
      localStorage.setItem(ARCADE_DEVICE_ID_KEY, id);
    }
  } catch {
    // Private mode / storage disabled: no durable identity is possible, so send nothing rather than a
    // per-tab value that would pollute the table with single-use devices.
    id = "";
  }
  // Match the worker's and the site's sanitiser so all three agree on the stored key.
  cachedArcadeDeviceId = id.replace(/[^A-Za-z0-9-]/g, "").slice(0, 64);
  return cachedArcadeDeviceId;
}

let ignoreStreamedPads = (() => { try { return localStorage.getItem("arcade.ignoreStreamedPads") === "1"; } catch { return false; } })();
export function getIgnoreStreamedPads() { return ignoreStreamedPads; }
export function setIgnoreStreamedPads(on) {
  ignoreStreamedPads = !!on;
  try { localStorage.setItem("arcade.ignoreStreamedPads", on ? "1" : "0"); } catch { /* storage disabled */ }
}
// True when the guard is ON and this pad presents as XInput (Chrome: "Xbox 360 Controller (XInput
// STANDARD GAMEPAD)"; Firefox: "xinput"). Accepts anything with an `id` — real Gamepad or the
// panel's {index,id} row.
export function isStreamedPad(gp) {
  return ignoreStreamedPads && !!gp && /xinput/i.test(gp.id || "");
}

// ── Echo guard: our OWN output can come back as "a controller pressing buttons" ──────────────────
// The capture lane (docs/arcade-capture-worker-plan.md §4.5) drives a ViGEm virtual Xbox 360 pad on
// the CAPTURE HOST so the streamed app sees a controller. When the browser playing that room runs ON
// THAT SAME MACHINE — the normal way this gets tested on the host — Chrome enumerates that virtual
// pad exactly like a real Xbox pad, and it mirrors, one round trip later, whatever we send. That
// turns the adoption heuristic below into a latch: the poll after the player lets go, THEIR pad reads
// idle while the echo still reads pressed (our release is in flight), so the seat is handed to the
// echo — and from then on the session is reading its own output and re-sending it. Observed live
// 2026-07-28: one tap of A in SM64 (pc-sm64-plus, a capture title) became A tapping forever until a
// page refresh. It oscillates rather than sticks because the echo presents as an Xbox pad, so
// effectiveFaceSwap flips the face buttons and each round trip maps back to the OTHER bit.
//
// The guard is structural, not a heuristic: an echo can only look active because our output is (or
// just was) non-neutral, whereas a real pad the player picks up is active while we send nothing. So
// auto-adoption simply refuses to change pads inside a short window after our own last non-neutral
// frame. It costs a real re-adoption at most ECHO_WINDOW_MS (the player is by definition not touching
// the old pad), needs no per-machine config, and covers the Moonlight-host case the manual
// ignoreStreamedPads toggle above was added for. Pinned pads and explicit panel assignment bypass it.
const ECHO_WINDOW_MS = 500;
// Module-scope on purpose: with local multiplayer one machine holds several sessions, and ANY of
// them driving the host's virtual pad can be the thing another session sees echoed back.
let lastNonNeutralOutputAt = 0;

// ── Phantom pads: the corpses a disconnect/reconnect leaves in the Gamepad API ───────────────────
// Unplug a pad (or let a Bluetooth one sleep) and plug it back in, and the browser does not always
// retire the old slot: getGamepads() keeps returning a non-null entry that will never report again,
// while the SAME controller reappears at a new index. The corpse is not harmless — it shows up in the
// Controllers panel as an assignable controller, it can be latched by the primary's fluid adoption
// (and if it froze mid-press it reads that button as held FOREVER, which no amount of releasing the
// real pad can undo), and a local player pinned to the old index just goes dead.
//
// There is no flag that says "corpse": a phantom looks exactly like a real pad nobody is touching.
// What distinguishes it is a TWIN — the same model id at another index that IS still reporting. So
// liveness is tracked by observation (has this slot's sample timestamp advanced lately?) and an entry
// is only condemned when a live twin exists. Consequences of that choice, both deliberate:
//   • a stale entry with no twin stays listed — we cannot prove it dead, and hiding someone's idle
//     controller is worse than showing one ghost;
//   • two REAL identical pads: while one is in use and the other has sat untouched past the stale
//     window, the untouched one is hidden — and it comes straight back the moment it is touched,
//     which is also exactly how a player "wakes" a pad Chrome hasn't surfaced yet.
const PAD_STALE_MS = 3000;
const padSeen = new Map();      // index -> { id, ts, changedAt } — as last OBSERVED, not as claimed
let phantomIndexes = new Set(); // recomputed by notePads on every poll

// Sample the pad table into the liveness registry. Every entry point that reads pads calls this
// first, so the registry stays fresh at whatever the fastest poller's rate is (readGamepad, 60 Hz).
function notePads(pads) {
  const now = Date.now();
  const present = new Set();
  for (const p of pads) {
    if (!p) continue;
    present.add(p.index);
    const rec = padSeen.get(p.index);
    // A different id at the same index means the slot was RECYCLED — a new pad, not the old one
    // going quiet, so start its observation over rather than inheriting the corpse's staleness.
    if (!rec || rec.id !== p.id) padSeen.set(p.index, { id: p.id, ts: p.timestamp, changedAt: now, firstActiveAt: 0 });
    else if (p.timestamp !== rec.ts) { rec.ts = p.timestamp; rec.changedAt = now; }
    // WHEN this pad first played — the one durable way to tell the controller that was ALREADY
    // playing from one that just dropped in (see incumbentPad). Scanned only until it is known, so
    // once every pad in the room has been touched this costs a single truthy test per pad per poll.
    const cur = padSeen.get(p.index);
    if (!cur.firstActiveAt && p.buttons && Array.prototype.some.call(p.buttons, (b) => b && b.pressed)) {
      cur.firstActiveAt = now;
    }
  }
  for (const idx of padSeen.keys()) if (!present.has(idx)) padSeen.delete(idx);

  const fresh = new Set();
  const next = new Set();
  for (const p of pads) {
    if (!p) continue;
    const rec = padSeen.get(p.index);
    if (rec && now - rec.changedAt < PAD_STALE_MS) fresh.add(p.index);
  }
  for (const p of pads) {
    if (!p || fresh.has(p.index)) continue;
    // Stale. Condemn it only if a live twin — same model, another index, a NEWER sample — exists.
    const hasLiveTwin = Array.prototype.some.call(pads, (q) =>
      q && q.index !== p.index && q.id === p.id && fresh.has(q.index) && q.timestamp > p.timestamp);
    if (hasLiveTwin) next.add(p.index);
  }
  phantomIndexes = next;
}

/** True when this entry is a corpse the browser hasn't retired (or an obviously hollow slot). */
export function isPhantomPad(gp) {
  if (!gp) return true;
  if (gp.connected === false) return true;            // the spec's own answer, when a browser gives it
  if (!gp.buttons || gp.buttons.length === 0) return true; // hollow slot: nothing to read anyway
  return phantomIndexes.has(gp.index);
}

/**
 * The pad the PRIMARY seat should KEEP when another controller drops in: of the pads this machine can
 * still assign — live, not already claimed by a local seat, not a streamed/virtual pad — the one that
 * STARTED PLAYING FIRST. -1 when no pad here has ever been touched (a keyboard-only host).
 *
 * ⚠ This exists because "which pad is P1's?" MUST NOT be answered by asking the primary session what
 * it is driving right now. The primary adopts whichever free pad is momentarily active — that is the
 * whole point of fluid adoption, and it is correct while one person plays. But it means that in the
 * instant a SECOND controller is first pressed (which is exactly when auto-bind runs), the primary
 * may already have adopted the NEWCOMER. Sampling there inverts the two: the new pad is mistaken for
 * P1's, gets pinned to P1, and the incumbent's own controller — still unclaimed and being pressed —
 * is handed to the new local seat. The player who was already playing then finds their controller
 * driving P2, i.e. "my controller stopped working the moment a second one was detected" (reported
 * live 2026-08-01). First-input order cannot invert, so ask that instead.
 */
export function incumbentPad(excludeIndexes = []) {
  const pads = navigator.getGamepads ? navigator.getGamepads() : [];
  notePads(pads);
  let best = -1;
  let bestAt = Infinity;
  for (const gp of pads) {
    if (!gp || claimedPadIndexes.has(gp.index) || excludeIndexes.includes(gp.index)) continue;
    if (isStreamedPad(gp) || isPhantomPad(gp)) continue;
    const rec = padSeen.get(gp.index);
    const at = (rec && rec.firstActiveAt) || 0;
    if (!at || at >= bestAt) continue; // never played, or someone else played earlier
    best = gp.index;
    bestAt = at;
  }
  return best;
}

/**
 * The whole auto-bind decision, in one place so it can be tested as a unit: given P1's pin (or null
 * while its adoption is still fluid), return the pad P1 must KEEP and the pad a new local seat should
 * take. `candidate` is -1 when no new controller is asking to play, which is the common case.
 *
 * The invariant worth stating out loud, because violating it is the bug this replaced:
 * **candidate is never the pad that was already playing.** incumbent is resolved first and excluded
 * from the search, so the seat that exists cannot be handed someone else's controller.
 */
export function pickAutoBindPads(pinnedPad = null) {
  const incumbent = Number.isInteger(pinnedPad) && pinnedPad >= 0 ? pinnedPad : incumbentPad();
  return { incumbent, candidate: findNewPad(incumbent >= 0 ? [incumbent] : []) };
}

/** The pads worth showing/assigning: present, non-hollow, not a corpse with a live twin. */
export function livePads() {
  const pads = navigator.getGamepads ? navigator.getGamepads() : [];
  notePads(pads);
  return Array.prototype.filter.call(pads, (p) => p && !isPhantomPad(p));
}

// ── Gamepad button rebinding ────────────────────────────────────────────────────────────────
// Custom gamepad profiles override the system defaults. Maps physical button index -> RetroPad bit.
// Stored per-system in localStorage.
let customGamepadProfiles = (() => {
  try {
    const stored = localStorage.getItem("arcade.customGamepadProfiles");
    return stored ? JSON.parse(stored) : {};
  } catch {
    return {};
  }
})();

export function getCustomGamepadProfile(system = "default") {
  return customGamepadProfiles[system] || {};
}

export function setCustomGamepadProfile(profile, system = "default") {
  customGamepadProfiles[system] = profile;
  try {
    localStorage.setItem("arcade.customGamepadProfiles", JSON.stringify(customGamepadProfiles));
  } catch { /* storage disabled */ }
}

export function resetCustomGamepadProfile(system = "default") {
  delete customGamepadProfiles[system];
  try {
    localStorage.setItem("arcade.customGamepadProfiles", JSON.stringify(customGamepadProfiles));
  } catch { /* storage disabled */ }
}

// Custom quick-action CHORD binds. Global (not per-system): a chord is a set of RetroPad bit names
// (the mapping OUTPUT), so it means the same thing everywhere. Shape: { quickSave: ["SELECT","B"], … };
// a missing action keeps its shipped default (controllerChords.resolveChords). The room's chord watcher
// re-reads this via session.reloadChords() after a rebind, so it takes effect live like button remaps.
let customChordBinds = (() => {
  try {
    const stored = localStorage.getItem("arcade.customChords");
    return stored ? JSON.parse(stored) : {};
  } catch {
    return {};
  }
})();

export function getCustomChords() { return customChordBinds; }

export function setCustomChords(binds) {
  customChordBinds = binds || {};
  try { localStorage.setItem("arcade.customChords", JSON.stringify(customChordBinds)); } catch { /* storage disabled */ }
}

export function resetCustomChords() {
  customChordBinds = {};
  try { localStorage.removeItem("arcade.customChords"); } catch { /* storage disabled */ }
}

// ── Left-stick → D-pad fold (per-system, user-overridable) ───────────────────────────────────
// Whether the analog LEFT STICK ALSO presses the d-pad bits. Correct ONLY for pure-dpad 2D cores,
// where stick and d-pad both mean "move" so an analog-only pad can still steer. On an analog-native
// console (n64/gc/wii/ps1/ps2/psp/dc) the machine reads the d-pad and the stick as DISTINCT inputs,
// so folding DOUBLE-BINDS them — the cause of "N64 Goldeneye pans the view up as I walk forward"
// (d-pad-up ≠ stick-up) and "Wii/GC Smash taunts when I push the stick" (Smash reads the d-pad as
// taunt). The default is per-profile (PROFILES[system].foldStickToDpad); a saved user override wins,
// so the rare edge case (a d-pad-less pad on a 3D game) can flip it back from the mapping panel.
// Keyed per input-system, same as customGamepadProfiles.
let customStickFold = (() => {
  try {
    const stored = localStorage.getItem("arcade.customStickFold");
    return stored ? JSON.parse(stored) : {};
  } catch {
    return {};
  }
})();

export function getStickFoldOverride(system = "default") {
  return customStickFold[(system || "").toLowerCase()]; // true | false | undefined (undefined ⇒ profile default)
}

export function setStickFoldOverride(on, system = "default") {
  customStickFold[(system || "").toLowerCase()] = !!on;
  try {
    localStorage.setItem("arcade.customStickFold", JSON.stringify(customStickFold));
  } catch { /* storage disabled */ }
}

export function resetStickFoldOverride(system = "default") {
  delete customStickFold[(system || "").toLowerCase()];
  try {
    localStorage.setItem("arcade.customStickFold", JSON.stringify(customStickFold));
  } catch { /* storage disabled */ }
}

// Effective fold for a system: a saved user override wins, else the profile default.
export function stickFoldFor(system) {
  const override = getStickFoldOverride(system);
  if (override !== undefined) return !!override;
  return profileFor(system).foldStickToDpad === true;
}

// ── Right-stick left/right swap (per-game, user-toggled) ─────────────────────────────────────
// Mirrors the right stick's X axis before it rides the frame. Plenty of 5th-gen 3D titles read the
// camera stick the opposite way round from the modern convention (the N64 C-buttons are the usual
// offender — the right stick IS the C-pad there, and a game that pans the camera with C-left/C-right
// feels inverted to anyone who learned the layout after 1999). The fix belongs here rather than in a
// rebind: the mapping panel's per-button rebinding works on BUTTON bits, and the right stick is sent
// as two analog axes, so there is no pair of bits to exchange. Y is deliberately untouched — vertical
// camera inversion is nearly always a setting inside the game itself.
//
// Keyed per GAME, not per system: mirroring is a property of the title (Zelda pans one way, Goldeneye
// the other), so a per-system flag would be wrong for half the library the moment it was set. Off
// everywhere by default — no profile default to consult, unlike the stick fold above.
let rightStickSwaps = (() => {
  try {
    const stored = localStorage.getItem("arcade.rightStickSwapX");
    return stored ? JSON.parse(stored) : {};
  } catch {
    return {};
  }
})();

// The storage key for a room. Falls back to the system when the game key isn't known yet (an invitee
// before its descriptor resolves) so the toggle still does SOMETHING sane rather than writing under
// a shared blank key that every such room would then share.
function rightStickSwapKey(gameKey, system) {
  return String(gameKey || `sys:${system || ""}`).toLowerCase();
}

export function getRightStickSwapX(gameKey, system) {
  return rightStickSwaps[rightStickSwapKey(gameKey, system)] === true;
}

export function setRightStickSwapX(on, gameKey, system) {
  const k = rightStickSwapKey(gameKey, system);
  if (on) rightStickSwaps[k] = true; else delete rightStickSwaps[k]; // off IS the default — don't store it
  try {
    localStorage.setItem("arcade.rightStickSwapX", JSON.stringify(rightStickSwaps));
  } catch { /* storage disabled */ }
}

// Whether the keyboard ARROW keys should ALSO press the d-pad. Arrows always drive the LEFT STICK
// (so 3D games can steer); this decides the d-pad half. It mirrors the gamepad fold but is NOT the
// same call, because the keyboard has only one directional input: on a stick-primary console whose
// d-pad is a distinct function (n64/gc/wii) arrows must be stick-ONLY, or the keyboard reproduces the
// Goldeneye view-pan / Smash taunt double-bind; but on a d-pad-movement console (2D, and PS1's many
// digital-only games) arrows MUST keep driving the d-pad or the keyboard can't move at all. The live
// fold state wins either way — turning "left stick also acts as d-pad" on restores arrows→d-pad.
export function keyboardArrowsDriveDpad(system, foldStickToDpad) {
  if (foldStickToDpad) return true;
  return profileFor(system).keyboardArrowsAsStickOnly !== true;
}

/**
 * One poll pass looking for "the new player pressed a button": returns the index of a connected pad
 * with any button currently pressed that is neither claimed by a local-player session nor in
 * `excludeIndexes` (the caller passes the primary's current pad), or -1. The room page polls this
 * after "Add local player" so the new controller identifies itself the way consoles do.
 */
export function findNewPad(excludeIndexes = []) {
  const pads = navigator.getGamepads ? navigator.getGamepads() : [];
  notePads(pads);
  for (const gp of pads) {
    if (!gp || claimedPadIndexes.has(gp.index) || excludeIndexes.includes(gp.index) || isStreamedPad(gp)) continue;
    // A pad that was unplugged mid-press keeps reporting that button as held; without this the
    // "press a button" detector would hand the new seat a corpse that can never let go.
    if (isPhantomPad(gp)) continue;
    if (gp.buttons.some((b) => b.pressed)) return gp.index;
  }
  return -1;
}

/** The on-screen control hint for a system's input profile (rendered by the room page). */
export function arcadeInputHint(system) {
  return profileFor(system).hint;
}

// Wire frame — CONFIRMED against CloudRetro web/js/input/retropad.js (2026-07-02): five int16s,
// platform-endian typed array (LE in practice, exactly what the stock client sends) —
//   [buttonBitmap, leftStickX, leftStickY, rightStickX, rightStickY]
// Axes are trunc(clamp(-1,1) * 32767). There is NO player-index byte: the worker already knows
// whose input this is from the connection's controller port (t=104 player_index / t=108).
function encodeInput(mask, axes) {
  const frame = new Int16Array(5);
  frame[0] = mask;
  for (let i = 0; i < 4; i++) frame[i + 1] = axes[i] | 0;
  return frame;
}

// Systems whose core consumes RETRO_DEVICE_POINTER (touch/stylus). The core maps a full-frame
// normalized pointer through its screen layout to the touch panel itself (melonDS DS), so the client
// stays layout-agnostic — it only delivers where in the video frame the pointer is. Extensible.
// 3ds joins unchanged (2026-07-24): citra's MouseTracker does the same full-frame->layout transform
// melonDS does (normalized pointer -> frame pixels -> clamped into the bottomScreen rect), so nothing
// client-side is 3DS-specific. Needs citra_touch_touchscreen:"enabled" in config.worker-gl.yaml —
// without it citra ignores POINTER_PRESSED and taps never register.
// scummvm does NOT belong here (tried 2026-07-27, corrected same day) — see MOUSE_SYSTEMS below for why
// a mouse game needs RETRO_DEVICE_MOUSE instead of RETRO_DEVICE_POINTER.
const POINTER_SYSTEMS = new Set(["nds", "3ds"]);
export function systemUsesPointer(system) { return POINTER_SYSTEMS.has(String(system || "").toLowerCase()); }

// scummvm (2026-07-27, corrected same day): NOT a POINTER_SYSTEMS member — RETRO_DEVICE_POINTER was the
// wrong device for it. Verified against the core's own source (backends/platform/libretro/src/
// libretro-os-inputs.cpp): the ScummVM cursor only moves on a PRESSED transition or while held, because
// RETRO_DEVICE_POINTER models a touchscreen (no touch = no valid position, by design) — melonDS/citra
// happen to read X/Y regardless of pressed for their own crosshair rendering, ScummVM does not, so our
// hover-with-pressed=0 packets were received and silently never applied. A real desktop mouse cursor
// needs RETRO_DEVICE_MOUSE (relative deltas), which ScummVM applies unconditionally every poll — that's
// MOUSE_SYSTEMS below, a completely separate wire path (stock CloudRetro's own worker-opened "mouse"
// DataChannel, never wired into this shim before now).
const MOUSE_SYSTEMS = new Set(["scummvm"]);
export function systemUsesMouse(system) { return MOUSE_SYSTEMS.has(String(system || "").toLowerCase()); }

// FALLBACK ONLY. The core reads RETRO_DEVICE_MOUSE deltas in ITS OWN unscaled coordinate space (clamped
// to getScreenWidth()/Height() — the real internal game resolution), while videoEl.videoWidth/Height is
// that size AFTER the worker's server-side upscale, so a delta taken straight off the video element is
// too large by whatever that factor is. mseScale() normally divides it out using the worker's own
// APP_VIDEO_CHANGE payload (av.w/av.h = the frame the core actually renders), which is exact and needs
// no parity with any config value. This constant is only used if that packet never arrived, and is a
// guess at the config's `scale` — deliberately NOT load-bearing, because that config value is now a
// per-game CEILING (scaleMaxWidth), not a fixed multiplier, so no single number here could be right.
const SCUMMVM_VIDEO_SCALE = 3;

// Pointer cores map the libretro pointer through their OWN screen layout in DISPLAY (top-down) space,
// INDEPENDENT of the GL framebuffer flip — that is the RETRO_DEVICE_POINTER convention (coords are in
// the space the frame is PRESENTED in, not the raw GL buffer). Since 2026-07-24 both nds (melonDS
// OpenGL) and 3ds (citra GL) render via GL, so their rooms send av.flip and the video is displayed
// scaleY(-1) — but undoing that flip on the POINTER double-inverts Y and every tap lands vertically
// mirrored (confirmed live on Phoenix Wright New Game AND Mario Kart 7 OK, 2026-07-24). So no current
// pointer core wants the flip undone; the set is kept (== POINTER_SYSTEMS today) to document WHY and to
// leave room for a hypothetical future core that genuinely hit-tests in raw-framebuffer space.
const POINTER_DISPLAY_SPACE_SYSTEMS = new Set(["nds", "3ds"]);
export function pointerIgnoresFrameFlip(system) { return POINTER_DISPLAY_SPACE_SYSTEMS.has(String(system || "").toLowerCase()); }

// Pointer wire packet (W10 stylus/touch) — rides the SAME "data" channel as the pad frame, length+tag
// discriminated (pad = 10-byte Int16Array; this = fixed 8 bytes, magic tag 0xF0). x/y are FULL-FRAME
// normalized (-32767..32767), LITTLE-ENDIAN to match the pad channel. Matches the worker's PointerState.
//   [tag:1=0xF0][ver:1=1][x:i16 LE][y:i16 LE][pressed:1][flags:1]
export function encodePointer(x, y, pressed) {
  const buf = new ArrayBuffer(8);
  const dv = new DataView(buf);
  dv.setUint8(0, 0xf0);
  dv.setUint8(1, 0x01);
  dv.setInt16(2, x, true);
  dv.setInt16(4, y, true);
  dv.setUint8(6, pressed ? 1 : 0);
  dv.setUint8(7, 0);
  return buf;
}

// Relative-mouse wire packets (RETRO_DEVICE_MOUSE), on the worker's own dedicated "mouse" DataChannel —
// this is STOCK CloudRetro's own protocol (pkg/worker/coordinatorhandlers.go `s.Channel("mouse", ...)`
// → InputMouse → MouseState.ShiftPos/SetButtons), never previously wired into this shim.
// ⚠ That channel only EXISTS for a core whose config declares `kbMouseSupport: true` — the worker gates
// both it and "keyboard" on `r.App().KbMouseSupport()`. Stock sets it on dosbox_pure alone, so the first
// version of this code shipped completely dead: pc.ondatachannel never fired for "mouse", every delta
// and click went nowhere, and because the same change flipped scummvm_pointer_device off "pointer" it
// also removed the POINTER path that had at least made clicks work. Keep config.worker-gl.yaml's
// kbMouseSupport in step with MOUSE_SYSTEMS below or mouse support silently disappears again.
// Format matches
// nanoarch.go's InputMouse exactly: a 1-byte type tag, then type-specific payload, BIG-ENDIAN (this
// channel does NOT follow the pad/pointer channel's little-endian convention).
//   Move:   [0x00][dx:i16 BE][dy:i16 BE]
//   Button: [0x01][mask:u8]  (bit0=left, bit1=right, bit2=middle — only left is used today)
const MouseMoveTag = 0x00;
const MouseButtonTag = 0x01;
export function encodeMouseMove(dx, dy) {
  const buf = new ArrayBuffer(5);
  const dv = new DataView(buf);
  dv.setUint8(0, MouseMoveTag);
  dv.setInt16(1, dx, false);
  dv.setInt16(3, dy, false);
  return buf;
}
export function encodeMouseButtons(mask) {
  const buf = new ArrayBuffer(2);
  const dv = new DataView(buf);
  dv.setUint8(0, MouseButtonTag);
  dv.setUint8(1, mask);
  return buf;
}

// Gamepad float (-1..1) → int16, with a small deadzone so idle stick drift doesn't spam frames.
function axisToInt16(v) {
  if (!v || Math.abs(v) < 0.08) return 0;
  return Math.trunc(Math.max(-1, Math.min(1, v)) * 32767);
}

function packet(t, p, id) {
  return JSON.stringify(id !== undefined ? { id, t, p } : { t, p });
}

/**
 * Open a CloudRetro session for a room.
 *
 * `descriptor.spectator` (playerSlot -1) opens a WATCH-ONLY session: video and audio arrive exactly as
 * they do for a player, but the input pump never starts and t=108 is never sent. That is what makes a
 * spectator harmless. On the worker, `user.Index` is only ever read inside the DataChannel's
 * `OnMessage` handler (`r.App().Input(user.Index, …)` — coordinatorhandlers.go), so a connection that
 * sends no input frames cannot reach the emulator at all, whatever index it holds.
 *
 * @param descriptor { wsUrl, gameKey, playerSlot, spectator, iceConfig, isCreator, roomCode, system }
 * @param opts { videoEl, onRoomId(cloudRetroRoomId), onStatus(str), onError(err), onSeat(index),
 *               onAspect(ratio) — the core's own display aspect (see reportAspect) }
 * @returns { close, save, load, reset }
 */
/**
 * The display aspect ratio a room should render at, from CloudRetro's `av` payload, or null when the
 * core doesn't specify one (libretro: geometry.aspect_ratio <= 0 means "derive from base w/h").
 * Exported for unit tests; see the reportAspect() comment for why `a` is used verbatim.
 */
export function displayAspect(av) {
  if (!av) return null;
  const a = Number(av.a);
  if (!isFinite(a) || a <= 0.2 || a > 4) return null;
  return a;
}

/**
 * CSS width/height for the <video> inside an aspect box of ratio `ar`, given the core's rotation.
 *
 * A quarter-turn swaps the element's axes, so a rotated video must be as wide as the box is TALL and as
 * tall as the box is WIDE, or it overflows (observed on 1942, rot=90: upright but spilling out of its 3:4
 * box with dead space below). Pairs with the `translate(-50%,-50%) rotate(...)` transform applied above,
 * which rotates about the box centre.
 *
 *   width  = boxH = boxW / ar   -> calc(100% / ar)   (100% width  resolves against boxW)
 *   height = boxW = boxH * ar   -> calc(100% * ar)   (100% height resolves against boxH)
 */
export function rotatedVideoSize(ar, rot) {
  const turned = ((Number(rot) || 0) % 180) !== 0;
  if (!turned || !(ar > 0)) return { width: "100%", height: "100%" };
  return { width: `calc(100% / ${ar})`, height: `calc(100% * ${ar})` };
}

/**
 * CSS transform for the <video>, which is absolutely centred inside its aspect box.
 *
 * The leading translate is NOT optional and NOT cosmetic: the element is positioned at top/left 50%, so
 * without pulling it back by half its own size the picture renders in the bottom-right quadrant. It must
 * therefore be produced even when the core reports no geometry at all (rot 0, flip false) — 21 of our 29
 * cores never send an `av` payload, because CloudRetro only emits one for cores with `coreAspectRatio`.
 *
 * GL cores render bottom-left-origin (flip → scaleY(-1)); vertical arcade cabs report rot=90
 * (→ rotate(-90deg), about the centre, paired with rotatedVideoSize's swapped axes).
 */
export function videoTransform(rot, flip) {
  const r = (Number(rot) || 0) % 360;
  return ["translate(-50%, -50%)", r ? `rotate(${-r}deg)` : "", flip ? "scaleY(-1)" : ""]
    .filter(Boolean)
    .join(" ");
}

export function createCloudRetroSession(descriptor, opts) {
  const { videoEl, onRoomId, onStatus, onError, onSeat, onAspect, onChordAction, onAchievement, onTtff, customGamepadProfile: customGamepadProfileOverride } = opts || {};
  const status = (s) => onStatus && onStatus(s);
  // Watch-only seat. Trust the explicit flag, but fall back to the slot itself so an older descriptor
  // (or a hand-built one in a test) can't accidentally hand a watcher a controller.
  const spectator = descriptor.spectator === true || (descriptor.playerSlot | 0) < 0;
  // Local multiplayer: an extra INPUT-ONLY session pinned to one physical pad. It joins the room like
  // any second player (own WS + PeerConnection + DataChannel, own seat via t=108) but renders nothing —
  // the primary session already plays the room's one shared stream. No keyboard (the primary owns it),
  // no pad adoption (exactly pads[padIndex]), no aux audio PC (nothing to hear).
  // The pin is RUNTIME-REASSIGNABLE (setPad) — the room's Controllers panel moves pads between seats.
  // `inputOnly` is the creation-time ROLE (never keyboard/media); the pin is just the current pad.
  let pinnedPad = Number.isInteger(opts && opts.padIndex) ? opts.padIndex : -1;
  const inputOnly = pinnedPad >= 0;
  if (pinnedPad >= 0) claimedPadIndexes.add(pinnedPad);
  // The MODEL of the pinned pad, learned the first time we read it. Indexes are not stable across a
  // disconnect/reconnect but the id is, so this is what lets the pin follow the same controller to
  // its new slot (readGamepad) instead of the seat silently going dead.
  let pinnedPadId = null;

  // Chord/hold-to-fire bindings (quick-save/quick-load/reset — see controllerChords.js). Only
  // built when a caller passes onChordAction (the primary session; local-player extra sessions
  // don't). No spectator guard needed here: pumpInput's own timer never starts for a spectator
  // (see stopInput/startInput below), so this can never be polled for one.
  // Chord watcher uses the user's custom binds merged over the defaults. `let` so reloadChords() can
  // rebuild it live when a bind changes in the controller tool (no room restart).
  let chordWatcher = onChordAction ? createChordWatcher(onChordAction, resolveChords(customChordBinds)) : null;

  let ws = null;
  let pc = null;
  let dc = null;
  let discDc = null; // patch 0005: worker-created "disc" channel; the browser sends a target disc index
  let mouseDc = null; // stock CloudRetro's worker-created "mouse" channel (RETRO_DEVICE_MOUSE relative deltas)
  let inputTimer = null;
  let closed = false;
  let gameStartSent = false; // t=104 goes out exactly once: from dc.onopen, or the slow fallback below
  const inboundStream = new MediaStream(); // audio + video tracks accumulate here (see ontrack)
  let lastAv = null; // last known video geometry (flip/rotation), re-applied whenever the track attaches

  // ── Time-to-first-frame marks (arcade perf program P1, 2026-09-05) ────────────────────────────
  // Every hop of the room's start path, in ms since connect(): ws-open → init (t=4 handled) → dc-open
  // (ICE + DTLS + SCTP up; this is what gates t=104) → game-start (t=104 answered: ROM staged, core
  // loaded, emulator running) → first-frame (the first frame the <video> presented, via rVFC). The GAPS
  // are the start path: dc-open−init is transport, game-start−dc-open is JIT extraction + core load +
  // boot on the worker, first-frame−game-start is the first keyframe reaching the decoder. Until this
  // existed nothing in the stack measured how long "Connecting…" really took. One `[ttff]` console line
  // per session; onTtff() hands it to the room page, which carries it on its first heartbeat so the
  // ArcadeSession row keeps it. Observability only — nothing adapts to these numbers.
  const ttff = { t0: 0, marks: {} };
  const nowMs = () => (typeof performance !== "undefined" && performance.now ? performance.now() : Date.now());
  function ttffMark(name) {
    if (!ttff.t0 || ttff.marks[name] != null) return;
    ttff.marks[name] = Math.round(nowMs() - ttff.t0);
    if (name !== "first-frame") return;
    const m = ttff.marks;
    const v = (k) => (m[k] == null ? "-" : m[k]);
    try {
      console.log(`[ttff] total ${v("first-frame")}ms — ws-open ${v("ws-open")} init ${v("init")} offer ${v("offer")} ` +
        `ice-checking ${v("ice-checking")} ice-connected ${v("ice-connected")} pc-connected ${v("pc-connected")} ` +
        `dc-open ${v("dc-open")} gather-done ${v("gather-done")} game-start ${v("game-start")} first-frame ${v("first-frame")} (${descriptor.system || "?"})`);
      onTtff && onTtff({ totalMs: m["first-frame"], marks: { ...m } });
    } catch { /* an observer's error must never touch the session */ }
  }
  // STUN entries carry only urls; a TURN entry also carries the ephemeral username/credential the site
  // minted for this join. Passing them through is what enables the last-resort relay path for clients
  // that can't reach a worker directly (guest/isolated SSID, hostile remote network). Mirrors the INIT
  // (t=4) ICE-replacement path below, which already keeps username/credential.
  let iceServers = (descriptor.iceConfig || []).map((s) => ({ urls: s.urls, username: s.username, credential: s.credential }));

  // ── Audio de-contention knobs (docs/arcade-audio-nextsteps.md) ──────────────────────────────────
  // The residual audio hitch: on the bundled transport a burst of video RTP head-of-line-blocks the
  // tiny opus packets, so audio arrives late/bursty and Chrome's NetEq over-buffers toward ~260ms and
  // TIME-STRETCHES (~8%) — the warble the user hears. Two levers, both tunable via localStorage so a
  // real browser (the only place smoothness can be judged) can A/B them live:
  //  • arcade.audioJitterMs (default 80; 150 on psp/gc — see AUDIO_JITTER_BY_SYSTEM): give NetEq a small STABLE audio target so it stops adaptively
  //    inflating + stretching. Video stays at 0 (Pion uses separate stream ids, so audio delay never
  //    drags video). ~80ms audio latency is imperceptible for game SFX. Set 0 to restore old behavior.
  //  • arcade.audioPC (default ON; opt out with "0"): give audio its OWN PeerConnection so video bursts can't
  //    head-of-line-block it. NOT SDP un-bundling — that is unimplementable against this worker:
  //    pion/webrtc hardcodes ONE ICE/DTLS transport per PeerConnection (its BundlePolicy config is
  //    stored but never read), so a max-compat browser's extra transports have no peer and DTLS never
  //    completes → the 2026-07-08 "Negotiating" hang, unfixable by any port layout (multiport tested +
  //    refuted). A SECOND PC is the shape Pion does support (it's just another peer on the mux, and the
  //    browser gives it its own local port → distinct 5-tuple). Worker half: patch 0020 — we ask via
  //    init sdp:"audio-pc"; the worker then offers video+data on the main PC and opus on an aux PC,
  //    tunneling the aux offer/ICE through the ice signal field as "aux-sdp:"/"aux-ice:" envelopes
  //    (coordinator relays those strings verbatim). Old worker ignores the ask → audio arrives on the
  //    main PC as always, so the flag is safe against any worker build. Verified on prod 2026-07-08:
  //    2 PCs both connected, video-only on main / opus-only on aux, Playing in 4s. Escape hatch if a
  //    room ever has video but NO audio: localStorage.setItem("arcade.audioPC","0") + reload.
  // Audio jitter buffer depth — PER SYSTEM, because the stall it absorbs is per system.
  //
  // An emulator is single-threaded and does its file IO inline, so a load — PPSSPP reading its savedata
  // when the player walks into a sign, a core's first-use lazy asset load — stalls retro_run for tens of
  // milliseconds and produces NO AUDIO for that whole span. Measured on Loco Roco: stalls of 90-110 ms
  // against a buffer that actually held ~105 ms. That is a knife-edge, so it failed occasionally and the
  // player heard a faint crackle at area transitions. The receiver stats say exactly this and nothing
  // else: concealedSamples > 0 with packetsLost == 0 — the decoder invented audio because none arrived.
  // It was never loss, never FEC, never the encoder.
  //
  // ONLY psp. The depth is sized to PPSSPP's MEASURED stall ceiling, and to nothing else. From 759
  // PSP-only pace-diag samples (2026-07-14): median 10.6 ms, p90 26.7, p99 97.9, WORST 147.5 — and zero
  // samples above 150. Two stall sources, both confirmed in the worker log rather than assumed:
  //   * the savedata dialog — AES EncryptData + the inline write — 87-147 ms, on every save/load. This
  //     is what sets the ceiling.
  //   * a lazy VFPU table load (PPSSPP's InitVFPU has an eager "load all in advance" preload that is
  //     #if 0'd out, so vfpu_asin_lut65536 & friends read from disk on FIRST USE, inside retro_run) —
  //     92-115 ms, once per session. Enabling that preload would remove these, but it needs a custom
  //     PPSSPP build and would NOT lower the ceiling, since savedata is the taller stall. Not worth it.
  // Absorbing a BOUNDED stall with a buffer is the correct answer; 150 covers 147.5 with margin. What was
  // wrong before was the SCOPE, not the number: this was global, so every core paid for PPSSPP.
  //
  // gc is deliberately NOT here. Dolphin's long stalls are shader compilation and a cold boot — they
  // happen at loads, gc has never actually skipped, and a session-long latency tax to cover a loading
  // screen is a bad trade. The 2D cores, PS1 and N64 don't stall at all.
  //
  // The cost being avoided is not input latency — video's buffer stays 0 (~6 ms) and audio rides its OWN
  // PeerConnection (the aux audio PC above), so no RTCP lip-sync drags the picture — it is AUDIO latency:
  // at 150 the sound sits ~175 ms behind the video, past the ~125 ms where a lagging soundtrack becomes
  // detectable. That lands hardest on the one genre that cannot absorb it, because a rhythm player reacts
  // to what they HEAR: every extra ms of audio delay puts their input that much later against the beat.
  // Patapon, Gitaroo Man and Beaterator are all PSP — the very system that needs the buffer. They pay it
  // because they must; nobody else does.
  //
  // The only fix that would DELETE this buffer is making PPSSPP's savedata encrypt+write asynchronous —
  // on the one system whose memory card is the sole progress (noSaveStates) and has no save-state to fall
  // back on. Risking save corruption to reclaim 70 ms of audio delay is not a trade worth making.
  //
  // Tunable live, no deploy: localStorage.setItem("arcade.audioJitterMs", "220") + reload (overrides the
  // per-system value). Raise it if a system turns out to stall harder than this; lower it if the audio
  // ever feels detached from the action.
  const AUDIO_JITTER_BY_SYSTEM = { psp: 150 };
  // BROWSER-OFFER MODE (perf program P13b, 2026-09-05) — A/B flag, default OFF. Today the WORKER offers
  // (INIT_WEBRTC initiator:false), Pion writes a=setup:actpass and Chrome answers active: Chrome is the DTLS
  // client and sends its ClientHello the instant ICE connects — before Pion's DTLS transport listens — so
  // the flight is lost and Chrome waits out the RFC 6347 1 s RTO. Measured 1041 / 1057 ms between
  // ice-connected and pc-connected on every same-host room, unchanged by Pion's own retransmit interval.
  // With this on, THIS browser offers (recvonly transceivers), Pion answers as DTLS client (worker config
  // webrtc.dtlsRole: 2) and sends its ClientHello only once it is ready. localStorage arcade.browserOffers=1.
  let BROWSER_OFFERS = false;
  try { BROWSER_OFFERS = localStorage.getItem("arcade.browserOffers") === "1"; } catch { /* storage unavailable */ }
  const AUDIO_JITTER_DEFAULT_MS = 80;
  const AUDIO_JITTER_MS = (() => {
    try {
      const v = parseInt(localStorage.getItem("arcade.audioJitterMs"), 10);
      if (Number.isFinite(v) && v >= 0) return v;
    } catch { /* localStorage unavailable — fall through to the per-system default */ }
    const sys = String(descriptor.system || "").toLowerCase();
    return AUDIO_JITTER_BY_SYSTEM[sys] ?? AUDIO_JITTER_DEFAULT_MS;
  })();
  const AUDIO_PC = (() => { try { return localStorage.getItem("arcade.audioPC") !== "0"; } catch { return true; } })();

  // Input profile for this game's system (button layout + keyboard map + optional right-stick keys).
  // effectiveInputSystem substitutes "gc" for the two GC_ON_WII_GAME_KEYS ROMs (unless the room
  // picked "wiimote") — must match the worker's actual device choice or this sends bits the core
  // no longer reads.
  const inputSystem = effectiveInputSystem(descriptor.system, descriptor.gameKey, strFromWsUrl(descriptor.wsUrl, "ctrlscheme"));
  const profile = profileFor(inputSystem);
  let keymap = { ...profile.keymap };
  let rstickKeys = profile.rstick ? { ...profile.rstick } : undefined; // key→right-stick direction, or undefined
  let gamepad = { ...profile.gamepad };
  // Whether the analog left stick also presses the d-pad bits (see the stickFoldFor comment).
  // Resolved from the profile default + any saved user override; RUNTIME-REASSIGNABLE via setStickFold
  // (the mapping panel toggles it live, so a fix takes effect without leaving the room).
  let foldStickToDpad = stickFoldFor(inputSystem);
  // Mirror the right stick's X axis (per-game, see getRightStickSwapX). Also runtime-reassignable —
  // the whole point is flipping it mid-room when a game's camera turns out to be mirrored.
  let swapRightStickX = getRightStickSwapX(descriptor.gameKey, descriptor.system);

  // Apply custom gamepad button rebindings if provided
  // customGamepadProfileOverride maps: physicalButtonIndex -> RetroPadBit (user rebindings)
  if (customGamepadProfileOverride && Object.keys(customGamepadProfileOverride).length > 0) {
    for (const [buttonIndexStr, newBit] of Object.entries(customGamepadProfileOverride)) {
      const buttonIndex = parseInt(buttonIndexStr, 10);
      if (Number.isFinite(buttonIndex)) {
        gamepad[buttonIndex] = newBit;
      }
    }
  }

  // Live input state.
  const keyMask = { value: 0 };
  // Left-stick direction held via the ARROW keys. Always drives the left stick; folds into the d-pad
  // only when keyboardArrowsDriveDpad says so (see pumpInput).
  const lKeys = { up: false, down: false, left: false, right: false };
  // Right-stick direction held via the keyboard (N64 C-buttons), when the profile maps them.
  const rKeys = { up: false, down: false, left: false, right: false };
  // Keyboard edges go out on the EVENT, not on the next poll tick (perf program P2, 2026-09-05): the poll
  // used to be the only sender, so a keypress waited up to a full 16 ms interval before it left the browser
  // — on top of the worker's own once-per-tick sampling. onKeyState mutates the live state exactly as
  // before; onKey wraps it with an immediate pump, which dedupes (an unmapped key sends nothing) and is
  // skipped during tape replay (the recorded run must not be contaminated by a stray key).
  const onKey = (down) => {
    const apply = onKeyState(down);
    return (e) => { apply(e); if (!replaySource) pumpInput(); };
  };
  const onKeyState = (down) => (e) => {
    const bit = keymap[e.code];
    if (bit !== undefined) {
      e.preventDefault();
      if (down) keyMask.value |= (1 << bit);
      else keyMask.value &= ~(1 << bit);
      return;
    }
    const ldir = ARROW_DIR[e.code];
    if (ldir) { e.preventDefault(); lKeys[ldir] = down; return; }
    if (rstickKeys) {
      for (const dir of ["up", "down", "left", "right"]) {
        if (rstickKeys[dir] === e.code) { e.preventDefault(); rKeys[dir] = down; return; }
      }
    }
  };
  const keyDown = onKey(true);
  const keyUp = onKey(false);

  // Pick which local pad drives our seat. Blind "first non-null" broke for Bluetooth pads
  // (DualSense): when the pad idle-sleeps and reconnects, Chrome can leave a PHANTOM entry at
  // index 0 (or re-register the pad at a new index) — the shim then polls the corpse and "the
  // controller stopped working". Instead: stick with the pad the player last used; adopt any pad
  // showing real activity (pressed button / deflected stick); only then fall back to first
  // non-null. A phantom is never active, so a woken pad wins with its first button press.
  let activePadIndex = -1;
  // When the adopted pad last showed real input. The primary session latches the first connected pad
  // even for a keyboard-only player, so "which pad is the primary using" must mean RECENTLY used —
  // otherwise adding a local player on a keyboard host's only spare pad is impossible (the idle
  // latched pad would be excluded from the press-a-button detector).
  let lastPadActiveAt = 0;
  // While the room page listens for a NEW controller's button press, the primary must not adopt it:
  // this 16 ms poll otherwise steals the pad the instant it's pressed (beating the 125 ms detector),
  // reports it as "the primary's pad", and the detector excludes the very pad being pressed —
  // observed live 2026-07-10: quick-add could never complete. Held = keep the current pad only.
  let adoptionHeld = false;
  const padActive = (gp) =>
    gp.buttons.some((b) => b.pressed) || gp.axes.some((a) => Math.abs(a) > 0.2);

  function readGamepad() {
    const pads = navigator.getGamepads ? navigator.getGamepads() : [];
    notePads(pads); // 60 Hz: the freshest liveness sampling anything here gets (see PAD_STALE_MS)
    const mask0 = { mask: 0, axes: [0, 0, 0, 0] };
    let gp;
    if (pinnedPad >= 0) {
      // Pinned to one physical pad — never adopt another. If Chrome re-enumerates the pad away
      // (Bluetooth sleep), send neutral until it returns.
      gp = pads[pinnedPad] || null;
      if (gp && !isPhantomPad(gp)) {
        pinnedPadId = gp.id || pinnedPadId; // remembered so the pin can FOLLOW this controller
      } else if (pinnedPadId) {
        // The pin points at nothing, or at a corpse. Reconnecting a controller is how a player fixes
        // a misbehaving pad, and it usually comes back at a NEW index — so follow it there instead of
        // leaving this seat dead until someone re-assigns it by hand. Only when the answer is
        // unambiguous: exactly one live, unclaimed entry of the same model.
        const twins = Array.prototype.filter.call(pads, (p) =>
          p && p.index !== pinnedPad && p.id === pinnedPadId
          && !isPhantomPad(p) && !claimedPadIndexes.has(p.index));
        if (twins.length === 1) {
          claimedPadIndexes.delete(pinnedPad);
          pinnedPad = twins[0].index;
          claimedPadIndexes.add(pinnedPad);
          gp = twins[0];
        } else {
          gp = null;
        }
      }
      // A corpse we can't replace must still not be READ: one frozen mid-press would hold that
      // button on this seat for the rest of the room.
      if (gp && isPhantomPad(gp)) gp = null;
    } else if (inputOnly) {
      // A local seat whose pad was reassigned away: neutral until the panel gives it another.
      gp = null;
    } else {
      // Never adopt a pad a local-player session has claimed — without this the primary and the
      // extra seat would both forward the same physical controller. Streamed (ViGEm) pads are
      // likewise never auto-adopted when the guard is on; if the guard is flipped on mid-game while
      // one is latched, drop it so the primary re-adopts a real pad (or goes keyboard-only).
      const free = (p) => p && !claimedPadIndexes.has(p.index) && !isStreamedPad(p) && !isPhantomPad(p);
      gp = activePadIndex >= 0 && !claimedPadIndexes.has(activePadIndex) ? pads[activePadIndex] : null;
      if (gp && isStreamedPad(gp)) gp = null;
      // Drop a latched corpse too — otherwise a pad unplugged mid-press stays "active" forever, which
      // both holds that button down and (being active) stops adoption from ever moving on.
      if (gp && isPhantomPad(gp)) gp = null;
      // Never re-adopt while our own output could still be bouncing back off a virtual pad on this
      // machine (see the echo guard above) — that "active" pad may be us. Keep whatever we hold.
      // A negative age means the stamp is in the future (system clock moved back, or a stamp left by
      // a session that outlived a clock change): un-trustable, so treat it as no risk rather than
      // wedging adoption until the clock catches up.
      const outputAge = Date.now() - lastNonNeutralOutputAt;
      const echoRisk = outputAge >= 0 && outputAge < ECHO_WINDOW_MS;
      if (!adoptionHeld && !echoRisk
          && (!gp || (!padActive(gp) && Array.prototype.some.call(pads, (p) => free(p) && p !== gp && padActive(p))))) {
        gp = Array.prototype.find.call(pads, (p) => free(p) && padActive(p)) || gp
          || Array.prototype.find.call(pads, (p) => free(p)) || null;
      }
    }
    if (!gp) return mask0;
    activePadIndex = gp.index;
    if (!inputOnly && padActive(gp)) lastPadActiveAt = Date.now();

    let mask = 0;
    const axes = [0, 0, 0, 0];
    // Face-button relabel (see faceSwap above): swap south↔east and west↔north when this SPECIFIC
    // pad's convention doesn't match the profile's positional assumption. Computed per poll, per
    // pad (not a machine-wide flag) so mixed-brand local multiplayer gets each pad right, and
    // toggling the panel's override takes effect mid-game.
    const swap = effectiveFaceSwap(gp);
    gp.buttons.forEach((b, i) => {
      const pi = swap && i < 4 ? (i ^ 1) : i;
      if (b.pressed && gamepad[pi] !== undefined) mask |= (1 << gamepad[pi]);
    });
    // Real analog axes ALWAYS ride the frame (N64/GC/PS steering wants them). The left-stick→d-pad
    // FOLD, on the other hand, is only correct for pure-dpad 2D cores, where an analog-only pad has
    // no other way to steer and stick==d-pad anyway. On an analog-native console it double-binds two
    // DISTINCT inputs (Goldeneye pans up as you walk, GC/Wii Smash taunts) — so it's gated per system
    // by foldStickToDpad (profile default + user override, toggleable live via setStickFold).
    for (let i = 0; i < 4 && i < gp.axes.length; i++) axes[i] = axisToInt16(gp.axes[i]);
    if (foldStickToDpad) {
      const [ax, ay] = gp.axes;
      if (ax < -0.5) mask |= (1 << PAD.LEFT); else if (ax > 0.5) mask |= (1 << PAD.RIGHT);
      if (ay < -0.5) mask |= (1 << PAD.UP); else if (ay > 0.5) mask |= (1 << PAD.DOWN);
    }
    return { mask, axes };
  }

  // Send on change only, like the stock client (dirty flag over the whole 5-int16 frame) — plus a
  // slow KEEPALIVE resend of the current frame (RESYNC_MS), because "on change only" over an
  // unreliable transport is a one-way trip: the joypad channel is maxRetransmits:0 / unordered, so a
  // dropped frame is simply gone, and the worker keeps whatever state it last heard until the NEXT
  // change. Lose a RELEASE and the button is held in-game forever — nothing else resends it (blur/
  // focus force a resync, but only if the player happens to leave the tab). Resending the current
  // state once a second bounds any such desync to ~1s at ~10 bytes/s, and is safe out of order
  // precisely because the frame is absolute state, not an edge.
  // ── Input tape: record this seat's wire frames, or replay a recorded one ────────────────────────
  // See inputTape.js for the format and for what a replay can and cannot promise. Everything here is
  // inert until someone arms it: `tape` and `replaySource` are null on every ordinary session, and
  // the only cost a non-recording room pays is the two null checks in pumpInput.
  let tape = null; // createInputTape() while recording
  let tapeTraceOn = false; // also capture per-frame video thumbprints
  let replaySource = null; // (clockMs) => [mask, ax, ay, rx, ry]
  let replayT0Media = null; // media-clock origin, latched on the first frame after arming
  let replayT0Wall = 0;
  let replayUsesWallClock = false;
  let videoClockRunning = false;
  let lastMediaTime = null; // seconds, from requestVideoFrameCallback (the STREAM's own clock)
  let lastPresentedFrames = null;
  let thumbCanvas = null;
  let thumbCtx = null;

  // Browsers without requestVideoFrameCallback get a coarser media clock off currentTime, sampled
  // whenever the clock is read. Same timeline, ~1 tick of resolution instead of per-frame; a tape
  // recorded there still replays, its trace is simply absent.
  function sampleClockFallback() {
    if (videoClockRunning && videoEl && !videoEl.requestVideoFrameCallback && Number.isFinite(videoEl.currentTime)) {
      lastMediaTime = videoEl.currentTime;
    }
  }

  // A stamp for the recorder: where we are on both clocks right now.
  const clockStamp = () => {
    sampleClockFallback();
    return { mediaTime: lastMediaTime, presentedFrames: lastPresentedFrames, now: Date.now() };
  };

  // The replay clock, in ms since the replay was armed. Media-clock by default: it advances with the
  // encoder (hence with the emulator), so a stalled tab or a network hiccup on either run shifts the
  // replay with the game instead of desynchronising it. Falls back to wall clock when the media
  // clock isn't moving yet (pre-attach) or when the caller explicitly asked for wall.
  function replayClockMs() {
    sampleClockFallback();
    if (!replayUsesWallClock && lastMediaTime != null) {
      if (replayT0Media == null) replayT0Media = lastMediaTime;
      return (lastMediaTime - replayT0Media) * 1000;
    }
    return Date.now() - replayT0Wall;
  }

  // One rVFC loop serves both halves: it maintains the media clock (needed by record AND replay) and,
  // when a trace is armed, writes one downscaled greyscale thumbprint per PRESENTED frame into the
  // tape. Runs only while something needs it.
  function videoClockStep(now, meta) {
    if (!videoClockRunning) return;
    lastMediaTime = meta && Number.isFinite(meta.mediaTime) ? meta.mediaTime : lastMediaTime;
    lastPresentedFrames = meta ? meta.presentedFrames : lastPresentedFrames;
    if (tape && tapeTraceOn && thumbCtx) {
      try {
        thumbCtx.drawImage(videoEl, 0, 0, THUMB_W, THUMB_H);
        const d = thumbCtx.getImageData(0, 0, THUMB_W, THUMB_H).data;
        const g = new Uint8Array(THUMB_W * THUMB_H);
        for (let i = 0, p = 0; p < g.length; i += 4, p++) {
          g[p] = (d[i] * 77 + d[i + 1] * 150 + d[i + 2] * 29) >> 8;
        }
        tape.frame(lastMediaTime, lastPresentedFrames, g);
      } catch { /* a tainted or not-yet-sized frame; skip it, keep the clock */ }
    }
    if (videoEl && videoEl.requestVideoFrameCallback) videoEl.requestVideoFrameCallback(videoClockStep);
  }

  function startVideoClock(withTrace) {
    tapeTraceOn = tapeTraceOn || !!withTrace;
    if (videoClockRunning) return;
    if (!videoEl) return;
    videoClockRunning = true;
    if (tapeTraceOn && !thumbCanvas && typeof document !== "undefined") {
      thumbCanvas = document.createElement("canvas");
      thumbCanvas.width = THUMB_W;
      thumbCanvas.height = THUMB_H;
      thumbCtx = thumbCanvas.getContext("2d", { willReadFrequently: true });
    }
    // Prime the clock from currentTime before the first rVFC callback lands. Without this the first
    // input frame after arming is stamped with a null media time and a replay drops it — measured on
    // the first end-to-end run, which reported "1 row had no media stamp".
    if (Number.isFinite(videoEl.currentTime)) lastMediaTime = videoEl.currentTime;
    if (videoEl.requestVideoFrameCallback) videoEl.requestVideoFrameCallback(videoClockStep);
  }

  function stopVideoClock() {
    if (tape || replaySource) return; // the other half still needs it
    videoClockRunning = false;
    tapeTraceOn = false;
  }

  // Keepalive resend of an UNCHANGED frame. The joypad channel is unreliable (maxRetransmits:0), so a
  // lost packet is simply gone — and a lost RELEASE leaves the worker's virtual pad pressed until the next
  // frame we send. While anything is held that costs at most RESYNC_HELD_MS (perf program P2, 2026-09-05:
  // was 1000 — a lost release used to hold a button for a whole second); at neutral the slow beat is
  // enough, nothing is stuck there. ~7 ten-byte frames a second while holding is noise on the wire.
  const RESYNC_HELD_MS = 150;
  const RESYNC_NEUTRAL_MS = 1000;
  let last = null;
  let lastSentAt = 0;
  // Read the physical controls and produce the frame this seat WOULD send. Split out of pumpInput so
  // tape replay can substitute a recorded frame at exactly this point — everything downstream (the
  // chord strip is upstream, the dedupe, the keepalive, the echo-guard stamp, the recorder) then
  // behaves identically for a replayed frame and a played one. A replay that re-synthesised key
  // events instead would re-run all of this logic and could drift from what was recorded.
  function readLiveInput() {
    const gp = readGamepad();
    let mask = keyMask.value | gp.mask;
    // Keyboard arrows fold into the d-pad only where that's correct for the system (see
    // keyboardArrowsDriveDpad): on for 2D + d-pad-movement consoles, OFF for n64/gc/wii so an arrow
    // key doesn't press the d-pad AND deflect the stick (the keyboard twin of the Goldeneye/Smash
    // double-bind). The gamepad's own d-pad (buttons 12-15) is unaffected — it rode in via gp.mask.
    if (keyboardArrowsDriveDpad(inputSystem, foldStickToDpad)) {
      if (lKeys.up) mask |= (1 << PAD.UP);
      if (lKeys.down) mask |= (1 << PAD.DOWN);
      if (lKeys.left) mask |= (1 << PAD.LEFT);
      if (lKeys.right) mask |= (1 << PAD.RIGHT);
    }
    // Chord watch must run on EVERY tick a chord's bits are held, not just the first — placed here,
    // before the send-on-change dedupe below returns early once the mask stops changing (a steadily
    // held chord produces an unchanged mask after tick 1, and the dedupe would otherwise starve it
    // of the ticks it needs to reach its hold duration). The poll's return is the mask of currently
    // ENGAGED chords — stripped from the frame so a held fast-forward/rewind (or a fired quick-save)
    // stops pressing its buttons in the game (Select alone opens a menu in half the SNES library).
    // The dedupe below compares the STRIPPED mask, so engage/release edges still resend correctly.
    if (chordWatcher) mask &= ~chordWatcher.poll(mask, Date.now());
    // Keyboard arrows also drive the LEFT ANALOG STICK. N64 (and most 3D) games steer with the stick,
    // NOT the d-pad — so without this the keyboard couldn't turn. Full deflection from the arrow keys;
    // a real gamepad stick takes precedence when it's being pushed.
    let ax = gp.axes[0], ay = gp.axes[1];
    if (!ax) ax = lKeys.left ? -32767 : lKeys.right ? 32767 : 0;
    if (!ay) ay = lKeys.up ? -32767 : lKeys.down ? 32767 : 0;
    // Right stick from the keyboard when the profile maps it (N64 C-buttons); the pad stick wins.
    let rx = gp.axes[2], ry = gp.axes[3];
    if (rstickKeys) {
      if (!rx) rx = rKeys.left ? -32767 : rKeys.right ? 32767 : 0;
      if (!ry) ry = rKeys.up ? -32767 : rKeys.down ? 32767 : 0;
    }
    // Mirror right-stick left/right last, so it covers the keyboard's synthetic deflection too — the
    // player's "left" must mean the same thing on both inputs. Safe to negate: axisToInt16 clamps to
    // ±32767, never int16's -32768.
    if (swapRightStickX) rx = -rx;
    return { mask, a: [ax, ay, rx, ry] };
  }

  function pumpInput() {
    if (closed || !dc || dc.readyState !== "open") return;
    // Tape replay drives this seat instead of the hardware. The chord watcher and every keyboard/pad
    // read are skipped entirely — a stray key or a controller left on the desk must not contaminate
    // a run whose whole point is that it is the recorded run.
    let mask, a;
    if (replaySource) {
      const f = replaySource(replayClockMs());
      mask = f[0] | 0;
      a = [f[1] | 0, f[2] | 0, f[3] | 0, f[4] | 0];
    } else {
      const live = readLiveInput();
      mask = live.mask;
      a = live.a;
    }
    // Echo-guard bookkeeping (see ECHO_WINDOW_MS): stamp every tick our output is non-neutral, NOT
    // every frame we send — a HELD button stops producing sends the moment the dedupe below latches,
    // while the worker's virtual pad stays pressed the whole time. Stamping on send would let the
    // window expire mid-hold, which is exactly when the release makes the echo look like a live pad.
    const now = Date.now();
    if (mask !== 0 || a[0] !== 0 || a[1] !== 0 || a[2] !== 0 || a[3] !== 0) lastNonNeutralOutputAt = now;
    const neutral = mask === 0 && a[0] === 0 && a[1] === 0 && a[2] === 0 && a[3] === 0;
    const changed = !(last && last[0] === mask && last[1] === a[0] && last[2] === a[1] && last[3] === a[2] && last[4] === a[3]);
    if (!changed && now - lastSentAt < (neutral ? RESYNC_NEUTRAL_MS : RESYNC_HELD_MS))
      return;
    last = [mask, a[0], a[1], a[2], a[3]];
    lastSentAt = now;
    // Record the frame that is actually going on the wire, at the moment it goes. Deliberately after
    // the dedupe: a tape of every 16 ms tick would be 60x larger and say nothing extra, because the
    // frame is ABSOLUTE STATE — the sent frames are exactly the changes, and a replay that reasserts
    // each one at its recorded time reproduces the whole timeline. (The 1 s keepalive resends ride
    // along harmlessly: replaying a redundant identical frame is a no-op the dedupe eats again.)
    // Only CHANGES go into the tape: with the 150 ms held resync a keepalive is ~7 identical frames/s, and a
    // tape that recorded them would diff against older tapes on nothing but the resync cadence.
    if (tape && changed) tape.input(mask, a, clockStamp());
    try { dc.send(encodeInput(mask, a)); } catch { /* channel closing */ }
  }

  // Focus-transition hygiene. A key held when focus leaves never gets its keyup (Alt-Tab delivers it
  // to the OS switcher), so its bit would ride every future frame as a button the worker believes is
  // held forever. On blur: zero the keyboard state and null the `last` dedupe, so the next pump SENDS
  // the release. On focus return: null the dedupe again (the first poll then re-sends true state even
  // if "unchanged" — self-heals any worker-side desync accrued while away) and forget the remembered
  // gamepad index so a pad Chrome re-enumerated while unfocused is re-adopted on its first input.
  const onWindowBlur = () => {
    keyMask.value = 0;
    lKeys.up = lKeys.down = lKeys.left = lKeys.right = false;
    rKeys.up = rKeys.down = rKeys.left = rKeys.right = false;
    last = null;
  };
  const onWindowFocus = () => {
    last = null;
    activePadIndex = -1;
  };

  function startInput() {
    // A spectator holds no controller port: no key/gamepad listeners, no poll, so pumpInput's dc.send
    // can never run. This is the single guard that keeps a watcher from touching the game.
    if (spectator) return;
    if (inputTimer) return; // already started (a second onopen must not leak a second poll)
    // An input-only local-player session drives its pinned pad only — the PRIMARY session owns the
    // keyboard, and wiring it here too would send every keystroke to two seats at once.
    if (!inputOnly) {
      window.addEventListener("keydown", keyDown);
      window.addEventListener("keyup", keyUp);
    }
    window.addEventListener("blur", onWindowBlur);
    window.addEventListener("focus", onWindowFocus);
    // Poll the pads at 8 ms (perf program P2, 2026-09-05; was 16, briefly 4): the Gamepad API has no edge events, so
    // the poll interval IS the pad's input quantization — 16 ms cost ~8 ms on average before a press even
    // left the browser. Sends still happen only on change (pumpInput dedupes), so a faster poll costs
    // reads, not packets. Keyboard edges are sent from their own events (see onKey).
    inputTimer = setInterval(pumpInput, 8);
  }

  function stopInput() {
    window.removeEventListener("keydown", keyDown);
    window.removeEventListener("keyup", keyUp);
    window.removeEventListener("blur", onWindowBlur);
    window.removeEventListener("focus", onWindowFocus);
    if (inputTimer) clearInterval(inputTimer);
    inputTimer = null;
  }

  function setupPeer() {
    pc = new RTCPeerConnection({ iceServers });

    // The joypad channel is pre-negotiated — label/id/flags must match the server EXACTLY (Appendix A4)
    // or it silently never opens.
    dc = pc.createDataChannel("data", { negotiated: true, id: 0, ordered: false, maxRetransmits: 0 });
    dc.binaryType = "arraybuffer";
    dc.onopen = () => {
      ttffMark("dc-open");
      status("connected");
      startInput();
      // The channel opening IS the transport being up — start the game now (perf program P2, 2026-09-05).
      // This used to be found by a 100 ms poll in connect(), which added up to 100 ms to every room start.
      if (!gameStartSent) startGame();
    };
    // The worker PUSHES async control/UI packets to us over this same negotiated channel (room.Send →
    // the peer data channel): e.g. an achievement unlock (t=160) or a mid-game video-geometry change.
    // They're the same JSON envelope the signaling WS carries, just arriving here as an ArrayBuffer, so
    // decode to text and route through the shared handler. Input is browser→worker only; this direction
    // is worker→browser control, so anything that isn't our JSON simply fails handle()'s JSON.parse guard.
    dc.onmessage = (e) => {
      const d = e.data;
      if (typeof d === "string") { handle(d); return; }
      try { handle(new TextDecoder("utf-8").decode(d)); } catch { /* not a text control packet */ }
    };
    // A negotiated channel cannot reopen, and pumpInput's readyState guard just returns — so without
    // this the death of the input channel is INVISIBLE: media keeps flowing (audio even rides its own
    // aux PC), the status stays "playing", and the player is stuck in a room they can't control
    // (observed live 2026-07-09 after an alt-tab). Report it so the page can offer/perform recovery.
    // Spectators hold no input path, so a dc close means nothing to their session.
    dc.onclose = () => { if (!closed && !spectator) { stopInput(); status("input-lost"); } };

    // The worker opens side channels non-negotiated (keyboard/mouse/disc); we receive them here. The
    // "disc" channel carries a 1-byte target image index for multi-disc games (patch 0005). The "mouse"
    // channel (stock CloudRetro) carries RETRO_DEVICE_MOUSE relative deltas — see MOUSE_SYSTEMS.
    pc.ondatachannel = (ev) => {
      if (ev.channel && ev.channel.label === "disc") {
        discDc = ev.channel;
        discDc.binaryType = "arraybuffer";
      } else if (ev.channel && ev.channel.label === "mouse") {
        mouseDc = ev.channel;
        mouseDc.binaryType = "arraybuffer";
      }
    };

    pc.ontrack = onInboundTrack;
    pc.onicecandidate = (e) => {
      if (e.candidate) send(T.SIGNAL, { ice: JSON.stringify(e.candidate) });
    };
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === "failed" || pc.connectionState === "disconnected")
        status(pc.connectionState);
      if (pc.connectionState === "connected") ttffMark("pc-connected"); // ICE + DTLS up; SCTP/dc still to come
      if (pc.connectionState === "connected" && audioReceiverPc === pc) scheduleAudioJitterTiering();
    };
    // TTFF sub-marks for the transport hop (perf program P13): where inside init→dc-open the time goes.
    // offer = the worker's SDP arrived (signaling path); ice-checking = both sides have candidates and
    // checks started; ice-connected = a pair nominated; gather-done = our own gathering (STUN/TURN) finished.
    pc.onicegatheringstatechange = () => { if (pc.iceGatheringState === "complete") ttffMark("gather-done"); };
    pc.oniceconnectionstatechange = () => {
      if (pc.iceConnectionState === "checking") ttffMark("ice-checking");
      if (pc.iceConnectionState === "connected" || pc.iceConnectionState === "completed") ttffMark("ice-connected");
    };

    // Kick off: we are not the offerer — the server sends the SDP offer. sdp:"audio-pc" asks a
    // patch-0020 worker to put opus on a dedicated aux PeerConnection (ignored by older workers —
    // the worker only reads init sdp when initiator is true, so audio then just rides this PC).
    // An input-only local-player session plays nothing, so it never asks for the aux audio PC.
    // video_codec (worker patch 0036): the room's per-room codec, from ?codec= on EVERY member's
    // wsUrl (creator's choice, stored room-side). The peer's TRACK mime is fixed here — before the
    // game start — so it must match the room's one encoder. Absent = worker config default.
    // An input-only local-player session marks the (otherwise unused) init sdp field "input-only" so
    // the worker negotiates ONLY the input DataChannel — no video/audio tracks. Without it the worker
    // (the offerer) sends this machine a FULL DUPLICATE of the room's stream (it renders nothing here —
    // the primary session already plays it) and counts this redundant receiver in the room's worst-peer
    // ABR pool, dragging the shared encoder's bitrate down for everyone. It never wants the aux audio PC
    // either (nothing to hear), so the two markers are mutually exclusive.
    let init = inputOnly
      ? { initiator: false, sdp: "input-only" }
      : (AUDIO_PC ? { initiator: false, sdp: "audio-pc" } : { initiator: false });
    // Browser-offer mode: the sdp field carries OUR offer, so the aux-audio request moves to split_audio.
    const browserOffers = BROWSER_OFFERS && !inputOnly;
    if (browserOffers) init = { initiator: true, split_audio: !!AUDIO_PC };
    const codec = strFromWsUrl(descriptor.wsUrl, "codec");
    if (codec) init.video_codec = codec;
    // device_id + username (ABR quality plan, Phase 0): who this PEER is, for the worker's
    // session-close link-stat mirror. This message is the right carrier precisely because every peer
    // sends it for itself — so ONE insertion here covers both the creator and every joiner, where the
    // room-create params (t=104) are creator-only and could never describe a joiner's device.
    // Observability only: the worker files the row, and nothing reads it back yet.
    const deviceId = arcadeDeviceId();
    if (deviceId) init.device_id = deviceId;

    // warm_kbps (ABR plan Phase 1, perf program P10): the site's memory of what THIS device's link sustained
    // last time, from the join descriptor. The worker seeds this peer's bandwidth estimator with it (and
    // the room's opener when we are the creator) so a proven link skips the 20 s cold climb. Absent = cold.
    if (!inputOnly && Number.isFinite(descriptor.warmKbps) && descriptor.warmKbps > 0) init.warm_kbps = descriptor.warmKbps | 0;
    try {
      const who = localStorage.getItem("Username");
      if (who) init.username = who;
    } catch { /* storage disabled — the row is simply not attributable, and the worker drops it */ }
    if (browserOffers) {
      // LAST, after warm_kbps and username are on `init` (an earlier placement dropped both: no warm start
      // and no ArcadeLinkStat row for the peer). Offer the media we want to RECEIVE (video always; audio
      // here only when it is not on the aux PC) and let Pion answer onto those m-lines. The room's codec
      // is Pion's business (its track decides). Input-only seats never take this path: their "input-only"
      // sdp marker is how the worker recognises them, and there is no relay field for it.
      pc.addTransceiver("video", { direction: "recvonly" });
      if (!AUDIO_PC) pc.addTransceiver("audio", { direction: "recvonly" });
      pc.createOffer().then((offer) => pc.setLocalDescription(offer)).then(() => {
        init.sdp = JSON.stringify(pc.localDescription);
        send(T.INIT_WEBRTC, init);
      }).catch((err) => { onError && onError(err); });
      return;
    }
    send(T.INIT_WEBRTC, init);
  }

  // Shared by BOTH PeerConnections. Cloud gaming wants minimal receive buffering. Chrome's ADAPTIVE
  // jitter buffer is tiny for video (~8ms measured) but the AUDIO buffer grows unbounded (24→77ms in
  // 30s, from encoder clock drift) — and the browser lip-syncs video playout to audio, so the whole
  // stream drifts later the longer you play. Pin both receivers to the minimum. (jitterBufferTarget is
  // the standard; playoutDelayHint is the legacy Chrome name — set both, harmless where unknown.)
  // Roadmap WS-A.4: N64 was fine at 0, but cross-network multiplayer (4-player, mixed LAN+remote)
  // hits the exact "reopen" condition noted here — the send-path bursts make NetEq inflate + stretch.
  // Give AUDIO a small stable target (AUDIO_JITTER_MS: 80ms, 150ms on the cores that stall) to absorb the bursts; keep
  // VIDEO at 0 so it stays responsive (separate stream ids → video never lip-sync-waits on audio).
  // CloudRetro (Pion) sends audio and video as SEPARATE m-lines/streams (and with arcade.audioPC,
  // separate PeerConnections) — collect EVERY inbound track into ONE MediaStream so the element
  // plays both; per-track srcObject assignment left the last track orphaning the first.
  // ── Link-tiered audio jitter target (arcade perf program P4, 2026-09-05) ──────────────────────
  // AUDIO_JITTER_MS (80; 150 psp) was one number for every link. A same-host harness, a wired LAN desktop
  // and a phone on hotel Wi-Fi all got 80 ms of NetEq target, and audio sat ~80-100 ms behind video for
  // everyone. The browser can read its own selected ICE pair once the audio PeerConnection is connected
  // (getStats: candidate-pair RTT + the remote candidate's type/address), and jitterBufferTarget is
  // settable at any time — so the target is re-tiered LATE, from the measured link, with no server help:
  //   relay (TURN)                          -> 100 ms (a relayed path jitters more than a direct one)
  //   loopback / private-LAN remote, RTT<3  ->  40 ms (same house, wired: the bursts NetEq must absorb are tiny)
  //   everything else                       ->  80 ms (today's default)
  //   a per-system floor (psp 150) always wins, and an explicit localStorage arcade.audioJitterMs override
  //   (a human A/B knob) disables tiering entirely. arcade.audioJitterTiers=0 disables it too.
  // Applied at +2 s (the pair is nominated) and re-checked at +10 s (RTT has settled). One [audio-jb] line.
  let audioReceiver = null;      // the receiver carrying the audio track (on pc or apc)
  let audioReceiverPc = null;    // the PeerConnection it belongs to — the one whose stats describe the link
  let audioJitterApplied = null; // last target applied, ms
  function applyAudioJitter(ms, why) {
    if (!audioReceiver) return;
    try { audioReceiver.jitterBufferTarget = ms; } catch { /* older browsers */ }
    try { audioReceiver.playoutDelayHint = ms / 1000; } catch { /* non-Chrome */ }
    if (audioJitterApplied !== ms) console.log(`[audio-jb] target ${ms}ms (${why})`);
    audioJitterApplied = ms;
  }
  function audioJitterTiersEnabled() {
    try {
      if (localStorage.getItem("arcade.audioJitterTiers") === "0") return false;
      const v = parseInt(localStorage.getItem("arcade.audioJitterMs"), 10);
      if (Number.isFinite(v) && v >= 0) return false; // an explicit human override is not to be second-guessed
    } catch { /* localStorage unavailable — tiering stays on */ }
    return true;
  }
  const isPrivateAddr = (a) => {
    if (!a) return false;
    const s = String(a).toLowerCase();
    return s === "127.0.0.1" || s === "::1" || s.startsWith("10.") || s.startsWith("192.168.") ||
      /^172\.(1[6-9]|2\d|3[01])\./.test(s) || s.startsWith("fe80:") || s.startsWith("fc") || s.startsWith("fd");
  };
  async function tierAudioJitter(why) {
    if (closed || !audioReceiver || !audioReceiverPc || !audioJitterTiersEnabled()) return;
    if (typeof audioReceiverPc.getStats !== "function") return;
    let pair = null, remote = null;
    try {
      const st = await audioReceiverPc.getStats();
      const byId = new Map();
      st.forEach((r) => byId.set(r.id, r));
      st.forEach((r) => {
        if (r.type === "transport" && r.selectedCandidatePairId) pair = byId.get(r.selectedCandidatePairId) || pair;
      });
      if (!pair) st.forEach((r) => { if (r.type === "candidate-pair" && r.nominated && r.state === "succeeded") pair = pair || r; });
      if (pair && pair.remoteCandidateId) remote = byId.get(pair.remoteCandidateId) || null;
    } catch { return; }
    if (!pair) return; // not nominated yet — the +10 s pass will try again
    const rttMs = pair.currentRoundTripTime != null ? pair.currentRoundTripTime * 1000 : null;
    const kind = remote ? (remote.candidateType || remote.type) : null;
    const floor = AUDIO_JITTER_BY_SYSTEM[String(descriptor.system || "").toLowerCase()] ?? 0;
    let ms = AUDIO_JITTER_DEFAULT_MS, tier = "default";
    if (kind === "relay") { ms = 100; tier = "relay"; }
    else if (remote && isPrivateAddr(remote.address || remote.ip) && rttMs != null && rttMs < 3) { ms = 40; tier = "lan-wired"; }
    ms = Math.max(ms, floor);
    applyAudioJitter(ms, `${tier}, ${kind || "?"} ${remote ? (remote.address || remote.ip || "") : ""} rtt ${rttMs == null ? "?" : rttMs.toFixed(1)}ms, ${why}`);
  }
  function scheduleAudioJitterTiering() {
    setTimeout(() => tierAudioJitter("+2s"), 2000);
    setTimeout(() => tierAudioJitter("+10s"), 10000);
  }

  function onInboundTrack(e) {
    // TTFF: the first PRESENTED frame is the end of the start path. rVFC fires once per presented frame,
    // so a one-shot registration on the video track's attach marks it (ttffMark dedupes a re-attach).
    if (e.track && e.track.kind === "video" && videoEl) {
      if (videoEl.requestVideoFrameCallback) {
        try { videoEl.requestVideoFrameCallback(() => ttffMark("first-frame")); } catch { /* older browsers */ }
      } else {
        // No rVFC (Safari, older Firefox): the element's first "playing" is the closest thing to a presented frame.
        try { videoEl.addEventListener("playing", () => ttffMark("first-frame"), { once: true }); } catch { /* not an element */ }
      }
    }
    const jbMs = e.track && e.track.kind === "audio" ? AUDIO_JITTER_MS : 0;
    try { e.receiver.jitterBufferTarget = jbMs; } catch { /* older browsers */ }
    try { e.receiver.playoutDelayHint = jbMs / 1000; } catch { /* non-Chrome */ }
    if (e.track && e.track.kind === "audio") {
      audioReceiver = e.receiver;
      audioReceiverPc = (apc && apc.getReceivers && apc.getReceivers().includes(e.receiver)) ? apc : pc;
      audioJitterApplied = jbMs;
      // The track can attach before OR after the transport connects; the connected handlers below also
      // schedule, and tierAudioJitter is idempotent, so scheduling from both sides is safe.
      scheduleAudioJitterTiering();
    }
    inboundStream.addTrack(e.track);
    if (videoEl && videoEl.srcObject !== inboundStream) {
      videoEl.srcObject = inboundStream;
      videoEl.play?.().catch(() => {});
    }
    // The geometry (flip/rotation) message can land before OR after the track — re-assert it now
    // that the element is live so a GL core's frame isn't left upside down.
    applyVideoTransform(null);
  }

  // CloudRetro trickles ICE candidates that can arrive BEFORE its SDP offer. addIceCandidate() throws
  // ("remote description was null") if applied before setRemoteDescription — so buffer any early
  // candidates and flush them once the remote description is in place.
  let remoteReady = false;
  const pendingCandidates = [];

  async function addCandidate(iceString) {
    const candidate = JSON.parse(iceString);
    if (!remoteReady) { pendingCandidates.push(candidate); return; }
    try { await pc.addIceCandidate(candidate); } catch (err) { onError && onError(err); }
  }

  async function onSdp(sdpString) {
    ttffMark("offer"); // in browser-offer mode this is Pion's ANSWER arriving; same hop, same meaning for TTFF
    // Appendix A1/A2: signal values are JSON-stringified.
    const desc = JSON.parse(sdpString);
    await pc.setRemoteDescription(desc);
    remoteReady = true;
    while (pendingCandidates.length) {
      try { await pc.addIceCandidate(pendingCandidates.shift()); } catch (err) { onError && onError(err); }
    }
    if (desc && desc.type === "answer") return; // browser-offer mode: nothing to answer, ICE runs from here
    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);
    send(T.SIGNAL, { sdp: JSON.stringify(pc.localDescription) });
  }

  // ── aux audio PeerConnection (arcade.audioPC, worker patch 0020) ───────────────────────────────
  // The worker tunnels the aux PC's offer + ICE through the ice signal field with string prefixes
  // (the coordinator relays sdp/ice verbatim, so no protocol change): "aux-sdp:<json>" carries the
  // offer, "aux-ice:<json>" a candidate. We answer on the SIGNAL channel with the same "aux:" prefix
  // so the worker can route them to its aux PC. Audio tracks land in the SAME inboundStream.
  let apc = null;
  let auxRemoteReady = false;
  const auxPendingCandidates = [];

  async function onAuxOffer(sdpString) {
    if (!apc) {
      apc = new RTCPeerConnection({ iceServers });
      apc.ontrack = onInboundTrack;
      apc.onconnectionstatechange = () => { if (apc.connectionState === "connected") scheduleAudioJitterTiering(); };
      apc.onicecandidate = (e) => {
        if (e.candidate) send(T.SIGNAL, { ice: "aux:" + JSON.stringify(e.candidate) });
      };
    }
    await apc.setRemoteDescription(JSON.parse(sdpString));
    auxRemoteReady = true;
    while (auxPendingCandidates.length) {
      try { await apc.addIceCandidate(auxPendingCandidates.shift()); } catch (err) { onError && onError(err); }
    }
    const answer = await apc.createAnswer();
    await apc.setLocalDescription(answer);
    send(T.SIGNAL, { sdp: "aux:" + JSON.stringify(apc.localDescription) });
  }

  async function addAuxCandidate(iceString) {
    const candidate = JSON.parse(iceString);
    if (!auxRemoteReady) { auxPendingCandidates.push(candidate); return; }
    try { await apc.addIceCandidate(candidate); } catch (err) { onError && onError(err); }
  }

  async function onSignal(p) {
    try {
      if (p.sdp) await onSdp(p.sdp);
      if (p.ice) {
        if (p.ice.startsWith("aux-sdp:")) await onAuxOffer(p.ice.slice(8));
        else if (p.ice.startsWith("aux-ice:")) await addAuxCandidate(p.ice.slice(8));
        else await addCandidate(p.ice);
      }
    } catch (err) { onError && onError(err); }
  }

  function startGame() {
    if (gameStartSent) return;
    gameStartSent = true;
    // Both creator and joiner send the room_id from the wsUrl. The creator's is now a DETERMINISTIC
    // save id (sv-…___game, docs/arcade-saves-plan.md) rather than empty: CloudRetro creates a fresh
    // room with exactly that id (it accepts a non-live <prefix>___<gameKey> id), which lets the gateway
    // seed/harvest this user's save by a predictable filename. A joiner's is the creator's bound id.
    const roomId = roomIdFromWsUrl(descriptor.wsUrl);
    // Per-room encoder quality (arcade per-room bitrate/FEC): the creator's wsUrl carries ?vbr=<kbps>
    // and ?fec=<0|1|2> (appended by the backend). Only the creator's t=104 builds the room's encoder,
    // so only these values matter; a joiner's wsUrl won't have them. Omit when unset (worker uses config).
    // player_index sets which controller port THIS connection's input frames land on. A spectator sends
    // none, so its index is inert — but never send the -1 sentinel: the worker stores it verbatim
    // (`user.Index = rq.PlayerIndex`) and it would index a port slice if anything ever did send. 0 is safe
    // and shared harmlessly with the host.
    const p = {
      game_name: descriptor.gameKey,
      room_id: roomId,
      player_index: spectator ? 0 : descriptor.playerSlot | 0,
    };
    const vbr = numFromWsUrl(descriptor.wsUrl, "vbr");
    const fec = numFromWsUrl(descriptor.wsUrl, "fec");
    const pace = numFromWsUrl(descriptor.wsUrl, "pace");
    const codec = strFromWsUrl(descriptor.wsUrl, "codec");
    const hwctx = strFromWsUrl(descriptor.wsUrl, "hwctx");
    const core = strFromWsUrl(descriptor.wsUrl, "core");
    const ctrlscheme = strFromWsUrl(descriptor.wsUrl, "ctrlscheme");
    if (vbr > 0) p.video_bitrate = vbr;
    if (fec > 0) p.audio_fec = fec;
    // RUN LEGITIMACY: tell the worker this boot restored a state the player DELIBERATELY PICKED (a named
    // snapshot / Quickload, ?seedslot=N) rather than the ordinary auto-save continue. The worker cannot
    // tell them apart on its own — all it sees is "a save existed and we restored it" — so without this
    // it tagged EVERY resumed room save-scummed before a button was pressed, and nobody who plays across
    // sessions could ever earn a clean achievement. Absent = auto-continue = clean.
    if (numFromWsUrl(descriptor.wsUrl, "seedslot") > 0) p.seed_explicit = true;
    // Per-room codec (worker patch 0036): the creator's t=104 selects which encoder.list entry this
    // room's pipeline builds with; must match the video_codec every member sent at INIT_WEBRTC.
    if (codec) p.video_codec = codec;
    // Per-launch GL/Vulkan force (play-button dropdown). Creator-only — it only affects the room's
    // one-time CoreLoad, so unlike codec it never needs to ride a joiner's descriptor.
    if (hwctx) p.hw_context = hwctx;
    // Per-room CORE override (arcade render profiles): boot an alternate core-key, e.g. PS1 pcsx_rearmed
    // instead of the default Beetle. Creator-only, consumed once at CoreLoad — like hw_context.
    if (core) p.core = core;
    // Per-room Wii controller-scheme override (room-create picker): GameCube vs Wiimote+Nunchuk for
    // the GC-controller-native BrawlEx mods. Only the CREATOR's t=104 boots the core (joiners never
    // send GAME_START at all), so only its packet needs this — but unlike hw_context, the room's
    // chosen scheme also rides EVERY descriptor's wsUrl (creator AND joiners, server-side Join/
    // ClaimSeat echo it like codec), because it changes what button bits every client must send, not
    // just how the creator's one-time CoreLoad renders. See effectiveInputSystem's ctrlSchemeFromWsUrl use.
    if (ctrlscheme) p.controller_scheme = ctrlscheme;
    // In-frame packet pacing window ms (worker patch 0028) — the lobby Network profile's opt-in
    // smoother for Remote/5G rooms. Absent/0 = LAN default, wire-speed bursts.
    if (pace > 0) p.pace = pace;
    // Per-room cheats the creator picked in the lobby. Unlike vbr/fec these ride the descriptor body, not
    // the wsUrl query: a cheat code list runs to kilobytes. The worker merges core_options before the ROM
    // loads and feeds cheats to retro_cheat_set after (patch 0027). Only the creator's t=104 builds the
    // room, so a joiner's descriptor carries neither.
    if (descriptor.coreOptions && Object.keys(descriptor.coreOptions).length > 0) p.core_options = descriptor.coreOptions;
    if (Array.isArray(descriptor.cheats) && descriptor.cheats.length > 0) p.cheats = descriptor.cheats;
    // RetroAchievements: the worker runs a single SITE service account as the scoring engine (spectator
    // mode), so no per-user creds ride here. All the creator's t=104 needs to carry is whether this is a
    // COMPETITIVE (legit) run — the worker tags mirrored achievements/scores/times with it.
    // The RetroAchievements hash the SITE computed for this dump (?rahash=). With it the worker loads
    // that RA game directly instead of hashing the ROM itself — which is the only way PS2/PSP (.cso),
    // GameCube (.gcz) and Dreamcast/Saturn (.chd) rooms can be identified at all, since rc_hash cannot
    // read those containers. Creator-only, like the other boot flags: joiners never send GAME_START.
    const rahash = strFromWsUrl(descriptor.wsUrl, "rahash");
    if (rahash) p.ra_hash = rahash;
    if (descriptor.competitive) p.hardcore = true;
    send(T.GAME_START, p);
  }

  // GL cores (e.g. N64/gliden64) render bottom-left-origin, so CloudRetro flags the frame flipped
  // (and may report a rotation). It sends the geometry in the GAME_START response's `av` and in any
  // later t=150 AppVideoChange. Mirror the stock client: flip → scaleY(-1), rot → rotate(-Ndeg).
  //
  // `av.a` is the core's OWN display aspect ratio (retro_get_system_av_info geometry.aspect_ratio,
  // surfaced by Nanoarch.AspectRatio() for every core, not just the coreAspectRatio ones). We used to
  // throw it away and hardcode 4:3 in ArcadeRoomPage, which squeezed PSP's 16:9 into 4:3 and distorted
  // every handheld (gg/wsc/lynx/ngpc/vb) plus widescreen PS2/GC/DC titles. Per the libretro spec a
  // value <= 0 means "unspecified — derive it from base_width/base_height", so we only report a
  // plausible positive ratio upward and let the caller fall back to its per-system table.
  // `a` is used VERBATIM — it is already the intended DISPLAY aspect, not the framebuffer's.
  // Do NOT invert it for rotated boards: fbneo's vertical cabs report base 256x224 with a = 0.75
  // (measured on 1942, 2026-07-08), i.e. the post-rotation ratio, and CloudRetro has already
  // transposed the encoded frame (it arrives 672x768). Inverting would flip vertical shooters back
  // to landscape. The stock client does the same thing — see cloud-game web/js/stream.js `resize()`,
  // which assigns style.aspectRatio = a and applies rotate(-rot) independently.
  function reportAspect() {
    if (!onAspect || !lastAv) return;
    onAspect({ aspect: displayAspect(lastAv), rot: (Number(lastAv.rot) || 0) % 360, flip: !!lastAv.flip });
  }

  // NB: this NEVER touches videoEl.style. React owns the transform (ArcadeRoomPage renders it from the
  // geometry we report). Two reasons, both bugs we shipped by doing it imperatively:
  //   1. A core that sets no `coreAspectRatio` never gets an `av` at all (21 of 29 do not — every 2D
  //      system plus ps1), so the transform was never written. The <video> is absolutely centred at
  //      top/left 50%, and without the compensating translate(-50%,-50%) the picture sat in the
  //      bottom-right quadrant. Reported on Castlevania: SotN.
  //   2. Even where `av` did arrive, the next React re-render (the 12 s heartbeat updates player state)
  //      would overwrite style.transform from the inline style and silently drop the flip/rotate.
  function applyVideoTransform(av) {
    if (av) lastAv = av;
    if (!lastAv) return;
    reportAspect();
  }

  // Rumble (perf program P11): the worker sends this seat's (strong, weak) pair on change. libretro rumble
  // is a level ("until I say otherwise"); the Gamepad API plays timed effects, so each packet plays a
  // long effect the next packet replaces, and (0,0) resets. Plays on the pad THIS seat drives: the pin
  // for a local-player session, the latched pad for the primary. localStorage arcade.rumble=0 mutes it.
  let rumbleMuted = false;
  try { rumbleMuted = localStorage.getItem("arcade.rumble") === "0"; } catch { /* storage unavailable */ }
  function applyRumble(p) {
    if (rumbleMuted || spectator) return;
    const idx = pinnedPad >= 0 ? pinnedPad : activePadIndex;
    if (idx < 0) return;
    let gp = null;
    try { gp = navigator.getGamepads?.()[idx] || null; } catch { return; }
    const act = gp && gp.vibrationActuator;
    if (!act) return;
    const strong = Math.max(0, Math.min(1, (p.strong | 0) / 65535));
    const weak = Math.max(0, Math.min(1, (p.weak | 0) / 65535));
    try {
      if (strong === 0 && weak === 0) { act.reset?.(); return; }
      act.playEffect?.("dual-rumble", { startDelay: 0, duration: 1000, strongMagnitude: strong, weakMagnitude: weak })?.catch?.(() => {});
    } catch { /* a pad without rumble, or a browser without the API */ }
  }

  function onGameStarted(p) {
    ttffMark("game-start");
    const roomId = p && (p.roomId || p.room_id);
    if (descriptor.isCreator && roomId) onRoomId && onRoomId(roomId);
    if (p && p.av) applyVideoTransform(p.av);
    // Confirm the seat; the worker answers the accepted index (-1 = rejected). A spectator claims no
    // seat at all, so it never sends this — the room's players keep the ports they hold.
    if (!spectator) send(T.SET_PLAYER_INDEX, descriptor.playerSlot | 0);
    status(spectator ? "spectating" : "playing");
  }

  function handle(msg) {
    let data;
    try { data = JSON.parse(msg); } catch { return; }
    switch (data.t) {
      case T.INIT:
        // ICE config + game list arrive here (there is no HTTP game-list API).
        // UNION, never replace. The coordinator sends its own (STUN) list, but the TURN relay + its
        // per-join minted credential ride the SITE's descriptor — and setupPeer() runs below, AFTER
        // this assignment, so a wholesale replace meant the RTCPeerConnection never saw the relay at
        // all. That silently disabled TURN for every client since it shipped: LAN clients never
        // noticed (host candidates work), while guest-isolated/remote clients — the only ones the
        // relay exists for — stalled at negotiating with no relay candidate to gather.
        if (data.p && Array.isArray(data.p.ice) && data.p.ice.length) {
          const fromCoordinator = data.p.ice.map((s) => ({ urls: s.urls, username: s.username, credential: s.credential }));
          const seen = new Set(fromCoordinator.map((s) => s.urls));
          iceServers = fromCoordinator.concat(iceServers.filter((s) => !seen.has(s.urls)));
        }
        ttffMark("init");
        setupPeer();
        break;
      case T.INIT_WEBRTC:
      case T.SIGNAL:
        onSignal(data.p || {});
        break;
      case T.GAME_START:
        onGameStarted(data.p || {});
        break;
      case T.SET_PLAYER_INDEX: {
        const idx = typeof data.p === "number" ? data.p : (data.p && data.p.index);
        if (idx === -1) { status("seat-rejected"); onError && onError(new Error("Seat was rejected.")); }
        else if (typeof idx === "number") onSeat && onSeat(idx);
        break;
      }
      case T.NO_FREE_SLOTS:
        status("arcade-full");
        onError && onError(new Error("The arcade is full — no free machines."));
        break;
      case T.APP_VIDEO_CHANGE:
        // Geometry/flip/rotation update (GL cores flip; some cores rotate).
        applyVideoTransform(data.p || {});
        break;
      case T.RUMBLE:
        applyRumble(data.p || {});
        break;
      case T.ACHIEVEMENT_UNLOCK:
        // rcheevos unlocked an achievement (worker push) — hand it up for a live toast. Purely cosmetic;
        // the authoritative record is RetroAchievements + the server mirror callback, never this packet.
        onAchievement && onAchievement(data.p || {});
        break;
      default:
        // t=3 latency, 2xx internal — ignored in v1.
        break;
    }
  }

  function send(t, p, id) {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(packet(t, p, id));
  }

  function connect() {
    ttff.t0 = nowMs();
    status("connecting");
    ws = new WebSocket(descriptor.wsUrl);
    ws.onopen = () => { ttffMark("ws-open"); status("signalling"); };
    ws.onmessage = (e) => handle(e.data);
    // Don't cry "connection failed" when WE closed it (session teardown / React StrictMode's throwaway
    // first mount) — only a genuine, still-open failure should surface to the user.
    ws.onerror = () => { if (!closed) onError && onError(new Error("Signaling connection failed.")); };
    ws.onclose = () => { if (!closed) status("disconnected"); };
    // The INIT (t=4) arrives right after open and drives setupPeer(); GAME_START is sent from dc.onopen
    // the moment the DataChannel opens. This slow poll is only the safety net for a channel that was
    // already open before its onopen handler could fire (never observed; kept because the cost of a
    // missed start is a room that never plays).
    const armStart = setInterval(() => {
      if (closed || gameStartSent) { clearInterval(armStart); return; }
      if (dc && dc.readyState === "open") { clearInterval(armStart); startGame(); }
    }, 2000);
  }

  function close() {
    if (closed) return;
    closed = true;
    status("closed");
    stopInput();
    // Leaving the rVFC loop alive on a dead video element keeps a canvas readback running for a room
    // that no longer exists. A tape in progress is dropped, not saved: an unfinished tape whose room
    // vanished mid-recording has no anchor to replay against.
    replaySource = null;
    tape = null;
    stopVideoClock();
    detachPointer();
    detachMouse();
    if (pinnedPad >= 0) claimedPadIndexes.delete(pinnedPad);
    try { send(T.GAME_QUIT, { room_id: roomIdFromWsUrl(descriptor.wsUrl) }); } catch { /* */ }
    try { dc && dc.close(); } catch { /* */ }
    try { discDc && discDc.close(); } catch { /* */ }
    try { mouseDc && mouseDc.close(); } catch { /* */ }
    try { pc && pc.close(); } catch { /* */ }
    try { apc && apc.close(); } catch { /* */ }
    try { ws && ws.close(); } catch { /* */ }
    if (videoEl) videoEl.srcObject = null;
  }

  // ── Touch pointer (W10 stylus/touch) ───────────────────────────────────────────────────────────
  // PRIMARY session only, pointer-capable systems only (nds). Seat-claim = NO: this is an extra input
  // MODALITY on the session that already owns seat 0 + the video, not a new seat — so it never runs the
  // pad-autobind/claim path. Input-only 2nd-pad sessions and spectators never attach (and the worker
  // ignores port>0 pointer anyway — defense in depth). The player's OS mouse cursor is the aim
  // indicator, so we stream moves only while PRESSED (a drag) plus immediate down/up edges — no hover
  // flood on the channel that also carries pad input. Rides the primary's own `dc`.
  const pointerEnabled = !spectator && !inputOnly && !!videoEl && systemUsesPointer(descriptor.system);
  let ptrPressed = false;
  let ptrLastSent = null; // {x,y,pressed} — dedupe identical packets
  let ptrPending = null;  // coord awaiting the rAF flush (drag coalescing)
  let ptrRaf = 0;
  let ptrCaptureId = null;

  function ptrMap(ev) {
    const rect = videoEl.getBoundingClientRect();
    if (!rect || rect.width <= 0 || rect.height <= 0) return null;
    // objectFit:fill maps the FULL video frame linearly onto the element rect, so a fraction across the
    // rect IS the same fraction across the frame — independent of the box's aspect. getBoundingClientRect
    // is post-CSS-transform, so for an axis-aligned (un-rotated) video this needs no further correction.
    let fx = (ev.clientX - rect.left) / rect.width;
    let fy = (ev.clientY - rect.top) / rect.height;
    fx = Math.max(0, Math.min(1, fx));
    fy = Math.max(0, Math.min(1, fy));
    // Undo the room's videoTransform: scaleY(-1) flips Y, so a GL-flipped room needs the pointer's Y
    // un-flipped to match the RAW framebuffer the core hit-tests in (citra/3ds). EXCEPT cores that map
    // the pointer in display/logical space regardless of the GL flip (melonDS DS) — undoing it there
    // double-inverts and taps land vertically mirrored. No current pointer system rotates, so rotation
    // is intentionally not undone here.
    if (lastAv && lastAv.flip && !pointerIgnoresFrameFlip(descriptor.system)) fy = 1 - fy;
    const x = Math.max(-32767, Math.min(32767, Math.round((fx * 2 - 1) * 32767)));
    const y = Math.max(-32767, Math.min(32767, Math.round((fy * 2 - 1) * 32767)));
    return { x, y };
  }
  function ptrSend(x, y, pressed) {
    if (!dc || dc.readyState !== "open") return;
    if (ptrLastSent && ptrLastSent.x === x && ptrLastSent.y === y && ptrLastSent.pressed === pressed) return;
    ptrLastSent = { x, y, pressed };
    try { dc.send(encodePointer(x, y, pressed)); } catch { /* channel closing */ }
  }
  function ptrFlush() {
    ptrRaf = 0;
    // Send with the CURRENT press state: 1 while dragging (stylus down), 0 while hovering — the hover
    // position is what makes the core's touch cursor (Citra render_touchscreen) track the mouse.
    if (ptrPending) ptrSend(ptrPending.x, ptrPending.y, ptrPressed ? 1 : 0);
    ptrPending = null;
  }
  function onPtrDown(ev) {
    if (ev.pointerType === "mouse" && ev.button !== 0) return; // left button drives the stylus
    const p = ptrMap(ev);
    if (!p) return;
    ptrPressed = true;
    ptrCaptureId = ev.pointerId;
    // Capture keeps move/up flowing even when the cursor leaves the video mid-drag, so a release OUTSIDE
    // the element still fires onPtrUp (the "pointer left mid-press" case) — the touch never sticks.
    try { videoEl.setPointerCapture(ev.pointerId); } catch { /* unsupported */ }
    ptrPending = null;
    ptrSend(p.x, p.y, 1); // immediate down edge
    ev.preventDefault();
  }
  function onPtrMove(ev) {
    // Stream the position for BOTH a pressed drag (stylus down) and an unpressed hover. Hover sends
    // pressed=0, which never taps but moves the core's touch cursor (Citra render_touchscreen crosshair)
    // so you can SEE where the stylus is before tapping — some 3DS UIs are unusable without a visible
    // pointer. Naturally gated to "mouse over the video" (pointermove only fires over videoEl); coalesced
    // to one send per animation frame; identical positions are de-duped in ptrSend.
    const p = ptrMap(ev);
    if (!p) return;
    ptrPending = p;
    if (!ptrRaf && typeof requestAnimationFrame === "function") ptrRaf = requestAnimationFrame(ptrFlush);
    if (ptrPressed) ev.preventDefault(); // only a drag suppresses default; leave hover/scroll alone
  }
  function onPtrUp(ev) {
    if (!ptrPressed) return;
    const p = ptrMap(ev) || ptrLastSent;
    ptrPressed = false;
    if (ptrRaf && typeof cancelAnimationFrame === "function") { cancelAnimationFrame(ptrRaf); }
    ptrRaf = 0;
    ptrPending = null;
    if (p) ptrSend(p.x, p.y, 0); // immediate up edge — release the touch
    try { if (ptrCaptureId != null) videoEl.releasePointerCapture(ptrCaptureId); } catch { /* */ }
    ptrCaptureId = null;
  }
  function onPtrCancel() {
    if (!ptrPressed && !ptrPending) return; // never leave a touch stuck pressed
    ptrPressed = false;
    if (ptrRaf && typeof cancelAnimationFrame === "function") { cancelAnimationFrame(ptrRaf); }
    ptrRaf = 0;
    ptrPending = null;
    if (ptrLastSent) ptrSend(ptrLastSent.x, ptrLastSent.y, 0);
    try { if (ptrCaptureId != null) videoEl.releasePointerCapture(ptrCaptureId); } catch { /* */ }
    ptrCaptureId = null;
  }
  function attachPointer() {
    if (!pointerEnabled) return;
    videoEl.addEventListener("pointerdown", onPtrDown);
    videoEl.addEventListener("pointermove", onPtrMove);
    videoEl.addEventListener("pointerup", onPtrUp);
    videoEl.addEventListener("pointercancel", onPtrCancel);
    // Touch devices: stop the browser turning a stylus drag into a scroll/zoom on the video surface.
    try { videoEl.style.touchAction = "none"; } catch { /* */ }
  }
  function detachPointer() {
    if (!pointerEnabled) return;
    videoEl.removeEventListener("pointerdown", onPtrDown);
    videoEl.removeEventListener("pointermove", onPtrMove);
    videoEl.removeEventListener("pointerup", onPtrUp);
    videoEl.removeEventListener("pointercancel", onPtrCancel);
    if (ptrRaf && typeof cancelAnimationFrame === "function") { cancelAnimationFrame(ptrRaf); ptrRaf = 0; }
  }
  attachPointer();

  // ── Mouse (RETRO_DEVICE_MOUSE — ScummVM): ABSOLUTE positioning over a relative device ───────────
  // PRIMARY session only, mouse-capable systems only (scummvm). Same non-seat rationale as the pointer
  // block above. ScummVM applies RETRO_DEVICE_MOUSE deltas on every poll regardless of button state,
  // which is what makes hover work at all (its POINTER handling does not — see MOUSE_SYSTEMS).
  //
  // WHY THIS IS NOT A SIMPLE DELTA FORWARDER ANY MORE. Streaming raw movement deltas gives you a
  // cursor that is near the pointer rather than under it, and every source of error is PERMANENT
  // because nothing ever re-establishes agreement:
  //   * the two cursors start wherever they each happen to be, already offset;
  //   * ScummVM multiplies by its own scummvm_mouse_speed;
  //   * the real pointer stops at the window edge while the game cursor still has room;
  //   * pointer lock (tried first, now removed) changes movementX into RAW DEVICE units with no OS
  //     acceleration or DPI scaling, so a gain calibrated in CSS pixels is simply wrong under it.
  // Eric's report of both symptoms in turn — "some large offset", then "sensitivity is way off, it's
  // clear the cursor isn't my hardware mouse" — is that list, one item at a time.
  //
  // So instead of forwarding what the mouse DID, we send whatever delta moves the cursor to where the
  // pointer IS: keep a model of ScummVM's cursor, and each move send (target - model). The model is
  // re-derived from an ABSOLUTE clientX/clientY every single event, so any error survives exactly one
  // mouse move instead of forever, and OS pointer acceleration stops mattering entirely — the cursor
  // is wherever your hardware pointer is, because that is the quantity being solved for.
  //
  // Three things have to hold for the model to stay true, and all three are now guaranteed:
  //   1. no lost messages — the "mouse" channel was on the unreliable default (a dropped delta was an
  //      unrecoverable shift); the worker now opens it ordered+reliable.
  //   2. a known multiplier — config.worker-gl.yaml pins scummvm_mouse_speed, and MOUSE_SPEED below
  //      MUST equal it. ScummVM's own source is `deltaAcc = (float)x * mouse_speed` with no screen
  //      ratios of any kind (libretro-os-inputs.cpp), so inverting it is exact.
  //   3. a known origin — see mseCalibrate.
  const mouseEnabled = !spectator && !inputOnly && !!videoEl && systemUsesMouse(descriptor.system);
  let mseRaf = 0;
  let mseLastMask = 0;
  // Our belief about where ScummVM's cursor is, in the core's own pixels. null = unknown, which forces
  // a calibration before the next move is trusted.
  let mseModelX = null, mseModelY = null;
  let mseTargetX = 0, mseTargetY = 0, msePending = false;
  let mseCalibratingUntil = 0;

  // MUST equal config.worker-gl.yaml's scummvm_mouse_speed. 1.25 rather than the more obvious 1.0
  // because 1.25 is a token this core's own option table PROVABLY contains (extracted from the shipped
  // DLL: 0.05 0.15 0.35 0.45 1.25 1.75 ...) and libretro silently ignores an unknown option VALUE — a
  // wrong token would leave the core on its own default and skew every delta with nothing logged. It
  // is also exactly representable in binary floating point, so inverting it introduces no error.
  const MOUSE_SPEED = 1.25;

  // CSS-px → core-px gain: how many of the CORE's own coordinate units one pixel of real mouse travel
  // should be worth, so the ScummVM cursor keeps pace with the physical pointer instead of drifting at
  // some arbitrary sensitivity.
  //
  // The core clamps its cursor to its OWN unscaled resolution (getScreenWidth/Height), which is NOT the
  // decoded video size — the worker upscales before encoding. The authority on that unscaled size is the
  // worker's own APP_VIDEO_CHANGE payload (`av.w`/`av.h` = ViewportSize, the frame the core actually
  // renders), which every ScummVM room sends because the core switches from its declared 1280x720 GUI
  // geometry to the game's real size on the first frame. Using it means this needs no per-game table and
  // no client/config parity: a 320x200 SCUMM classic and a 640x480 Broken Sword each get the right gain,
  // and changing the server-side `scale` can never silently skew the cursor.
  //
  // The videoWidth/SCUMMVM_VIDEO_SCALE fallback is only for a room where that payload never arrived.
  // Where the pointer is, expressed in the core's own pixel grid — the target the cursor must reach.
  // Clamped exactly the way ScummVM clamps (0 .. screen-1) so that a pointer parked outside the
  // picture and the core's own limit agree on where "the edge" is.
  function mseTargetFor(ev) {
    const rect = videoEl.getBoundingClientRect();
    if (!rect || rect.width <= 0 || rect.height <= 0) return null;
    if (!videoEl.videoWidth || !videoEl.videoHeight) return null; // no frame decoded yet
    const coreW = lastAv && lastAv.w > 0 ? lastAv.w : videoEl.videoWidth / SCUMMVM_VIDEO_SCALE;
    const coreH = lastAv && lastAv.h > 0 ? lastAv.h : videoEl.videoHeight / SCUMMVM_VIDEO_SCALE;
    const clamp = (v, hi) => Math.max(0, Math.min(hi - 1, v));
    return {
      x: clamp(((ev.clientX - rect.left) / rect.width) * coreW, coreW),
      y: clamp(((ev.clientY - rect.top) / rect.height) * coreH, coreH),
      w: coreW, h: coreH,
    };
  }

  // Establish the origin. Nothing reports ScummVM's cursor position back to us, so the only way to
  // KNOW it is to drive it somewhere it cannot go: a delta far larger than the screen clamps against
  // the top-left, which is a fixed point regardless of where the cursor was or what mouse_speed is.
  //
  // It has to be a message of its own with a gap after it. MouseState.ShiftPos ACCUMULATES into an
  // atomic that the emulator drains once per frame, so a slam and a correction sent in the same frame
  // would be summed and the clamp — the entire point — would never happen. mseCalibratingUntil holds
  // off the correction for a few frames; the very next move then lands the cursor exactly, because
  // every move is absolute.
  function mseCalibrate(t) {
    const slam = Math.ceil((Math.max(t.w, t.h) * 2) / MOUSE_SPEED);
    mseWrite(-slam, -slam);
    mseModelX = 0;
    mseModelY = 0;
    mseCalibratingUntil = (typeof performance !== "undefined" ? performance.now() : Date.now()) + 60;
  }
  function mseWrite(dx, dy) {
    if (!mouseDc || mouseDc.readyState !== "open") return;
    const cx = Math.max(-32767, Math.min(32767, Math.round(dx)));
    const cy = Math.max(-32767, Math.min(32767, Math.round(dy)));
    if (!cx && !cy) return;
    try { mouseDc.send(encodeMouseMove(cx, cy)); } catch { /* channel closing */ }
  }
  function mseSendButtons(mask) {
    if (!mouseDc || mouseDc.readyState !== "open") return;
    if (mask === mseLastMask) return;
    mseLastMask = mask;
    try { mouseDc.send(encodeMouseButtons(mask)); } catch { /* channel closing */ }
  }
  // One send per animation frame, carrying the CURRENT target — coalescing is free here precisely
  // because the payload is a position rather than a movement: intermediate samples are not information
  // we lose, they are steps toward the same answer.
  function mseFlush() {
    mseRaf = 0;
    if (!msePending) return;
    const now = typeof performance !== "undefined" ? performance.now() : Date.now();
    if (now < mseCalibratingUntil) {                // let the slam clamp before correcting off it
      mseRaf = typeof requestAnimationFrame === "function" ? requestAnimationFrame(mseFlush) : 0;
      return;
    }
    msePending = false;
    if (mseModelX == null) return;
    // Wire units are what the CORE will multiply by mouse_speed, so divide it out here. The model then
    // advances by what the core will ACTUALLY do with the rounded integer we sent (not by what we
    // wanted), so rounding can never accumulate — and the next event recomputes the target absolutely
    // anyway, which is the real guarantee.
    const wx = Math.round((mseTargetX - mseModelX) / MOUSE_SPEED);
    const wy = Math.round((mseTargetY - mseModelY) / MOUSE_SPEED);
    if (!wx && !wy) return;
    mseWrite(wx, wy);
    mseModelX += wx * MOUSE_SPEED;
    mseModelY += wy * MOUSE_SPEED;
  }
  function onMseDown(ev) {
    if (ev.button !== 0) return; // left click only — right/middle unused by ScummVM's UI
    // Aim before firing. A click is the one moment where being a pixel out is the difference between
    // opening the door and walking into it, and pointer-down carries its own absolute coordinates.
    onMseMove(ev);
    mseFlush();
    mseSendButtons(mseLastMask | 0x01);
    ev.preventDefault();
  }
  function onMseMove(ev) {
    // ABSOLUTE, from clientX/clientY — never ev.movementX. The delta is computed in mseFlush as
    // (target - model), so OS pointer acceleration, DPI scaling and any missed event are all
    // irrelevant: whatever happened in between, the answer is still "put the cursor here".
    const t = mseTargetFor(ev);
    if (!t) return;
    mseTargetX = t.x;
    mseTargetY = t.y;
    msePending = true;
    if (mseModelX == null) mseCalibrate(t);
    if (!mseRaf && typeof requestAnimationFrame === "function") mseRaf = requestAnimationFrame(mseFlush);
  }
  function onMseUp(ev) {
    if (ev.button !== 0) return;
    mseSendButtons(mseLastMask & ~0x01);
  }
  function onMseEnter() {
    // Re-establish the origin on every entry. The model can only be wrong if something moved the
    // cursor that we did not — a game warping it itself, or a room resumed after the pointer spent
    // time elsewhere — and entering the picture is both the moment that is most likely to have
    // happened and the last moment before it would be visible.
    mseModelX = null;
    mseModelY = null;
  }
  function onMseLeave() {
    // Don't leave a click stuck down if the pointer wanders off the video mid-press.
    if (mseLastMask) mseSendButtons(0);
  }
  function onMseContextMenu(e) { e.preventDefault(); }
  function attachMouse() {
    if (!mouseEnabled) return;
    videoEl.addEventListener("mousedown", onMseDown);
    videoEl.addEventListener("mousemove", onMseMove);
    videoEl.addEventListener("mouseup", onMseUp);
    videoEl.addEventListener("mouseenter", onMseEnter);
    videoEl.addEventListener("mouseleave", onMseLeave);
    videoEl.addEventListener("contextmenu", onMseContextMenu);
  }
  function detachMouse() {
    if (!mouseEnabled) return;
    videoEl.removeEventListener("mousedown", onMseDown);
    videoEl.removeEventListener("mousemove", onMseMove);
    videoEl.removeEventListener("mouseup", onMseUp);
    videoEl.removeEventListener("mouseenter", onMseEnter);
    videoEl.removeEventListener("mouseleave", onMseLeave);
    videoEl.removeEventListener("contextmenu", onMseContextMenu);
    if (mseRaf && typeof cancelAnimationFrame === "function") { cancelAnimationFrame(mseRaf); mseRaf = 0; }
  }
  attachMouse();

  connect();

  // Save / load / reset / disc-swap act on the ROOM's single shared emulator, so they are the players'
  // to make, not a watcher's. Guarded here rather than only in the UI: this shim is the wire, and a
  // spectator's page must not be able to reset someone else's game by any route.
  const asPlayer = (fn) => (...args) => { if (!spectator) fn(...args); };

  return {
    close,
    // The pad this seat is ACTIVELY using — the pin when one is set; for a fluid primary, the latched
    // pad only while it has shown input in the last 10 s (-1 otherwise). The room page excludes it
    // when listening for a NEW controller's button press, and a keyboard-only primary must not
    // shadow the idle pad it passively latched.
    getActivePadIndex: () =>
      (pinnedPad >= 0 ? pinnedPad : (inputOnly || Date.now() - lastPadActiveAt >= 10_000 ? -1 : activePadIndex)),
    // Freeze/unfreeze the primary's fluid pad adoption — the room page holds it while listening for
    // a new controller's button press, so the primary can't steal (and thereby hide) that pad.
    setAdoptionHeld: (held) => { adoptionHeld = !!held; },
    // Re-pin this seat to another physical pad (Controllers panel). null/undefined = unassign: a
    // local seat then sends neutral; the primary falls back to fluid adopt-any-unclaimed-pad.
    setPad: (idx) => {
      const next = Number.isInteger(idx) && idx >= 0 ? idx : -1;
      if (next === pinnedPad) return;
      if (pinnedPad >= 0) claimedPadIndexes.delete(pinnedPad);
      pinnedPad = next;
      pinnedPadId = null; // a hand-assignment re-learns the model; never follow the OLD pad's id
      if (pinnedPad >= 0) claimedPadIndexes.add(pinnedPad);
      // Null the dedupe so the next pump resends true state — the old pad's held buttons release
      // on the worker instead of riding this seat forever.
      last = null;
    },
    // Resync input after player assignment changes (t=108). When a new seat index is assigned,
    // the core may remap its port bindings — force current input state to resend to the new port.
    // Fixes "Wii controls stop after player select" and similar port-rebinding issues.
    resyncInput: () => {
      last = null;
    },
    // Toggle the left-stick→d-pad fold live (mapping panel). Null the dedupe so the next pump resends
    // true state — otherwise a d-pad bit that the fold was adding stays "held" on the worker until the
    // stick next crosses the deadzone.
    setStickFold: (on) => { foldStickToDpad = !!on; last = null; },
    // Mirror right-stick left/right live. Null the dedupe for the same reason as the fold: a stick
    // held off-centre while the toggle flips produces no NEW change to send, so the worker would keep
    // the pre-flip deflection until the stick next moves.
    setSwapRightStickX: (on) => { swapRightStickX = !!on; last = null; },
    // Rebuild the chord watcher from the current custom binds (controller tool "Quick actions" rebind),
    // so a changed chord fires immediately without restarting the room. No-op for non-primary sessions.
    reloadChords: () => { if (onChordAction) chordWatcher = createChordWatcher(onChordAction, resolveChords(customChordBinds)); },
    // ── Input tape (inputTape.js) ─────────────────────────────────────────────────────────────
    // Record what this seat sends, so a run can be replayed later. `meta` is provenance written into
    // the tape header (game/system/room/anchor). trace=false skips the per-frame video thumbprint
    // (cheaper, but then a replay has nothing to be diffed against — keep it on unless measuring).
    startTapeRecording: (meta, o) => {
      if (spectator) return false; // a watcher sends nothing; a tape of it would be empty by construction
      tape = createInputTape(meta, o);
      startVideoClock(!o || o.trace !== false);
      // Null the dedupe so the tape OPENS with this seat's full current state rather than inheriting
      // an unrecorded held button — a replay must not start from a pad state it was never told about.
      last = null;
      return true;
    },
    stopTapeRecording: (extraMeta) => {
      if (!tape) return null;
      const json = tape.toJSON(extraMeta);
      tape = null;
      stopVideoClock();
      return json;
    },
    /** Add to the tape header after the fact (a title that had to be fetched, an anchor decision). */
    setTapeMeta: (patch) => { if (tape && patch) Object.assign(tape.meta, patch); },
    isTapeRecording: () => !!tape,
    tapeCounts: () => (tape ? tape.counts() : null),
    /** Drop a labelled mark into the tape at the current moment ("the bug happened HERE"). */
    markTape: (label) => { if (tape) tape.control("mark", String(label || ""), clockStamp()); },
    /**
     * Arm replay: from now on the pad frames come from `tapeJson`, not from the hardware. Returns the
     * player so the caller can watch progress and pump dueControls (save-state actions are the
     * caller's to dispatch — the client has no opinion about whether a quickLoad is allowed here).
     * Pass null to disarm; the seat returns to the live controls with a forced resync.
     */
    armTapeReplay: (tapeJson, o) => {
      if (spectator || !tapeJson) { replaySource = null; last = null; stopVideoClock(); return null; }
      const player = createTapePlayer(tapeJson, o);
      replayUsesWallClock = player.mode === "wall";
      replayT0Media = null;
      replayT0Wall = Date.now();
      replaySource = (t) => player.frameAt(t);
      startVideoClock(false);
      last = null; // first pump resends true (neutral) state, clearing anything the human left held
      return player;
    },
    disarmTapeReplay: () => { replaySource = null; replayT0Media = null; last = null; stopVideoClock(); },
    isTapeReplaying: () => !!replaySource,
    /** ms since the replay was armed, on whichever clock the replay is using. */
    replayClockMs: () => (replaySource ? replayClockMs() : 0),
    save: asPlayer(() => { if (tape) tape.control("quickSave", null, clockStamp()); return send(T.GAME_SAVE, {}); }),
    load: asPlayer(() => { if (tape) tape.control("quickLoad", null, clockStamp()); return send(T.GAME_LOAD, {}); }),
    // room_id is REQUIRED on reset/fast-forward/rewind: the coordinator's user handlers guard
    // these with `rq.Rid == worker.RoomId` (unlike save/load, which ignore Rid). Reset used to
    // send {} — which that guard silently no-op'd; carrying the id is the fix.
    reset: asPlayer(() => { if (tape) tape.control("reset", null, clockStamp()); return send(T.GAME_RESET, { room_id: roomIdFromWsUrl(descriptor.wsUrl) }); }),
    // Hold-to-engage time controls. The chord watcher calls these with true on engage and false
    // on release; the on-screen buttons use pointerdown/up the same way.
    fastForward: asPlayer((on) => { if (tape) tape.control("fastForward", !!on, clockStamp()); return send(T.GAME_FAST_FORWARD, { active: !!on, room_id: roomIdFromWsUrl(descriptor.wsUrl) }); }),
    rewind: asPlayer((on) => { if (tape) tape.control("rewind", !!on, clockStamp()); return send(T.GAME_REWIND, { active: !!on, room_id: roomIdFromWsUrl(descriptor.wsUrl) }); }),
    // Multi-disc: ask the emulator to swap to disc image `index` (patch 0005). No-op until the "disc"
    // channel is open / for single-disc games.
    swapDisc: asPlayer((index) => {
      if (tape) tape.control("swapDisc", index | 0, clockStamp());
      try {
        if (discDc && discDc.readyState === "open") discDc.send(new Uint8Array([index & 0xff]));
      } catch { /* channel closing */ }
    }),
  };
}

// The bound CloudRetro room id is carried in the gateway WS URL's room_id query (URL-encoded).
function roomIdFromWsUrl(wsUrl) {
  try {
    const q = wsUrl.indexOf("?");
    if (q < 0) return "";
    const params = new URLSearchParams(wsUrl.slice(q + 1));
    return params.get("room_id") || "";
  } catch { return ""; }
}

// A non-negative integer query param off the gateway WS URL (arcade per-room bitrate/FEC: vbr, fec).
function numFromWsUrl(wsUrl, key) {
  try {
    const q = wsUrl.indexOf("?");
    if (q < 0) return 0;
    const v = parseInt(new URLSearchParams(wsUrl.slice(q + 1)).get(key) || "0", 10);
    return Number.isFinite(v) && v > 0 ? v : 0;
  } catch { return 0; }
}

// A string query param off the gateway WS URL (per-room codec: ?codec=av1|h264, worker patch 0036).
function strFromWsUrl(wsUrl, key) {
  try {
    const q = wsUrl.indexOf("?");
    if (q < 0) return "";
    return new URLSearchParams(wsUrl.slice(q + 1)).get(key) || "";
  } catch { return ""; }
}
