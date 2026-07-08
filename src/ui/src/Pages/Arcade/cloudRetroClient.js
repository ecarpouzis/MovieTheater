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
 * @param descriptor { wsUrl, gameKey, playerSlot, iceConfig, isCreator, roomCode, system }
 * @param opts { videoEl, onRoomId(cloudRetroRoomId), onStatus(str), onError(err), onSeat(index) }
 * @returns { close, save, load, reset }
 */
export function createCloudRetroSession(descriptor, opts) {
  const { videoEl, onRoomId, onStatus, onError, onSeat } = opts || {};
  const status = (s) => onStatus && onStatus(s);

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
  //  • arcade.noBundle (default OFF — OPT-IN): give audio its OWN transport so video bursts can't
  //    head-of-line-block it. Two REQUIRED halves, learned the hard way: (a) construct THIS peer with
  //    bundlePolicy "max-compat" so Chrome actually allocates a separate transport per m-line, AND
  //    (b) strip a=group:BUNDLE from our answer so the two aren't collapsed. Stripping WITHOUT (a) leaves
  //    the audio m-line with no transport of its own → the connection hangs at "Negotiating" forever
  //    (that's exactly what default-on caused). Off by default: the jitter buffer (below) + lower bitrate
  //    already smooth the audio on the safe bundled path. Enable to test the deep fix: arcade.noBundle="1".
  const AUDIO_JITTER_MS = (() => {
    try { const v = parseInt(localStorage.getItem("arcade.audioJitterMs"), 10); return Number.isFinite(v) && v >= 0 ? v : 80; }
    catch { return 80; }
  })();
  const NO_BUNDLE = (() => { try { return localStorage.getItem("arcade.noBundle") === "1"; } catch { return false; } })();

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
  const padActive = (gp) =>
    gp.buttons.some((b) => b.pressed) || gp.axes.some((a) => Math.abs(a) > 0.2);

  function readGamepad() {
    const pads = navigator.getGamepads ? navigator.getGamepads() : [];
    let gp = activePadIndex >= 0 ? pads[activePadIndex] : null;
    if (!gp || (!padActive(gp) && Array.prototype.some.call(pads, (p) => p && p !== gp && padActive(p)))) {
      gp = Array.prototype.find.call(pads, (p) => p && padActive(p)) || gp
        || Array.prototype.find.call(pads, (p) => p) || null;
    }
    const mask0 = { mask: 0, axes: [0, 0, 0, 0] };
    if (!gp) return mask0;
    activePadIndex = gp.index;

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

  function startInput() {
    window.addEventListener("keydown", keyDown);
    window.addEventListener("keyup", keyUp);
    // Poll at ~60 Hz; only actually send on change (pumpInput dedupes).
    inputTimer = setInterval(pumpInput, 16);
  }

  function stopInput() {
    window.removeEventListener("keydown", keyDown);
    window.removeEventListener("keyup", keyUp);
    if (inputTimer) clearInterval(inputTimer);
    inputTimer = null;
  }

  function setupPeer() {
    // max-compat only when un-bundling (opt-in): it makes Chrome allocate a transport per m-line, which
    // the answer's BUNDLE-strip then keeps separate. Required — see the NO_BUNDLE note above.
    pc = new RTCPeerConnection(NO_BUNDLE ? { iceServers, bundlePolicy: "max-compat" } : { iceServers });

    // The joypad channel is pre-negotiated — label/id/flags must match the server EXACTLY (Appendix A4)
    // or it silently never opens.
    dc = pc.createDataChannel("data", { negotiated: true, id: 0, ordered: false, maxRetransmits: 0 });
    dc.binaryType = "arraybuffer";
    dc.onopen = () => { status("connected"); startInput(); };

    // The worker opens side channels non-negotiated (keyboard/mouse/disc); we receive them here. The
    // "disc" channel carries a 1-byte target image index for multi-disc games (patch 0005).
    pc.ondatachannel = (ev) => {
      if (ev.channel && ev.channel.label === "disc") {
        discDc = ev.channel;
        discDc.binaryType = "arraybuffer";
      }
    };

    // CloudRetro (Pion) sends audio and video as SEPARATE m-lines/streams. Assigning
    // videoEl.srcObject = e.streams[0] per track left the <video> holding only the LAST track's
    // stream — audio arrives last, so you'd get sound but a black frame (the video track orphaned).
    // Collect EVERY inbound track into one MediaStream so the element plays both.
    pc.ontrack = (e) => {
      // Cloud gaming wants minimal receive buffering. Chrome's ADAPTIVE jitter buffer is tiny for
      // video (~8ms measured) but the AUDIO buffer grows unbounded (24→77ms in 30s, from encoder
      // clock drift) — and the browser lip-syncs video playout to audio, so the whole stream drifts
      // later the longer you play. Pin both receivers to the minimum. (jitterBufferTarget is the
      // standard; playoutDelayHint is the legacy Chrome name — set both, harmless where unknown.)
      // Roadmap WS-A.4: N64 was fine at 0, but cross-network multiplayer (4-player, mixed LAN+remote)
      // hits the exact "reopen" condition noted here — the send-path bursts make NetEq inflate + stretch.
      // Give AUDIO a small stable target (arcade.audioJitterMs, default 80ms) to absorb the bursts; keep
      // VIDEO at 0 so it stays responsive (separate stream ids → video never lip-sync-waits on audio).
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
    };
    pc.onicecandidate = (e) => {
      if (e.candidate) send(T.SIGNAL, { ice: JSON.stringify(e.candidate) });
    };
    pc.onconnectionstatechange = () => {
      if (pc.connectionState === "failed" || pc.connectionState === "disconnected")
        status(pc.connectionState);
    };

    // Kick off: we are not the offerer — the server sends the SDP offer.
    send(T.INIT_WEBRTC, { initiator: false });
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
    // Opt-in un-bundle: drop the BUNDLE group from our answer so audio + video negotiate SEPARATE
    // transports (the worker offers per-m-line transports via BundlePolicyMaxCompat). Removes the
    // video-burst → audio head-of-line blocking entirely. Off by default (SDP munging is delicate;
    // verify on a real session before relying on it).
    if (NO_BUNDLE && answer.sdp) {
      answer.sdp = answer.sdp.replace(/^a=group:BUNDLE[^\r\n]*\r?\n/im, "");
    }
    await pc.setLocalDescription(answer);
    send(T.SIGNAL, { sdp: JSON.stringify(pc.localDescription) });
  }

  async function onSignal(p) {
    try {
      if (p.sdp) await onSdp(p.sdp);
      if (p.ice) await addCandidate(p.ice);
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
    const p = { game_name: descriptor.gameKey, room_id: roomId, player_index: descriptor.playerSlot | 0 };
    const vbr = numFromWsUrl(descriptor.wsUrl, "vbr");
    const fec = numFromWsUrl(descriptor.wsUrl, "fec");
    if (vbr > 0) p.video_bitrate = vbr;
    if (fec > 0) p.audio_fec = fec;
    send(T.GAME_START, p);
  }

  // GL cores (e.g. N64/gliden64) render bottom-left-origin, so CloudRetro flags the frame flipped
  // (and may report a rotation). It sends the geometry in the GAME_START response's `av` and in any
  // later t=150 AppVideoChange. Mirror the stock client: flip → scaleY(-1), rot → rotate(-Ndeg).
  function applyVideoTransform(av) {
    if (av) lastAv = av;
    if (!videoEl || !lastAv) return;
    const rot = lastAv.rot ? `rotate(${-lastAv.rot}deg)` : "";
    const flip = lastAv.flip ? "scaleY(-1)" : "";
    videoEl.style.transform = [rot, flip].filter(Boolean).join(" ");
  }

  function onGameStarted(p) {
    const roomId = p && (p.roomId || p.room_id);
    if (descriptor.isCreator && roomId) onRoomId && onRoomId(roomId);
    if (p && p.av) applyVideoTransform(p.av);
    // Confirm the seat; the worker answers the accepted index (-1 = rejected).
    send(T.SET_PLAYER_INDEX, descriptor.playerSlot | 0);
    status("playing");
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
    try { send(T.GAME_QUIT, { room_id: roomIdFromWsUrl(descriptor.wsUrl) }); } catch { /* */ }
    try { dc && dc.close(); } catch { /* */ }
    try { discDc && discDc.close(); } catch { /* */ }
    try { pc && pc.close(); } catch { /* */ }
    try { ws && ws.close(); } catch { /* */ }
    if (videoEl) videoEl.srcObject = null;
  }

  connect();

  return {
    close,
    save: () => send(T.GAME_SAVE, {}),
    load: () => send(T.GAME_LOAD, {}),
    reset: () => send(T.GAME_RESET, {}),
    // Multi-disc: ask the emulator to swap to disc image `index` (patch 0005). No-op until the "disc"
    // channel is open / for single-disc games.
    swapDisc: (index) => {
      try {
        if (discDc && discDc.readyState === "open") discDc.send(new Uint8Array([index & 0xff]));
      } catch { /* channel closing */ }
    },
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
