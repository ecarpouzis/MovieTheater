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
  let inputTimer = null;
  let closed = false;
  const inboundStream = new MediaStream(); // audio + video tracks accumulate here (see ontrack)
  let lastAv = null; // last known video geometry (flip/rotation), re-applied whenever the track attaches
  let iceServers = (descriptor.iceConfig || []).map((s) => ({ urls: s.urls }));

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

  function readGamepad() {
    const pads = navigator.getGamepads ? navigator.getGamepads() : [];
    let mask = 0;
    const axes = [0, 0, 0, 0];
    for (const gp of pads) {
      if (!gp) continue;
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
      break; // one local pad drives our seat
    }
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
    pc = new RTCPeerConnection({ iceServers });

    // The joypad channel is pre-negotiated — label/id/flags must match the server EXACTLY (Appendix A4)
    // or it silently never opens.
    dc = pc.createDataChannel("data", { negotiated: true, id: 0, ordered: false, maxRetransmits: 0 });
    dc.binaryType = "arraybuffer";
    dc.onopen = () => { status("connected"); startInput(); };

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
      try { e.receiver.jitterBufferTarget = 0; } catch { /* older browsers */ }
      try { e.receiver.playoutDelayHint = 0; } catch { /* non-Chrome */ }
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
    // Creator: empty room_id ⇒ create on a free worker. Joiner: the bound id ⇒ join that worker.
    const roomId = descriptor.isCreator ? "" : roomIdFromWsUrl(descriptor.wsUrl);
    send(T.GAME_START, { game_name: descriptor.gameKey, room_id: roomId, player_index: descriptor.playerSlot | 0 });
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
