import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";
import { createCloudRetroSession, findNewPad, setIgnoreStreamedPads, isStreamedPad, livePads, isPhantomPad } from "./cloudRetroClient";

// Local multiplayer (extra controllers on one machine): each extra pad gets its own INPUT-ONLY
// CloudRetro session pinned to that pad, because the wire protocol routes input by CONNECTION —
// there is no player-index byte in the frame. These tests protect the three guarantees that make
// that safe:
//   1. a pinned session claims its pad, so the primary's adopt-any-active-pad heuristic (and the
//      "press a button" detector) leave it alone;
//   2. a pinned session reads EXACTLY its pad — never the keyboard, never another pad;
//   3. it joins like a normal second player (t=108 with its seat) but never asks for the aux
//      audio PeerConnection (it renders nothing).

const T = { INIT: 4, INIT_WEBRTC: 100, GAME_START: 104, SET_PLAYER_INDEX: 108 };

let sockets;
let channels;

class FakeDataChannel {
  constructor(label) {
    this.label = label;
    this.readyState = "open";
    this.sent = [];
    channels.push(this);
  }
  send(data) { this.sent.push(data); }
  close() { this.readyState = "closed"; }
}

class FakePeerConnection {
  constructor() { this.tracks = []; }
  createDataChannel(label) { return new FakeDataChannel(label); }
  addEventListener() {}
  setRemoteDescription() { return Promise.resolve(); }
  setLocalDescription() { return Promise.resolve(); }
  createAnswer() { return Promise.resolve({ type: "answer", sdp: "" }); }
  addIceCandidate() { return Promise.resolve(); }
  getReceivers() { return []; }
  close() {}
}

class FakeWebSocket {
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;

  constructor(url) {
    this.url = url;
    this.readyState = 1;
    this.sent = [];
    sockets.push(this);
    setTimeout(() => this.onopen && this.onopen(), 0);
  }
  send(raw) { this.sent.push(JSON.parse(raw)); }
  close() { this.readyState = 3; }
  types() { return this.sent.map((m) => m.t); }
}

const descriptorFor = (over = {}) => ({
  wsUrl: "ws://gateway.test/w/token?room_id=r___Game",
  gameKey: "Game", playerSlot: 1, isCreator: false, roomCode: "AAA", system: "snes",
  iceConfig: [], ...over,
});

// A minimal Gamepad-API pad. `pressed` lists button indexes currently held.
const pad = (index, pressed = []) => ({
  index,
  buttons: Array.from({ length: 16 }, (_, i) => ({ pressed: pressed.includes(i) })),
  axes: [0, 0, 0, 0],
});
// An XInput pad as Chrome reports it — what a Moonlight guest's ViGEm virtual pad (or a real Xbox
// controller) looks like. The streamed-pad guard keys off exactly this id.
const xpad = (index, pressed = []) => ({
  ...pad(index, pressed),
  id: "Xbox 360 Controller (XInput STANDARD GAMEPAD)",
});

// A pad with an identity and a sample timestamp — what phantom detection reads. Liveness is
// OBSERVED (does `timestamp` still advance?) and a stale entry is only condemned once a live twin of
// the same model exists, so both fields matter; a reconnect is modelled by leaving the old entry
// frozen and adding a second one with the same id at a new index.
const idPad = (index, { id = "DualSense Wireless Controller (STANDARD GAMEPAD)", ts = 0, pressed = [] } = {}) => ({
  ...pad(index, pressed), id, timestamp: ts, connected: true,
});

let padsNow;
const setPads = (...list) => {
  padsNow = [null, null, null, null];
  for (const p of list) padsNow[p.index] = p;
};

async function driveToGameStart(ws) {
  await vi.waitFor(() => expect(ws.onopen).toBeTruthy());
  ws.onmessage({ data: JSON.stringify({ t: T.INIT, p: { ice: [] } }) });
  await vi.advanceTimersByTimeAsync(150);
  ws.onmessage({ data: JSON.stringify({ t: T.GAME_START, p: { room_id: "r___Game" } }) });
  return ws;
}

