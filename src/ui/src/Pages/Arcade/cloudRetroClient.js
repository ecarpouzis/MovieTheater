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
  APP_VIDEO_CHANGE: 150,
};

// Button → bit positions, CONFIRMED against CloudRetro's JOYPAD_KEYS order (web/js/input/keys.js):
// [B, Y, SELECT, START, UP, DOWN, LEFT, RIGHT, A, X, L, R, L2, R2, L3, R3] — the standard RetroPad order.
const PAD = { B: 0, Y: 1, SELECT: 2, START: 3, UP: 4, DOWN: 5, LEFT: 6, RIGHT: 7, A: 8, X: 9, L: 10, R: 11, L2: 12, R2: 13, L3: 14, R3: 15 };

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

// Keyboard fallback → RetroPad.
const DEFAULT_KEYMAP = {
  ArrowUp: PAD.UP, ArrowDown: PAD.DOWN, ArrowLeft: PAD.LEFT, ArrowRight: PAD.RIGHT,
  KeyZ: PAD.B, KeyX: PAD.A, KeyA: PAD.Y, KeyS: PAD.X,
  Enter: PAD.START, ShiftRight: PAD.SELECT, ShiftLeft: PAD.SELECT,
  KeyQ: PAD.L, KeyW: PAD.R,
};

const PROFILES = {
  default: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: DEFAULT_KEYMAP,
    hint: "Gamepad recommended. Keyboard: arrows = move, Z X A S = buttons, Q W = L/R, Enter = Start, Shift = Select.",
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
    hint: "Gamepad recommended (right stick = C-buttons). Keyboard: arrows = steer/move, X = A (accelerate), Z = B, I J K L = C-buttons, E = Z, Q W = L/R, Enter = Start.",
  },
  // PSP: ppsspp maps PSP Cross ← RetroPad B, Circle ← A, Square ← Y, Triangle ← X — so the DEFAULT
  // positional map is already correct (south → Cross/confirm). Analog nub = left stick; L/R are the
  // shoulder buttons. Just a tailored hint + Q/W on the shoulders.
  psp: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: { ...DEFAULT_KEYMAP },
    hint: "Gamepad recommended (left stick = analog nub). Keyboard: arrows = D-pad, Z = Cross, X = Circle, A = Square, S = Triangle, Q W = L/R, Enter = Start, Shift = Select.",
  },
  // PS1: pcsx_rearmed maps PS Cross ← RetroPad B, Circle ← A, Square ← Y, Triangle ← X — so the DEFAULT
  // positional map is already correct (south → Cross/confirm). With the DualShock pad type (config.yaml),
  // both analog sticks ride the frame (encodeInput lx/ly/rx/ry) — the left stick drives games like Ape
  // Escape that demand an analog controller; L1/L2/R1/R2 are the shoulders/triggers. Just a tailored hint.
  ps1: {
    gamepad: DEFAULT_GAMEPAD,
    keymap: {
      ...DEFAULT_KEYMAP,
      KeyQ: PAD.L, KeyW: PAD.R, KeyA: PAD.L2, KeyS: PAD.R2, // L1/R1 shoulders, L2/R2 triggers
    },
    hint: "Gamepad recommended (both sticks work — DualShock). Keyboard: arrows = D-pad/left stick, Z = Cross, X = Circle, Q W = L1/R1, A S = L2/R2, Enter = Start, Shift = Select.",
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
    hint: "Gamepad recommended (right stick = C-stick; triggers = L/R). Keyboard: arrows = move, X = A (confirm), Z = B, I J K L = C-stick, Q W = L/R triggers, E = Z, Enter = Start.",
  },
};

function profileFor(system) {
  return PROFILES[(system || "").toLowerCase()] || PROFILES.default;
}

