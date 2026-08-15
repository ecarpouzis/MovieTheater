import { describe, it, expect } from "vitest";
import {
  createInputTape, createTapePlayer, decodeTrace, bytesToB64, b64ToBytes, thumbDistance,
  TAPE_KIND, TAPE_VERSION,
} from "./inputTape";

// A tape's clocks: `now` is wall, `mediaTime` is the stream clock in SECONDS (that's what
// requestVideoFrameCallback hands us), and offsets are stored in MILLISECONDS on both.
const clock = (wallMs, mediaSec, pf) => ({ now: 1000 + wallMs, mediaTime: mediaSec, presentedFrames: pf });

describe("base64", () => {
  it("round-trips every byte value and every length remainder", () => {
    for (const len of [0, 1, 2, 3, 4, 5, 192]) {
      const bytes = new Uint8Array(len);
      for (let i = 0; i < len; i++) bytes[i] = (i * 37 + 11) & 0xff;
      expect(Array.from(b64ToBytes(bytesToB64(bytes)))).toEqual(Array.from(bytes));
    }
  });
});

describe("thumbDistance", () => {
  it("is 0 for identical frames and the mean gap otherwise", () => {
    expect(thumbDistance(new Uint8Array([10, 20]), new Uint8Array([10, 20]))).toBe(0);
    expect(thumbDistance(new Uint8Array([10, 20]), new Uint8Array([14, 30]))).toBe(7);
  });
  // "Couldn't compare" must never read as "identical" — a caller looking for divergence would
  // conclude the runs matched.
  it("returns Infinity rather than 0 when it cannot compare", () => {
    expect(thumbDistance(null, new Uint8Array([1]))).toBe(Infinity);
    expect(thumbDistance(new Uint8Array([1, 2]), new Uint8Array([1]))).toBe(Infinity);
    expect(thumbDistance(new Uint8Array([]), new Uint8Array([]))).toBe(Infinity);
  });
});

describe("createInputTape", () => {
  it("stamps both clocks, with the media clock's origin at the first stamped event", () => {
    const tape = createInputTape({ system: "ps2" }, { now: 1000 });
    // Media time starts at 12.5 s — wherever the live stream happened to be.
    tape.input(1, [0, 0, 0, 0], clock(0, 12.5, 100));
    tape.input(3, [32767, 0, 0, 0], clock(250, 12.75, 115));
    const json = tape.toJSON();
    expect(json.kind).toBe(TAPE_KIND);
    expect(json.v).toBe(TAPE_VERSION);
    expect(json.inputs[0].slice(0, 3)).toEqual([0, 0, 100]);
    expect(json.inputs[1].slice(0, 3)).toEqual([250, 250, 115]);
    expect(json.inputs[1].slice(3)).toEqual([3, 32767, 0, 0, 0]);
  });

  it("records controls and marks on the same timeline as inputs", () => {
    const tape = createInputTape({}, { now: 1000 });
    tape.input(0, [0, 0, 0, 0], clock(0, 5, 1));
    tape.control("quickLoad", null, clock(100, 5.1, 7));
    tape.control("mark", "crash here", clock(2000, 7, 120));
    const json = tape.toJSON();
    expect(json.controls).toEqual([[100, 100, "quickLoad", null], [2000, 2000, "mark", "crash here"]]);
  });

  it("stores a frame trace it can decode back to the same bytes", () => {
    const tape = createInputTape({}, { now: 1000 });
    const thumb = new Uint8Array([1, 2, 3, 250]);
    tape.frame(4, 42, thumb);
    const decoded = decodeTrace(tape.toJSON());
    expect(decoded).toHaveLength(1);
    expect(Array.from(decoded[0].thumb)).toEqual([1, 2, 3, 250]);
    expect(decoded[0].pf).toBe(42);
  });

  // A forgotten tab must not grow without bound — and must SAY it stopped tracing rather than hand
  // back a short trace that looks complete.
  it("caps the trace and flags the truncation, without dropping inputs", () => {
    const tape = createInputTape({}, { now: 1000, maxTraceFrames: 2 });
    for (let i = 0; i < 5; i++) tape.frame(i, i, new Uint8Array([i]));
    tape.input(1, [0, 0, 0, 0], clock(10, 0.1, 5));
    const json = tape.toJSON();
    expect(json.trace).toHaveLength(2);
    expect(json.meta.traceTruncated).toBe(true);
    expect(json.inputs).toHaveLength(1);
  });

  it("leaves the media stamp null when the video clock isn't running yet", () => {
    const tape = createInputTape({}, { now: 1000 });
    tape.input(1, [0, 0, 0, 0], { now: 1000, mediaTime: null, presentedFrames: null });
    expect(tape.toJSON().inputs[0][0]).toBe(null);
    expect(tape.toJSON().inputs[0][2]).toBe(-1);
  });
});

