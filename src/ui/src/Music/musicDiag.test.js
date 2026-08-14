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
    diagLog("park", { track: 7 });
    diagLog("load:failed", { track: 7 });
    diagLog("mse:dry", { sec: 91 });
    expect(diagList().map((e) => e.event)).toEqual(["park", "load:failed", "mse:dry"]);
  });

  it("ignores the ROUTINE lifecycle when off — the tripwire is not a journal", () => {
    // These were all always-on while the sleeping-phone bug was open. Each fires once or more PER
    // TRACK on a healthy player, and each one costs a localStorage write and a subscriber notify.
    // With that bug fixed they are the excess: ?diag=1 is what brings them back.
    setDiagEnabled(false);
    ["boundary", "wake", "recover", "visibility",
     "preload:ready", "preload:stream", "preload:fetch",
     "load:minted", "load:download", "load:downloaded"].forEach((e) => diagLog(e, {}));
    expect(diagList()).toHaveLength(0);
  });

  it("still ignores the media firehose when off — always-on must stay cheap", () => {
    setDiagEnabled(false);
    diagLog("timeupdate", {});
    diagLog("canplaythrough", {});
    diagLog("suspend", {});
    expect(diagList()).toHaveLength(0);
  });

  it("records everything again the moment the switch goes back on", () => {
    // The whole point of trimming the always-on set is that ONE switch undoes it. If this fails,
    // the next investigation starts by editing source, which is what the switch exists to avoid.
    setDiagEnabled(true);
    ["boundary", "wake", "preload:ready", "timeupdate"].forEach((e) => diagLog(e, {}));
    expect(diagList()).toHaveLength(4);
  });

  it("can be turned on for one page life without following the browser around", () => {
    // The MSE probe route flips this on for its run. Persisting it left every listener's browser
    // recording every media event forever because someone once opened a diagnostics URL on it.
    setDiagEnabled(false);
    setDiagEnabled(true, { persist: false });
    expect(diagEnabled()).toBe(true);
    expect(window.localStorage.getItem("music.diag")).toBe(null);
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

    diagLog("error", { upcoming: 9, deckReady: false });
    expect(reportIncident("boundary", { summary: "no buffered deck", trackId: 9 })).toBe(true);

    expect(sent).toHaveLength(1);
    expect(sent[0].url).toBe("/API/Music/Incident");
    const body = JSON.parse(await sent[0].blob.text());
    expect(body.kind).toBe("boundary");
    expect(body.trackId).toBe(9);
    // Whatever the ring holds rides along. With the switch off that is the failures only; with it on
    // it is the run-up too — either way the report is never a bare "it failed" with no context.
    expect(body.events.some((e) => e.event === "error")).toBe(true);
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

  it("refunds a report the browser refused to queue — a lost beacon must not silence the next one", async () => {
    // 2026-08-13: the budget was spent and the 60 s gap armed BEFORE anyone knew whether the
    // beacon was accepted, so on the sleeping phone the reports that failed were the ones that
    // silenced everything after them. A refused hand-off now costs nothing.
    let accept = false;
    const sent = [];
    navigator.sendBeacon = () => { if (accept) { sent.push(1); return true; } return false; };
    const { reportIncident } = await import("./musicDiag");

    expect(reportIncident("park", { force: true })).toBe(false);
    expect(reportIncident("park", { force: true })).toBe(false);
    accept = true;
    // No gap was armed and no budget was spent by the refusals: this lands immediately, unforced.
    expect(reportIncident("park", {})).toBe(true);
    expect(sent).toHaveLength(1);
  });

  it("caps the whole session, which is the limit `force: true` used to walk straight past", async () => {
    // The gap limit alone was never enough: the MSE paths passed force to jump it, so a browser
    // stuck in a fallback loop could write rows faster than one a minute — the exact flood the
    // limit existed to prevent. force skips the GAP; nothing skips the session ceiling.
    const sent = [];
    navigator.sendBeacon = () => { sent.push(1); return true; };
    const { reportIncident } = await import("./musicDiag");

    for (let i = 0; i < 20; i += 1) reportIncident("mse", { summary: `loop ${i}`, force: true });
    expect(sent).toHaveLength(5);
    expect(reportIncident("mse", { force: true })).toBe(false);
  });
});