// ── Local multiplayer: pad ownership across sessions ─────────────────────────────────────────────
// One browser can hold SEVERAL CloudRetro connections (the primary + one input-only session per extra
// local controller — the wire protocol routes input by connection, so an extra pad needs an extra
// connection). Each extra session is PINNED to one Gamepad-API index; this registry is how the primary
// session's adopt-any-active-pad heuristic knows to leave those pads alone.
const claimedPadIndexes = new Set();

/**
 * One poll pass looking for "the new player pressed a button": returns the index of a connected pad
 * with any button currently pressed that is neither claimed by a local-player session nor in
 * `excludeIndexes` (the caller passes the primary's current pad), or -1. The room page polls this
 * after "Add local player" so the new controller identifies itself the way consoles do.
 */
export function findNewPad(excludeIndexes = []) {
  const pads = navigator.getGamepads ? navigator.getGamepads() : [];
  for (const gp of pads) {
    if (!gp || claimedPadIndexes.has(gp.index) || excludeIndexes.includes(gp.index)) continue;
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
  const { videoEl, onRoomId, onStatus, onError, onSeat, onAspect } = opts || {};
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

  let ws = null;
  let pc = null;
  let dc = null;
  let discDc = null; // patch 0005: worker-created "disc" channel; the browser sends a target disc index
  let inputTimer = null;
  let closed = false;
  const inboundStream = new MediaStream(); // audio + video tracks accumulate here (see ontrack)
  let lastAv = null; // last known video geometry (flip/rotation), re-applied whenever the track attaches
  let iceServers = (descriptor.iceConfig || []).map((s) => ({ urls: s.urls }));

  // ── Audio de-contention knobs (docs/arcade-audio-nextsteps.md) ──────────────────────────────────
  // The residual audio hitch: on the bundled transport a burst of video RTP head-of-line-blocks the
  // tiny opus packets, so audio arrives late/bursty and Chrome's NetEq over-buffers toward ~260ms and
  // TIME-STRETCHES (~8%) — the warble the user hears. Two levers, both tunable via localStorage so a
  // real browser (the only place smoothness can be judged) can A/B them live:
  //  • arcade.audioJitterMs (default 80): give NetEq a small STABLE audio target so it stops adaptively
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
  const AUDIO_JITTER_MS = (() => {
    try { const v = parseInt(localStorage.getItem("arcade.audioJitterMs"), 10); return Number.isFinite(v) && v >= 0 ? v : 80; }
    catch { return 80; }
  })();
  const AUDIO_PC = (() => { try { return localStorage.getItem("arcade.audioPC") !== "0"; } catch { return true; } })();

  // Input profile for this game's system (button layout + keyboard map + optional right-stick keys).
  const profile = profileFor(descriptor.system);
  const keymap = profile.keymap;
  const gamepad = profile.gamepad;
  const rstickKeys = profile.rstick; // key→right-stick direction, or undefined

  // Live input state.
  const keyMask = { value: 0 };
  // Right-stick direction held via the keyboard (N64 C-buttons), when the profile maps them.
  const rKeys = { up: false, down: false, left: false, right: false };
  const onKey = (down) => (e) => {
    const bit = keymap[e.code];
    if (bit !== undefined) {
      e.preventDefault();
      if (down) keyMask.value |= (1 << bit);
      else keyMask.value &= ~(1 << bit);
      return;
    }
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
  const padActive = (gp) =>
    gp.buttons.some((b) => b.pressed) || gp.axes.some((a) => Math.abs(a) > 0.2);

  function readGamepad() {
    const pads = navigator.getGamepads ? navigator.getGamepads() : [];
    const mask0 = { mask: 0, axes: [0, 0, 0, 0] };
    let gp;
    if (pinnedPad >= 0) {
      // Pinned to one physical pad — never adopt another. If Chrome re-enumerates the pad away
      // (Bluetooth sleep), send neutral until it returns at the same index.
      gp = pads[pinnedPad] || null;
    } else if (inputOnly) {
      // A local seat whose pad was reassigned away: neutral until the panel gives it another.
      gp = null;
    } else {
      // Never adopt a pad a local-player session has claimed — without this the primary and the
      // extra seat would both forward the same physical controller.
      const free = (p) => p && !claimedPadIndexes.has(p.index);
      gp = activePadIndex >= 0 && !claimedPadIndexes.has(activePadIndex) ? pads[activePadIndex] : null;
      if (!gp || (!padActive(gp) && Array.prototype.some.call(pads, (p) => free(p) && p !== gp && padActive(p)))) {
        gp = Array.prototype.find.call(pads, (p) => free(p) && padActive(p)) || gp
          || Array.prototype.find.call(pads, (p) => free(p)) || null;
      }
    }
    if (!gp) return mask0;
    activePadIndex = gp.index;
    if (!inputOnly && padActive(gp)) lastPadActiveAt = Date.now();

    let mask = 0;
    const axes = [0, 0, 0, 0];
    gp.buttons.forEach((b, i) => {
      if (b.pressed && gamepad[i] !== undefined) mask |= (1 << gamepad[i]);
    });
    // Real analog axes ride the frame (N64 steering wants them); a left-stick→dpad fold is kept
    // so analog-only pads still drive pure-dpad 2D cores. (Dpad+stick doubling is harmless: cores
    // map them to different inputs.)
    for (let i = 0; i < 4 && i < gp.axes.length; i++) axes[i] = axisToInt16(gp.axes[i]);
    const [ax, ay] = gp.axes;
    if (ax < -0.5) mask |= (1 << PAD.LEFT); else if (ax > 0.5) mask |= (1 << PAD.RIGHT);
    if (ay < -0.5) mask |= (1 << PAD.UP); else if (ay > 0.5) mask |= (1 << PAD.DOWN);
    return { mask, axes };
  }

  // Send on change only, like the stock client (dirty flag over the whole 5-int16 frame).
  let last = null;
  function pumpInput() {
    if (closed || !dc || dc.readyState !== "open") return;
    const gp = readGamepad();
    const mask = keyMask.value | gp.mask;
    // Keyboard arrows also drive the LEFT ANALOG STICK. N64 (and most 3D) games steer with the stick,
    // NOT the d-pad — so without this the keyboard couldn't turn. Full deflection from the arrow keys;
    // a real gamepad stick takes precedence when it's being pushed.
    let ax = gp.axes[0], ay = gp.axes[1];
    if (!ax) ax = (keyMask.value & (1 << PAD.LEFT)) ? -32767 : (keyMask.value & (1 << PAD.RIGHT)) ? 32767 : 0;
    if (!ay) ay = (keyMask.value & (1 << PAD.UP)) ? -32767 : (keyMask.value & (1 << PAD.DOWN)) ? 32767 : 0;
    // Right stick from the keyboard when the profile maps it (N64 C-buttons); the pad stick wins.
    let rx = gp.axes[2], ry = gp.axes[3];
    if (rstickKeys) {
      if (!rx) rx = rKeys.left ? -32767 : rKeys.right ? 32767 : 0;
      if (!ry) ry = rKeys.up ? -32767 : rKeys.down ? 32767 : 0;
    }
    const a = [ax, ay, rx, ry];
    if (last && last[0] === mask && last[1] === a[0] && last[2] === a[1] && last[3] === a[2] && last[4] === a[3])
      return;
    last = [mask, a[0], a[1], a[2], a[3]];
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
    // An input-only local-player session drives its pinned pad only — the PRIMARY session owns the
    // keyboard, and wiring it here too would send every keystroke to two seats at once.
    if (!inputOnly) {
      window.addEventListener("keydown", keyDown);
      window.addEventListener("keyup", keyUp);
    }
    window.addEventListener("blur", onWindowBlur);
    window.addEventListener("focus", onWindowFocus);
    // Poll at ~60 Hz; only actually send on change (pumpInput dedupes).
    inputTimer = setInterval(pumpInput, 16);
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
    dc.onopen = () => { status("connected"); startInput(); };
    // A negotiated channel cannot reopen, and pumpInput's readyState guard just returns — so without
    // this the death of the input channel is INVISIBLE: media keeps flowing (audio even rides its own
    // aux PC), the status stays "playing", and the player is stuck in a room they can't control
    // (observed live 2026-07-09 after an alt-tab). Report it so the page can offer/perform recovery.
    // Spectators hold no input path, so a dc close means nothing to their session.
    dc.onclose = () => { if (!closed && !spectator) { stopInput(); status("input-lost"); } };

    // The worker opens side channels non-negotiated (keyboard/mouse/disc); we receive them here. The
    // "disc" channel carries a 1-byte target image index for multi-disc games (patch 0005).
    pc.ondatachannel = (ev) => {
      if (ev.channel && ev.channel.label === "disc") {
        discDc = ev.channel;
        discDc.binaryType = "arraybuffer";
      }
    };

    pc.ontrack = onInboundTrack;
    pc.onicecandidate = (e) => {
      if (e.candidate) send(T.SIGNAL, { ice: JSON.stringify(e.candidate) });
    };
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === "failed" || pc.connectionState === "disconnected")
        status(pc.connectionState);
    };

    // Kick off: we are not the offerer — the server sends the SDP offer. sdp:"audio-pc" asks a
    // patch-0020 worker to put opus on a dedicated aux PeerConnection (ignored by older workers —
    // the worker only reads init sdp when initiator is true, so audio then just rides this PC).
    // An input-only local-player session plays nothing, so it never asks for the aux audio PC.
    send(T.INIT_WEBRTC, AUDIO_PC && !inputOnly ? { initiator: false, sdp: "audio-pc" } : { initiator: false });
  }

  // Shared by BOTH PeerConnections. Cloud gaming wants minimal receive buffering. Chrome's ADAPTIVE
  // jitter buffer is tiny for video (~8ms measured) but the AUDIO buffer grows unbounded (24→77ms in
  // 30s, from encoder clock drift) — and the browser lip-syncs video playout to audio, so the whole
  // stream drifts later the longer you play. Pin both receivers to the minimum. (jitterBufferTarget is
  // the standard; playoutDelayHint is the legacy Chrome name — set both, harmless where unknown.)
  // Roadmap WS-A.4: N64 was fine at 0, but cross-network multiplayer (4-player, mixed LAN+remote)
  // hits the exact "reopen" condition noted here — the send-path bursts make NetEq inflate + stretch.
  // Give AUDIO a small stable target (arcade.audioJitterMs, default 80ms) to absorb the bursts; keep
  // VIDEO at 0 so it stays responsive (separate stream ids → video never lip-sync-waits on audio).
  // CloudRetro (Pion) sends audio and video as SEPARATE m-lines/streams (and with arcade.audioPC,
  // separate PeerConnections) — collect EVERY inbound track into ONE MediaStream so the element
  // plays both; per-track srcObject assignment left the last track orphaning the first.
  function onInboundTrack(e) {
    const jbMs = e.track && e.track.kind === "audio" ? AUDIO_JITTER_MS : 0;
    try { e.receiver.jitterBufferTarget = jbMs; } catch { /* older browsers */ }
    try { e.receiver.playoutDelayHint = jbMs / 1000; } catch { /* non-Chrome */ }
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
    // Appendix A1/A2: signal values are JSON-stringified.
    const desc = JSON.parse(sdpString);
    await pc.setRemoteDescription(desc);
    remoteReady = true;
    while (pendingCandidates.length) {
      try { await pc.addIceCandidate(pendingCandidates.shift()); } catch (err) { onError && onError(err); }
    }
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
    if (vbr > 0) p.video_bitrate = vbr;
    if (fec > 0) p.audio_fec = fec;
    // In-frame packet pacing window ms (worker patch 0028) — the lobby Network profile's opt-in
    // smoother for Remote/5G rooms. Absent/0 = LAN default, wire-speed bursts.
    if (pace > 0) p.pace = pace;
    // Per-room cheats the creator picked in the lobby. Unlike vbr/fec these ride the descriptor body, not
    // the wsUrl query: a cheat code list runs to kilobytes. The worker merges core_options before the ROM
    // loads and feeds cheats to retro_cheat_set after (patch 0027). Only the creator's t=104 builds the
    // room, so a joiner's descriptor carries neither.
    if (descriptor.coreOptions && Object.keys(descriptor.coreOptions).length > 0) p.core_options = descriptor.coreOptions;
    if (Array.isArray(descriptor.cheats) && descriptor.cheats.length > 0) p.cheats = descriptor.cheats;
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

  function onGameStarted(p) {
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
        if (data.p && Array.isArray(data.p.ice) && data.p.ice.length)
          iceServers = data.p.ice.map((s) => ({ urls: s.urls, username: s.username, credential: s.credential }));
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
      default:
        // t=3 latency, 2xx internal — ignored in v1.
        break;
    }
  }

  function send(t, p, id) {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(packet(t, p, id));
  }

  function connect() {
    status("connecting");
    ws = new WebSocket(descriptor.wsUrl);
    ws.onopen = () => { status("signalling"); };
    ws.onmessage = (e) => handle(e.data);
    // Don't cry "connection failed" when WE closed it (session teardown / React StrictMode's throwaway
    // first mount) — only a genuine, still-open failure should surface to the user.
    ws.onerror = () => { if (!closed) onError && onError(new Error("Signaling connection failed.")); };
    ws.onclose = () => { if (!closed) status("disconnected"); };
    // The INIT (t=4) arrives right after open and drives setupPeer(); GAME_START fires once the
    // DataChannel opens. Some builds send t=4 only after the WS is fully up, so also arm a fallback:
    // if the channel opens we start the game.
    const armStart = setInterval(() => {
      if (closed) { clearInterval(armStart); return; }
      if (dc && dc.readyState === "open") { clearInterval(armStart); startGame(); }
    }, 100);
  }

  function close() {
    if (closed) return;
    closed = true;
    status("closed");
    stopInput();
    if (pinnedPad >= 0) claimedPadIndexes.delete(pinnedPad);
    try { send(T.GAME_QUIT, { room_id: roomIdFromWsUrl(descriptor.wsUrl) }); } catch { /* */ }
    try { dc && dc.close(); } catch { /* */ }
    try { discDc && discDc.close(); } catch { /* */ }
    try { pc && pc.close(); } catch { /* */ }
    try { apc && apc.close(); } catch { /* */ }
    try { ws && ws.close(); } catch { /* */ }
    if (videoEl) videoEl.srcObject = null;
  }

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
    // Re-pin this seat to another physical pad (Controllers panel). null/undefined = unassign: a
    // local seat then sends neutral; the primary falls back to fluid adopt-any-unclaimed-pad.
    setPad: (idx) => {
      const next = Number.isInteger(idx) && idx >= 0 ? idx : -1;
      if (next === pinnedPad) return;
      if (pinnedPad >= 0) claimedPadIndexes.delete(pinnedPad);
      pinnedPad = next;
      if (pinnedPad >= 0) claimedPadIndexes.add(pinnedPad);
      // Null the dedupe so the next pump resends true state — the old pad's held buttons release
      // on the worker instead of riding this seat forever.
      last = null;
    },
    save: asPlayer(() => send(T.GAME_SAVE, {})),
    load: asPlayer(() => send(T.GAME_LOAD, {})),
    reset: asPlayer(() => send(T.GAME_RESET, {})),
    // Multi-disc: ask the emulator to swap to disc image `index` (patch 0005). No-op until the "disc"
    // channel is open / for single-disc games.
    swapDisc: asPlayer((index) => {
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
