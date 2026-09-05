import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";
import { createCloudRetroSession } from "./cloudRetroClient";

// The one guarantee this file exists to protect: a SPECTATOR's browser can never touch the game.
//
// On the worker, a connection's controller port (`user.Index`) is only ever read inside the joypad
// DataChannel's OnMessage handler — `r.App().Input(user.Index, …)` in coordinatorhandlers.go. So a
// client that sends no input frames cannot reach the emulator at all. The shim enforces exactly that:
// no t=108 seat claim, no key/gamepad listeners, no DataChannel sends, and no save/load/reset (those
// act on the room's single shared emulator).
//
// Everything below drives the real shim against fake WebRTC/WebSocket, so it breaks if that guard is
// ever refactored away.

const T = { INIT: 4, INIT_WEBRTC: 100, GAME_START: 104, SET_PLAYER_INDEX: 108 };

let sockets;
let channels;

class FakeDataChannel {
  constructor(label) {
    this.label = label;
    this.readyState = "open";
    this.sent = [];
    channels.push(this);
    // A browser announces the open channel through onopen — and since perf program P2 that event is what
    // sends t=104 (the old 100 ms readyState poll is now a 2 s safety net). Fire it like the fake socket does.
    setTimeout(() => this.onopen && this.onopen(), 0);
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
  // The shim gates every send on `WebSocket.OPEN` — without these statics it silently sends nothing.
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;

  constructor(url) {
    this.url = url;
    this.readyState = 1; // OPEN
    this.sent = [];
    sockets.push(this);
    setTimeout(() => this.onopen && this.onopen(), 0);
  }
  send(raw) { this.sent.push(JSON.parse(raw)); }
  close() { this.readyState = 3; }
  /** Packets this socket sent, by type. */
  types() { return this.sent.map((m) => m.t); }
}

const descriptorFor = (over = {}) => ({
  wsUrl: "ws://gateway.test/w/token?room_id=r___Game",
  gameKey: "Game", playerSlot: 0, isCreator: false, roomCode: "AAA", system: "snes",
  iceConfig: [], ...over,
});

/**
 * Walk the shim through INIT → (DataChannel opens) → t=104 GAME_START → the worker's GAME_START reply,
 * which is the moment a player claims its controller port. The shim sends t=104 from the joypad
 * channel's onopen (which the fake fires on the next tick) — so the timers must advance.
 */
async function driveToGameStart() {
  const ws = sockets[0];
  await vi.waitFor(() => expect(ws.onopen).toBeTruthy());
  ws.onmessage({ data: JSON.stringify({ t: T.INIT, p: { ice: [] } }) });
  await vi.advanceTimersByTimeAsync(150);
  ws.onmessage({ data: JSON.stringify({ t: T.GAME_START, p: { room_id: "r___Game" } }) });
  return ws;
}

describe("cloudRetroClient — spectator cannot touch the game", () => {
  beforeEach(() => {
    sockets = [];
    channels = [];
    vi.stubGlobal("WebSocket", FakeWebSocket);
    vi.stubGlobal("RTCPeerConnection", FakePeerConnection);
    vi.stubGlobal("MediaStream", class { addTrack() {} });
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

  it("a PLAYER claims its controller port with t=108", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 1 }), { videoEl: null });
    const ws = await driveToGameStart();
    expect(ws.types()).toContain(T.SET_PLAYER_INDEX);
    const seat = ws.sent.find((m) => m.t === T.SET_PLAYER_INDEX);
    expect(seat.p).toBe(1);
    s.close();
  });

  it("a SPECTATOR never claims a seat", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: -1, spectator: true }), { videoEl: null });
    const ws = await driveToGameStart();
    expect(ws.types()).not.toContain(T.SET_PLAYER_INDEX);
    s.close();
  });

  it("a SPECTATOR's GAME_START carries a safe port, never the -1 sentinel", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: -1, spectator: true }), { videoEl: null });
    const ws = await driveToGameStart();
    const start = ws.sent.find((m) => m.t === T.GAME_START);
    expect(start.p.player_index).toBe(0); // the worker stores this verbatim; -1 must never be sent
    s.close();
  });

  it("a SPECTATOR sends no input frames however hard the keyboard is hit", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: -1, spectator: true }), { videoEl: null });
    await driveToGameStart();
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();

    for (const key of ["ArrowRight", "Enter", "z", "x"]) {
      window.dispatchEvent(new KeyboardEvent("keydown", { key }));
      window.dispatchEvent(new KeyboardEvent("keyup", { key }));
    }
    await vi.advanceTimersByTimeAsync(500); // several input-pump ticks (16 ms each)

    expect(dc.sent).toHaveLength(0);
    s.close();
  });

  it("a PLAYER does send input frames on the same keypresses", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), { videoEl: null });
    await driveToGameStart();
    const dc = channels.find((c) => c.label === "data");
    dc.onopen?.();

    window.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowRight" }));
    await vi.advanceTimersByTimeAsync(100);

    expect(dc.sent.length).toBeGreaterThan(0);
    s.close();
  });

  it("a SPECTATOR's save/load/reset are inert — the emulator belongs to the players", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: -1, spectator: true }), { videoEl: null });
    const ws = await driveToGameStart();
    const before = ws.sent.length;

    s.save(); s.load(); s.reset(); s.swapDisc(1);

    expect(ws.sent.length).toBe(before);
    s.close();
  });

  it("a PLAYER's save/load/reset do reach the room", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: 0 }), { videoEl: null });
    const ws = await driveToGameStart();
    const before = ws.sent.length;

    s.save(); s.load(); s.reset();

    expect(ws.sent.length).toBe(before + 3);
    s.close();
  });

  it("treats a -1 slot as a spectator even without the explicit flag", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: -1 }), { videoEl: null });
    const ws = await driveToGameStart();
    expect(ws.types()).not.toContain(T.SET_PLAYER_INDEX);
    s.close();
  });
});
