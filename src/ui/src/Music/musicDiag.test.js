import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

import {
  diagLog, diagList, diagText, clearDiag, diagEnabled, setDiagEnabled, subscribeDiag,
  snapshotAudio, MEDIA_ERROR_NAMES,
} from "./musicDiag";

beforeEach(() => { clearDiag(); setDiagEnabled(true); });
afterEach(() => { setDiagEnabled(false); clearDiag(); });

describe("musicDiag", () => {
  it("still records FAILURE events when it is off — the failure erases its own evidence", () => {
    // This deliberately reverses the original rule ("records nothing at all when off"). That rule
    // is why this bug went unrecorded for twenty-odd occurrences: the failure happens on a sleeping
    // phone, the player then RECOVERS, and by the time anyone can look the moment is gone. Asking
    // someone to have had diagnostics enabled beforehand never once produced a log.
    setDiagEnabled(false);
    expect(diagEnabled()).toBe(false);
    diagLog("boundary", { upcoming: 2 });
    diagLog("park", { track: 7 });
    expect(diagList().map((e) => e.event)).toEqual(["boundary", "park"]);
  });

  it("still ignores the media firehose when off — always-on must stay cheap", () => {
    setDiagEnabled(false);
    diagLog("timeupdate", {});
    diagLog("canplaythrough", {});
    diagLog("suspend", {});
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

describe("musicDiag self-reporting", () => {
  it("survives the reload that the failure causes", async () => {
    // The player recovers by reloading, which used to take the ring with it. The evidence has to
    // outlive the page life that recorded it or there is nothing to read afterwards.
    diagLog("park", { track: 42 });
    const stored = JSON.parse(window.localStorage.getItem("music.diag.ring"));
    expect(stored.some((e) => e.event === "park" && e.data.track === 42)).toBe(true);
  });

  it("posts the log itself, by beacon, without anyone being asked to catch it", async () => {
    const sent = [];
    navigator.sendBeacon = (url, blob) => { sent.push({ url, blob }); return true; };
    const { reportIncident } = await import("./musicDiag");

    diagLog("boundary", { upcoming: 9, deckReady: false });
    expect(reportIncident("boundary", { summary: "no buffered deck", trackId: 9 })).toBe(true);

    expect(sent).toHaveLength(1);
    expect(sent[0].url).toBe("/API/Music/Incident");
    const body = JSON.parse(await sent[0].blob.text());
    expect(body.kind).toBe("boundary");
    expect(body.trackId).toBe(9);
    // The run-up is the point: a bare "it failed" says nothing the listener hadn't already said.
    expect(body.events.some((e) => e.event === "boundary")).toBe(true);
  });

  it("rate-limits itself so a failure loop cannot become a flood", async () => {
    const sent = [];
    navigator.sendBeacon = () => { sent.push(1); return true; };
    const { reportIncident } = await import("./musicDiag");

    expect(reportIncident("park", { force: true })).toBe(true);
    expect(reportIncident("park")).toBe(false);
    expect(reportIncident("park")).toBe(false);
    expect(sent).toHaveLength(1);
  });
});
