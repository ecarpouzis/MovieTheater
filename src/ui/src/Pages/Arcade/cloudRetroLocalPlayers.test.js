import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";
import { createCloudRetroSession, findNewPad, setIgnoreStreamedPads, isStreamedPad } from "./cloudRetroClient";

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
  it("onChordAction fires once the default quick-save chord (L3+R3) is held past its threshold", async () => {
    // Physical pad indices 10/11 are DEFAULT_GAMEPAD's L3/R3 (see cloudRetroClient.js) — held
    // continuously from the start of the session.
    setPads(pad(0, [10, 11]));
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