describe("cloudRetroClient — local multiplayer input-only sessions", () => {
  beforeEach(() => {
    sockets = [];
    channels = [];
    vi.stubGlobal("WebSocket", FakeWebSocket);
    vi.stubGlobal("RTCPeerConnection", FakePeerConnection);
    vi.stubGlobal("MediaStream", class { addTrack() {} });
    setPads();
    vi.stubGlobal("navigator", { ...navigator, getGamepads: () => padsNow });
    vi.useFakeTimers({ shouldAdvanceTime: true });
    // The echo guard's "when did WE last send something" stamp is module-scope (one machine, many
    // sessions), so it outlives a test. Start every test outside that window.
    vi.advanceTimersByTime(600);
  });
  afterEach(() => { setIgnoreStreamedPads(false); vi.useRealTimers(); vi.unstubAllGlobals(); });

  it("findNewPad answers the pad with a pressed button, honouring exclusions", () => {
    setPads(pad(0), pad(1, [0]), pad(2));
    expect(findNewPad()).toBe(1);
    expect(findNewPad([1])).toBe(-1);
    setPads(pad(0), pad(1), pad(2));
    expect(findNewPad()).toBe(-1); // nothing pressed → nothing adopted
  });

  it("a pinned session claims its pad for as long as it lives", async () => {
    setPads(pad(1, [0]));
    const s = createCloudRetroSession(descriptorFor(), { padIndex: 1 });
    expect(findNewPad()).toBe(-1); // claimed — invisible to the new-player detector
    s.close();
    expect(findNewPad()).toBe(1);  // released with the session
  });

  it("a pinned session claims its seat with t=108 like any second player", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 2 }), { padIndex: 1 });
    const ws = await driveToGameStart(sockets[0]);
    const seat = ws.sent.find((m) => m.t === T.SET_PLAYER_INDEX);
    expect(seat.p).toBe(2);
    const start = ws.sent.find((m) => m.t === T.GAME_START);
    expect(start.p.player_index).toBe(2);
    s.close();
  });

  it("a pinned session marks itself input-only instead of asking for media", async () => {
    const s = createCloudRetroSession(descriptorFor(), { padIndex: 1 });
    const ws = await driveToGameStart(sockets[0]);
    const init = ws.sent.find((m) => m.t === T.INIT_WEBRTC);
    // The init sdp field carries the "input-only" marker (no media tracks, excluded from ABR —
    // the second-pad quality fix) rather than being left empty.
    expect(init.p.sdp).toBe("input-only");
    s.close();
  });

  it("a pinned session forwards ITS pad's buttons and ignores every other pad", async () => {
    setPads(pad(0, [9]), pad(1)); // someone hammering Start on pad 0; our pad 1 idle
    const s = createCloudRetroSession(descriptorFor(), { padIndex: 1 });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(100);
    // Only the initial all-zero frame — pad 0's activity must not leak into this seat.
    const masks = dc.sent.map((f) => new Int16Array(f)[0]);
    expect(masks.every((m) => m === 0)).toBe(true);

    setPads(pad(0, [9]), pad(1, [0])); // now OUR pad presses south
    await vi.advanceTimersByTimeAsync(100);
    const last = new Int16Array(dc.sent[dc.sent.length - 1])[0];
    expect(last).not.toBe(0);
    s.close();
  });

  it("adoption hold keeps the primary off a newly pressed pad (the quick-add race)", async () => {
    setPads(pad(0), pad(1));
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), { videoEl: null });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(50); // the primary latches idle pad 0 (fluid fallback)

    s.setAdoptionHeld(true);
    setPads(pad(0), pad(1, [0])); // the NEW player presses a button to identify their pad
    await vi.advanceTimersByTimeAsync(100);

    // Un-held, the primary's 16 ms poll adopts pad 1 here: its press rides seat 0 and the detector
    // excludes the very pad being pressed. Held, the pad stays visible and the seat stays quiet.
    expect(findNewPad([0])).toBe(1);
    const masks = dc.sent.map((f) => new Int16Array(f)[0]);
    expect(masks[masks.length - 1]).toBe(0);

    s.setAdoptionHeld(false);
    s.close();
  });

  it("setPad moves the pad claim between physical pads", async () => {
    setPads(pad(1), pad(2));
    const s = createCloudRetroSession(descriptorFor(), { padIndex: 1 });
    setPads(pad(1, [0]), pad(2, [0]));
    expect(findNewPad()).toBe(2); // 1 claimed, 2 free

    s.setPad(2);
    expect(findNewPad()).toBe(1); // claim moved
    expect(s.getActivePadIndex()).toBe(2);
    s.close();
    expect(findNewPad()).toBe(1); // close releases the CURRENT pin, not the original
  });

  it("an unassigned local seat reads neutral even while its old pad is hammered", async () => {
    setPads(pad(1, [0]));
    const s = createCloudRetroSession(descriptorFor(), { padIndex: 1 });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(50);

    s.setPad(null);
    await vi.advanceTimersByTimeAsync(100);
    const last = new Int16Array(dc.sent[dc.sent.length - 1])[0];
    expect(last).toBe(0); // the un-pin resent state as neutral; the held button released
    s.close();
  });

  it("a primary session pinned via setPad reads only that pad", async () => {
    setPads(pad(0, [9]), pad(1));
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), { videoEl: null });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();

    s.setPad(1); // pinned to the idle pad — pad 0's Start must stop riding this seat
    await vi.advanceTimersByTimeAsync(100);
    const masks = dc.sent.map((f) => new Int16Array(f)[0]);
    expect(masks[masks.length - 1]).toBe(0);
    expect(s.getActivePadIndex()).toBe(1);
    s.close();
  });

  // ── Streamed-pad guard (heavy lane §6.3): on the Moonlight host, XInput ⇒ a guest's ViGEm pad ──

  it("guard ON: the press-a-button detector never answers an XInput pad", () => {
    setPads(xpad(0, [0]), pad(1));
    expect(findNewPad()).toBe(0); // guard off (default): behaves as before
    setIgnoreStreamedPads(true);
    expect(findNewPad()).toBe(-1); // the streamed guest hammering buttons is invisible
    setPads(xpad(0, [0]), pad(1, [0]));
    expect(findNewPad()).toBe(1); // a real (non-XInput) pad still identifies itself
    expect(isStreamedPad({ index: 0, id: "Xbox 360 Controller (XInput STANDARD GAMEPAD)" })).toBe(true);
    expect(isStreamedPad({ index: 1, id: "DualSense Wireless Controller" })).toBe(false);
  });

  it("guard ON: the primary never adopts a streamed pad, and drops one already latched", async () => {
    setPads(xpad(0, [9])); // a guest mashing Start
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), { videoEl: null });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(50); // guard still off: fluid adoption latches the pad
    let masks = dc.sent.map((f) => new Int16Array(f)[0]);
    expect(masks[masks.length - 1]).not.toBe(0); // ...and their input reaches the seat (the trap)

    setIgnoreStreamedPads(true); // flipping it mid-game evicts the latched streamed pad
    await vi.advanceTimersByTimeAsync(100);
    masks = dc.sent.map((f) => new Int16Array(f)[0]);
    expect(masks[masks.length - 1]).toBe(0); // seat reads neutral while the guest keeps mashing
    s.close();
  });

  it("guard ON: explicit panel assignment of an XInput pad still works (deliberate override)", async () => {
    setIgnoreStreamedPads(true);
    setPads(xpad(1, [0]));
    const s = createCloudRetroSession(descriptorFor(), { padIndex: 1 });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(100);
    const last = new Int16Array(dc.sent[dc.sent.length - 1])[0];
    expect(last).not.toBe(0); // the pin bypasses the guard
    s.close();
  });

  // ── Echo guard (capture lane): on the capture HOST our own output comes back as an XInput pad ──

  it("echo guard: the seat is never handed to a pad mirroring our own output", async () => {
    setPads(pad(0, [0])); // the player's own pad, A held
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), { videoEl: null });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(50);
    expect(s.getActivePadIndex()).toBe(0);
    expect(new Int16Array(dc.sent[dc.sent.length - 1])[0]).not.toBe(0);

    // The capture worker pressed its virtual ViGEm pad because WE told it to, and on the host
    // Chrome enumerates that pad like any Xbox controller. The player lets go: for one poll their
    // pad reads idle while the echo still reads pressed (our release is still in flight).
    setPads(pad(0), xpad(1, [0]));
    await vi.advanceTimersByTimeAsync(100);
    expect(s.getActivePadIndex()).toBe(0); // the echo never takes the seat...
    expect(new Int16Array(dc.sent[dc.sent.length - 1])[0]).toBe(0); // ...so the release is sent
    s.close();
  });

  it("echo guard: a real pad is still adopted once our output has gone quiet", async () => {
    setPads(pad(0, [0]));
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), { videoEl: null });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(50);

    setPads(pad(0));                    // released — nothing left for a virtual pad to mirror
    await vi.advanceTimersByTimeAsync(600);
    setPads(pad(0), pad(1, [0]));       // ...then they pick up another pad (the re-enumeration case)
    await vi.advanceTimersByTimeAsync(50);
    expect(s.getActivePadIndex()).toBe(1);
    expect(new Int16Array(dc.sent[dc.sent.length - 1])[0]).not.toBe(0);
    s.close();
  });

  it("keepalive: the unchanged frame is re-sent ~1/s so a dropped release can't stick forever", async () => {
    setPads(pad(0, [0]));
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), { videoEl: null });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(100);
    const afterPress = dc.sent.length;
    expect(afterPress).toBe(1); // send-on-change: one frame for the press, not one per 16 ms poll

    await vi.advanceTimersByTimeAsync(300);
    expect(dc.sent.length).toBe(afterPress); // still quiet — the dedupe is intact

    await vi.advanceTimersByTimeAsync(800); // now past the resync interval
    expect(dc.sent.length).toBe(afterPress + 1);
    const [resent, held] = [dc.sent[dc.sent.length - 1], dc.sent[afterPress - 1]].map((f) => new Int16Array(f)[0]);
    expect(resent).toBe(held); // absolute state, re-asserted — not an edge
    s.close();
  });

  // ── Phantom pads: the corpse a disconnect/reconnect leaves in the Gamepad API ──────────────────

  it("phantom: a corpse leaves the pad list once its live twin reports", async () => {
    setPads(idPad(0, { ts: 100 }));
    expect(livePads().map((p) => p.index)).toEqual([0]);

    // Reconnected: the old slot lingers frozen at ts 100, the same controller returns at index 1.
    setPads(idPad(0, { ts: 100 }), idPad(1, { ts: 500 }));
    expect(livePads().map((p) => p.index)).toEqual([0, 1]); // both new to us — nothing condemned yet

    await vi.advanceTimersByTimeAsync(3200);
    setPads(idPad(0, { ts: 100 }), idPad(1, { ts: 900 })); // only the live one keeps sampling
    expect(livePads().map((p) => p.index)).toEqual([1]);
    expect(isPhantomPad(padsNow[0])).toBe(true);
    expect(isPhantomPad(padsNow[1])).toBe(false);
  });

  it("phantom: an idle pad with no twin is never condemned", async () => {
    setPads(idPad(0, { ts: 100, id: "Pad A" }), idPad(1, { ts: 100, id: "Pad B" }));
    livePads();
    await vi.advanceTimersByTimeAsync(5000); // both quiet far past the stale window
    expect(livePads().map((p) => p.index)).toEqual([0, 1]); // "quiet" is not "dead"
  });

  it("phantom: a pinned seat follows its controller to the index it comes back on", async () => {
    setPads(idPad(1, { ts: 100, pressed: [0] }));
    const s = createCloudRetroSession(descriptorFor(), { padIndex: 1 });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(50);
    expect(s.getActivePadIndex()).toBe(1);
    expect(new Int16Array(dc.sent[dc.sent.length - 1])[0]).not.toBe(0);

    // Unplugged MID-PRESS (the nastiest corpse: it reports that button held forever) and plugged
    // back in at index 2.
    setPads(idPad(1, { ts: 100, pressed: [0] }), idPad(2, { ts: 600 }));
    await vi.advanceTimersByTimeAsync(3200);
    setPads(idPad(1, { ts: 100, pressed: [0] }), idPad(2, { ts: 1200 }));
    await vi.advanceTimersByTimeAsync(100);

    expect(s.getActivePadIndex()).toBe(2); // the pin followed the controller, not the index
    expect(new Int16Array(dc.sent[dc.sent.length - 1])[0]).toBe(0); // and the frozen press let go
    expect(findNewPad()).toBe(-1); // the corpse can't answer the press-a-button detector either
    s.close();
  });

  it("phantom: the primary drops a corpse it had latched instead of holding its button", async () => {
    setPads(idPad(0, { ts: 100, pressed: [0] }));
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), { videoEl: null });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(50);
    expect(new Int16Array(dc.sent[dc.sent.length - 1])[0]).not.toBe(0);

    setPads(idPad(0, { ts: 100, pressed: [0] }), idPad(1, { ts: 600 }));
    await vi.advanceTimersByTimeAsync(3200);
    setPads(idPad(0, { ts: 100, pressed: [0] }), idPad(1, { ts: 1200 }));
    await vi.advanceTimersByTimeAsync(600); // past the stale window, then past the echo window

    expect(new Int16Array(dc.sent[dc.sent.length - 1])[0]).toBe(0);
    expect(s.getActivePadIndex()).toBe(1); // adoption moved on to the real pad
    s.close();
  });

  it("a pinned session ignores the keyboard — the primary session owns it", async () => {
    setPads(pad(1));
    const s = createCloudRetroSession(descriptorFor(), { padIndex: 1 });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();
    await vi.advanceTimersByTimeAsync(50);
    const before = dc.sent.length;

    window.dispatchEvent(new KeyboardEvent("keydown", { code: "ArrowRight" }));
    await vi.advanceTimersByTimeAsync(200);

    // No new frames: the keyboard changed nothing for this seat (pumpInput dedupes unchanged state).
    expect(dc.sent.length).toBe(before);
    s.close();
  });

  // Chord/hold-to-fire bindings (controllerChords.js) ride the SAME pumpInput poll this whole file
  // exercises — this proves onChordAction actually fires through a real tick, not just in the
  // chord-watcher's own isolated unit tests.
  it("onChordAction fires once the default quick-save chord (Select+R3) is held past its threshold", async () => {
    // Physical pad indices 8/11 are DEFAULT_GAMEPAD's SELECT/R3 (see cloudRetroClient.js) — held
    // continuously from the start of the session.
    setPads(pad(0, [8, 11]));
    const fired = [];
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), {
      videoEl: null,
      onChordAction: (action) => fired.push(action),
    });
    await driveToGameStart(sockets[0]);
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();

    await vi.advanceTimersByTimeAsync(500); // under quickSave's 600ms hold threshold
    expect(fired).toEqual([]);

    await vi.advanceTimersByTimeAsync(200); // now past it
    expect(fired).toEqual(["quickSave"]);

    await vi.advanceTimersByTimeAsync(500); // still held — must not re-fire without a release
    expect(fired).toEqual(["quickSave"]);
    s.close();
  });
});
