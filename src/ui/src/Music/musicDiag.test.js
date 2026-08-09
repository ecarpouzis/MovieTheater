import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

import {
  diagLog, diagList, diagText, clearDiag, diagEnabled, setDiagEnabled, subscribeDiag,
  snapshotAudio, MEDIA_ERROR_NAMES,
} from "./musicDiag";

beforeEach(() => { clearDiag(); setDiagEnabled(true); });
afterEach(() => { setDiagEnabled(false); clearDiag(); });

describe("musicDiag", () => {
  it("records nothing at all when it is off — it must cost nothing in normal use", () => {
    setDiagEnabled(false);
    expect(diagEnabled()).toBe(false);
    diagLog("boundary", { upcoming: 2 });
    expect(diagList()).toHaveLength(0);
  });

  it("keeps the ring bounded so a long session can't grow without limit", () => {
    for (let i = 0; i < 700; i += 1) diagLog("tick", { i });
    const list = diagList();
    expect(list.length).toBeLessThanOrEqual(500);
    // and it keeps the NEWEST, which is where the failure is
    expect(list[list.length - 1].data.i).toBe(699);
  });

  it("names MediaError codes, because `err: 4` means nothing on a phone at 3am", () => {
    expect(MEDIA_ERROR_NAMES[4]).toBe("SRC_NOT_SUPPORTED");
    const audio = { src: "https://gw/abcdefgh/MusicFile", paused: true, ended: false,
                    currentTime: 0, readyState: 0, networkState: 3, error: { code: 4 } };
    const snap = snapshotAudio(audio);
    expect(snap.err).toBe("SRC_NOT_SUPPORTED");
    expect(snap.net).toBe("NO_SOURCE");
    expect(snap.ready).toBe("NOTHING");
  });

  it("marks a gap between entries, which is how a frozen renderer shows up", () => {
    const t0 = 1_000_000_000_000;
    const spy = vi.spyOn(Date, "now");
    spy.mockReturnValue(t0);
    diagLog("play");
    spy.mockReturnValue(t0 + 42_000);   // the phone slept for 42 seconds
    diagLog("boundary");
    spy.mockRestore();
    expect(diagText()).toContain("+42.0s");
  });

  it("notifies subscribers so the panel updates while it is open", () => {
    const seen = vi.fn();
    const off = subscribeDiag(seen);
    diagLog("play");
    expect(seen).toHaveBeenCalled();
    off();
    seen.mockClear();
    diagLog("pause");
    expect(seen).not.toHaveBeenCalled();
  });

  it("produces pasteable text — the log is only useful off the phone", () => {
    diagLog("error", { err: "SRC_NOT_SUPPORTED", net: "NO_SOURCE" });
    const text = diagText();
    expect(text).toContain("error");
    expect(text).toContain("SRC_NOT_SUPPORTED");
  });
});
