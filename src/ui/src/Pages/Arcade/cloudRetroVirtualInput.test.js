import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";
import { createCloudRetroSession, PAD } from "./cloudRetroClient";

// The on-screen touch pad (touch/TouchControls) feeds the primary seat through
// session.setVirtualInput(mask, axes). These tests pin the seam: a touch press reaches the wire on the
// EDGE (not the next poll), a release is always sent, sticks merge with a real pad by magnitude, a touched
// chord fires like a physical one, and nobody but the primary seat can drive it.

const T = { INIT: 4, GAME_START: 104 };

let sockets;
let channels;
let padsNow;

class FakeDataChannel {
  constructor(label) {
    this.label = label;
    this.readyState = "open";
    this.sent = [];
    channels.push(this);
    setTimeout(() => this.onopen && this.onopen(), 0);
  }
  send(data) { this.sent.push(data); }
  close() { this.readyState = "closed"; }
}

class FakePeerConnection {
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
  static OPEN = 1;
  constructor(url) {
    this.url = url;
    this.readyState = 1;
    this.sent = [];
    sockets.push(this);
    setTimeout(() => this.onopen && this.onopen(), 0);
  }
  send(raw) { this.sent.push(JSON.parse(raw)); }
  close() { this.readyState = 3; }
}

const descriptorFor = (over = {}) => ({
  wsUrl: "ws://gateway.test/w/token?room_id=r___Game",
  gameKey: "Game", playerSlot: 0, isCreator: false, roomCode: "AAA", system: "snes",
  iceConfig: [], ...over,
});

const pad = (index, axes = [0, 0, 0, 0]) => ({
  index,
  buttons: Array.from({ length: 16 }, () => ({ pressed: false })),
  axes,
});

async function openSession(desc, opts = {}) {
  const s = createCloudRetroSession(desc, { videoEl: null, ...opts });
  const ws = sockets[sockets.length - 1];
  await vi.waitFor(() => expect(ws.onopen).toBeTruthy());
  ws.onmessage({ data: JSON.stringify({ t: T.INIT, p: { ice: [] } }) });
  await vi.advanceTimersByTimeAsync(150);
  ws.onmessage({ data: JSON.stringify({ t: T.GAME_START, p: { room_id: "r___Game" } }) });
  const dc = channels.find((c) => c.label === "data");
  dc.onopen?.();
  await vi.advanceTimersByTimeAsync(50);
  return { s, dc };
}

const frames = (dc) => dc.sent.map((f) => Array.from(new Int16Array(f)));
const lastFrame = (dc) => frames(dc).at(-1);

describe("cloudRetroClient — virtual (touch) input", () => {
  beforeEach(() => {
    sockets = [];
    channels = [];
    padsNow = [null, null, null, null];
    vi.stubGlobal("WebSocket", FakeWebSocket);
    vi.stubGlobal("RTCPeerConnection", FakePeerConnection);
    vi.stubGlobal("MediaStream", class { addTrack() {} });
    vi.stubGlobal("navigator", { ...navigator, getGamepads: () => padsNow });
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.advanceTimersByTime(600);
  });
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

  it("a touch press goes out on the edge and its release is sent", async () => {
    const { s, dc } = await openSession(descriptorFor());
    const before = dc.sent.length;
    s.setVirtualInput(1 << PAD.A, [0, 0, 0, 0]);
    // Sent synchronously by the edge pump — no poll tick needed.
    expect(dc.sent.length).toBe(before + 1);
    expect(lastFrame(dc)[0]).toBe(1 << PAD.A);
    s.setVirtualInput(0, [0, 0, 0, 0]);
    expect(lastFrame(dc)[0]).toBe(0);
    s.close();
  });

  it("virtual sticks merge with a real pad by magnitude, and are clamped to ±32767", async () => {
    padsNow[0] = pad(0, [0.5, 0, 0, 0]);
    const { s, dc } = await openSession(descriptorFor({ system: "n64" }));
    s.setVirtualInput(0, [-100000, 20000, 0, -32767]);
    const f = lastFrame(dc);
    expect(f[1]).toBe(-32767);   // the virtual stick is pushed further than the pad's 0.5
    expect(f[2]).toBe(20000);
    expect(f[4]).toBe(-32767);
    s.setVirtualInput(0, [1000, 0, 0, 0]);
    expect(lastFrame(dc)[1]).toBe(Math.trunc(0.5 * 32767)); // a resting thumb loses to the pushed pad
    s.close();
  });

  it("a touched chord fires onChordAction and its buttons are stripped from the frame", async () => {
    const fired = [];
    const { s, dc } = await openSession(descriptorFor(), { onChordAction: (a, on) => fired.push([a, on]) });
    s.setVirtualInput((1 << PAD.SELECT) | (1 << PAD.Y), [0, 0, 0, 0]);
    await vi.advanceTimersByTimeAsync(250); // past rewind's 150 ms hold
    expect(fired.some(([a]) => a === "rewind")).toBe(true);
    expect(lastFrame(dc)[0]).toBe(0); // engaged chord bits never press Select/Y in the game
    s.setVirtualInput(0, [0, 0, 0, 0]);
    await vi.advanceTimersByTimeAsync(20);
    expect(fired.at(-1)).toEqual(["rewind", false]);
    s.close();
  });

  it("window blur releases a held touch", async () => {
    const { s, dc } = await openSession(descriptorFor());
    s.setVirtualInput(1 << PAD.B, [0, 10000, 0, 0]);
    window.dispatchEvent(new Event("blur"));
    await vi.advanceTimersByTimeAsync(20);
    expect(lastFrame(dc)).toEqual([0, 0, 0, 0, 0]);
    s.close();
  });

  it("a spectator's touch pad sends nothing", async () => {
    const s = createCloudRetroSession(descriptorFor({ playerSlot: -1, spectator: true }), { videoEl: null });
    await vi.advanceTimersByTimeAsync(200);
    s.setVirtualInput(1 << PAD.A, [0, 0, 0, 0]);
    await vi.advanceTimersByTimeAsync(50);
    const dc = channels.find((c) => c.label === "data");
    expect(dc ? dc.sent.length : 0).toBe(0);
    s.close();
  });

  it("an input-only local seat ignores the touch pad", async () => {
    padsNow[1] = pad(1);
    const { s, dc } = await openSession(descriptorFor({ playerSlot: 1 }), { padIndex: 1 });
    const before = dc.sent.length;
    s.setVirtualInput(1 << PAD.A, [0, 0, 0, 0]);
    await vi.advanceTimersByTimeAsync(50);
    expect(frames(dc).slice(before).every((f) => f[0] === 0)).toBe(true);
    s.close();
  });
});