describe("createTapePlayer", () => {
  // mask, then the four axes. Frames are ABSOLUTE STATE, so "the frame at T" is the last one sent
  // at or before T — not an edge to be re-applied.
  const tape = {
    inputs: [
      [0, 0, 0, 0, 0, 0, 0, 0],
      [100, 120, 6, 1, 0, 0, 0, 0],
      [500, 540, 30, 0, 0, 0, 0, 0],
      [900, 1000, 54, 9, -32767, 0, 0, 0],
    ],
    controls: [[300, 320, "quickLoad", null], [800, 860, "fastForward", true]],
  };

  it("holds the last sent frame until the next one is due", () => {
    const p = createTapePlayer(tape);
    expect(p.frameAt(0)).toEqual([0, 0, 0, 0, 0]);
    expect(p.frameAt(99)).toEqual([0, 0, 0, 0, 0]);
    expect(p.frameAt(100)).toEqual([1, 0, 0, 0, 0]);
    expect(p.frameAt(499)).toEqual([1, 0, 0, 0, 0]);
    expect(p.frameAt(900)).toEqual([9, -32767, 0, 0, 0]);
    expect(p.frameAt(99999)).toEqual([9, -32767, 0, 0, 0]);
  });

  // The pump calls frameAt every 16 ms; a dropped tick or a clock that jumps must land on the right
  // frame, and a re-anchored loop iteration reuses the player with a clock that went backwards.
  it("seeks in both directions rather than only advancing", () => {
    const p = createTapePlayer(tape);
    expect(p.frameAt(900)).toEqual([9, -32767, 0, 0, 0]);
    expect(p.frameAt(100)).toEqual([1, 0, 0, 0, 0]);
    expect(p.frameAt(0)).toEqual([0, 0, 0, 0, 0]);
  });

  it("reads the wall clock column when asked", () => {
    const p = createTapePlayer(tape, { clockMode: "wall" });
    expect(p.mode).toBe("wall");
    expect(p.frameAt(119)).toEqual([0, 0, 0, 0, 0]); // media-due at 100, wall-due at 120
    expect(p.frameAt(120)).toEqual([1, 0, 0, 0, 0]);
  });

  it("hands each control out exactly once, in order", () => {
    const p = createTapePlayer(tape);
    expect(p.dueControls(299)).toEqual([]);
    expect(p.dueControls(300).map((c) => c.action)).toEqual(["quickLoad"]);
    expect(p.dueControls(400)).toEqual([]);
    expect(p.dueControls(9999).map((c) => c.action)).toEqual(["fastForward"]);
    expect(p.dueControls(9999)).toEqual([]);
  });

  it("rewinds for another loop iteration", () => {
    const p = createTapePlayer(tape);
    p.dueControls(9999);
    p.rewind();
    expect(p.dueControls(9999).map((c) => c.action)).toEqual(["quickLoad", "fastForward"]);
    expect(p.frameAt(0)).toEqual([0, 0, 0, 0, 0]);
  });

  it("scales the whole timeline by speed", () => {
    const p = createTapePlayer(tape, { speed: 2 });
    expect(p.frameAt(199)).toEqual([0, 0, 0, 0, 0]);
    expect(p.frameAt(200)).toEqual([1, 0, 0, 0, 0]); // 100 ms of tape at 2x = 200 ms of clock
    expect(p.durationMs).toBe(1800);
  });

  // A tape recorded before the video attached has null media stamps; replaying it on the media clock
  // must drop those rows LOUDLY rather than treat null as time zero.
  it("drops unstampable rows and counts them", () => {
    const p = createTapePlayer({ inputs: [[null, 0, -1, 5, 0, 0, 0, 0], [10, 12, 1, 7, 0, 0, 0, 0]], controls: [] });
    expect(p.length).toBe(1);
    expect(p.droppedInputs).toBe(1);
    expect(p.frameAt(0)).toEqual([0, 0, 0, 0, 0]);
    expect(p.frameAt(10)).toEqual([7, 0, 0, 0, 0]);
  });

  it("reports progress and completion", () => {
    const p = createTapePlayer(tape);
    expect(p.progress(450)).toBeCloseTo(0.5, 5);
    expect(p.finished(899)).toBe(false);
    expect(p.finished(901)).toBe(true);
    expect(createTapePlayer({ inputs: [], controls: [] }).finished(0)).toBe(true);
  });
});
