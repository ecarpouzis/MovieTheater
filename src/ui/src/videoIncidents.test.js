import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

import {
  clearVideoIncidents,
  createVideoWatcher,
  noteBandwidthEstimate,
  notePlaylistError,
  noteStreamSwitch,
  noteVideoEvent,
  reportAbrDowngrade,
  reportVideoIncident,
  setVideoIncidentContext,
  clearVideoIncidentContext,
  videoEvents,
} from "./videoIncidents";

// A stand-in for the beacon, so a test can read what would have been posted. setupTests.js already
// no-ops sendBeacon globally; this replaces it per-test and hands the bodies back.
function captureBeacons() {
  const sent = [];
  navigator.sendBeacon = (url, blob) => { sent.push({ url, blob }); return true; };
  return sent;
}

const WATCH_CONTEXT = {
  player: "watch",
  movieId: 9754,
  playableId: 331,
  ladder: { qualityKey: "auto", autoBps: Infinity, copied: true, codec: "hevc", sourceVideoBps: 23_000_000 },
  getPosition: () => 4212.5,
};

let clock;

/** Freeze time at `t`; every helper below reads Date.now(), so tests move the clock explicitly. */
function at(t) {
  clock.mockReturnValue(t);
}

beforeEach(() => {
  clearVideoIncidents();
  setVideoIncidentContext(WATCH_CONTEXT);
  clock = vi.spyOn(Date, "now");
  at(1_000_000);
});

afterEach(() => {
  clock.mockRestore();
  clearVideoIncidentContext();
  clearVideoIncidents();
});

describe("videoIncidents — reporting", () => {
  it("posts the player's own log by beacon, with the ids and the ladder state", async () => {
    const sent = captureBeacons();
    noteVideoEvent("waiting");
    noteBandwidthEstimate(5_200_000);

    expect(reportVideoIncident("stall", { summary: "frozen 14s while playing" })).toBe(true);
    expect(sent).toHaveLength(1);
    expect(sent[0].url).toBe("/API/Stream/Incident");

    const body = JSON.parse(await sent[0].blob.text());
    expect(body.kind).toBe("stall");
    expect(body.player).toBe("watch");
    expect(body.movieId).toBe(9754);
    expect(body.playableId).toBe(331);
    expect(body.channelId).toBe(null);
    expect(body.positionSeconds).toBe(4212.5);
    // The lossless tier is Infinity in the ladder math and doesn't survive JSON — it has to arrive
    // as something readable rather than as null (which reads as "no rung", the opposite claim).
    expect(body.state.rung).toBe("direct");
    expect(body.state.copied).toBe(true);
    expect(body.state.estimateBps).toBe(5_200_000);
    expect(body.events.some((e) => e.event === "waiting")).toBe(true);
  });

  it("files nothing when no player of ours is on screen", () => {
    // The photo-album video shares the engine (createHls) but is not what this table is for, and an
    // anonymous row with no ids is worse than no row: it looks like a movie incident nobody can trace.
    const sent = captureBeacons();
    clearVideoIncidentContext();
    expect(reportVideoIncident("fatal", { summary: "boom", force: true })).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it("rate-limits itself so a failure loop cannot become a flood", () => {
    const sent = captureBeacons();
    expect(reportVideoIncident("stall")).toBe(true);
    at(1_000_000 + 30_000);
    expect(reportVideoIncident("stall")).toBe(false);
    at(1_000_000 + 59_000);
    expect(reportVideoIncident("stall")).toBe(false);
    // A minute later the next one is allowed through.
    at(1_000_000 + 61_000);
    expect(reportVideoIncident("stall")).toBe(true);
    expect(sent).toHaveLength(2);
  });

  it("lets a terminal failure jump the gap but never the session ceiling", () => {
    // `force` exists for the once-only failures (fatal, a stream that never started). It must not
    // become a way around the ceiling, or one broken session writes rows all night.
    const sent = captureBeacons();
    for (let i = 0; i < 20; i += 1) reportVideoIncident("fatal", { summary: `loop ${i}`, force: true });
    expect(sent).toHaveLength(5);
    expect(reportVideoIncident("fatal", { force: true })).toBe(false);
  });

  it("refunds a report the browser refused to queue", () => {
    // Music's 2026-08-13 lesson: spending the budget before the hand-off meant a refused beacon
    // still armed the gap, so the reports that failed silenced everything after them.
    let accept = false;
    const sent = [];
    navigator.sendBeacon = () => { if (accept) { sent.push(1); return true; } return false; };
    expect(reportVideoIncident("stall")).toBe(false);
    expect(reportVideoIncident("stall")).toBe(false);
    accept = true;
    expect(reportVideoIncident("stall")).toBe(true);
    expect(sent).toHaveLength(1);
  });

  it("keeps the ring bounded — the payload rides in a beacon", () => {
    for (let i = 0; i < 400; i += 1) noteVideoEvent("waiting", { i });
    const ring = videoEvents();
    expect(ring.length).toBeLessThanOrEqual(120);
    expect(ring[ring.length - 1].data.i).toBe(399); // and it keeps the NEWEST, where the failure is
  });
});

describe("videoIncidents — a stall, and the restart that is not one", () => {
  /** A watcher whose reports are collected rather than posted, so detection is judged on its own. */
  function watcher() {
    const reports = [];
    const w = createVideoWatcher({ report: (kind, opts) => { reports.push({ kind, ...opts }); return true; } });
    return { w, reports };
  }

  it("reports a `waiting` that persists past ten seconds while playing", () => {
    const { w, reports } = watcher();
    noteStreamSwitch("source");
    at(1_060_000); // a minute in, well clear of the switch grace
    w.play();
    w.playing();
    w.waiting();
    at(1_071_000); // frozen for 11s
    w.tick();
    expect(reports.map((r) => r.kind)).toEqual(["stall"]);
    expect(reports[0].summary).toContain("frozen 11s");
  });

  it("does NOT report the ABR restart's own rebuffer — which is the whole discrimination", () => {
    // Every rung change tears the session down and starts a fresh ffmpeg: seconds of frozen picture,
    // BY DESIGN, announced on screen as "Adjusting quality". At the element it is the identical
    // `waiting` a genuine underrun fires. Without this the ladder would file an incident every time
    // it moved — and a table whose rows are mostly expected behaviour is worse than no table.
    const { w, reports } = watcher();
    at(1_060_000);
    w.play();
    w.playing();
    noteStreamSwitch("abr"); // useAdaptiveBitrate marks this on every adapt
    w.waiting();
    at(1_060_000 + 20_000); // 20s of restart: past the stall threshold, inside the switch grace
    w.tick();
    expect(reports).toEqual([]);
  });

  it("...but a stall that outlives the grace is still reported", () => {
    // The grace is a delay, not an amnesty: a restart that never recovers must still surface.
    const { w, reports } = watcher();
    at(1_060_000);
    w.play();
    w.playing();
    noteStreamSwitch("abr");
    w.waiting();
    at(1_060_000 + 40_000);
    w.tick();
    expect(reports.map((r) => r.kind)).toEqual(["stall"]);
  });

  it("does not report the viewer's own scrub, or a pause", () => {
    const { w, reports } = watcher();
    noteStreamSwitch("source");
    at(1_060_000);
    w.play();
    w.playing();

    w.waiting();
    w.seeking();          // a seek rebuffers; that is the viewer's doing
    at(1_072_000);
    w.tick();

    w.waiting();
    w.pause();            // nothing is stalling if nothing is meant to be playing
    at(1_090_000);
    w.tick();
    expect(reports).toEqual([]);
  });

  it("treats time advancing as ground truth that the stall ended", () => {
    const { w, reports } = watcher();
    noteStreamSwitch("source");
    at(1_060_000);
    w.play();
    w.playing();
    w.waiting();
    at(1_065_000);
    w.timeupdate();       // frames again — some browsers never re-fire `playing`
    at(1_075_000);
    w.tick();
    expect(reports).toEqual([]);
    expect(w.isWaiting).toBe(false);
  });

  it("does not re-report the same unbroken stall every second", () => {
    const { w, reports } = watcher();
    noteStreamSwitch("source");
    at(1_060_000);
    w.play();
    w.playing();
    w.waiting();
    at(1_071_000);
    w.tick();
    at(1_072_000);
    w.tick();
    at(1_073_000);
    w.tick();
    expect(reports).toHaveLength(1);
  });
});

describe("videoIncidents — startup, ending, and the ladder", () => {
  function watcher() {
    const reports = [];
    const w = createVideoWatcher({ report: (kind, opts) => { reports.push({ kind, ...opts }); return true; } });
    return { w, reports };
  }

  it("reports a stream that was asked to play and never produced a frame", () => {
    const { w, reports } = watcher();
    w.sourceChanged();
    at(1_002_000);
    w.play();             // play() was reached...
    at(1_050_000);        // ...and 50s later there is still no picture
    w.tick();
    expect(reports.map((r) => r.kind)).toEqual(["startup-timeout"]);
    expect(reports[0].force).toBe(true);
  });

  it("does NOT report blocked autoplay or a frozen channel as a failed start", () => {
    // Neither ever reaches play(): autoplay policy rejects the promise (the player shows a tap
    // prompt) and a paused channel is deliberately held on a still frame. Both would otherwise look
    // exactly like "no first frame in 45s".
    const { w, reports } = watcher();
    w.sourceChanged();
    at(1_100_000);
    w.tick();
    expect(reports).toEqual([]);
  });

  it("reports `ended` far from the duration, and stays quiet at the end of a film", () => {
    const { w, reports } = watcher();
    expect(w.ended({ position: 1_200, duration: 6_000 })).toBe(true);
    expect(reports[0].kind).toBe("early-ended");
    expect(reports[0].summary).toContain("4800s short");

    reports.length = 0;
    w.ended({ position: 5_990, duration: 6_000 });   // the credits rolled
    w.ended({ position: 90, duration: 0 });          // no duration known — nothing can be claimed
    w.ended({ position: 90, duration: Infinity });
    expect(reports).toEqual([]);
  });

  it("reports the emergency downgrade with both rungs named", () => {
    const sent = captureBeacons();
    expect(reportAbrDowngrade({ fromBps: Infinity, toBps: 4_000_000, estimateBps: 5_200_000 })).toBe(true);
    expect(sent).toHaveLength(1);
    return sent[0].blob.text().then((text) => {
      const body = JSON.parse(text);
      expect(body.kind).toBe("abr-downgrade");
      expect(body.summary).toBe("dropped Original → 4 Mbps on ~5.2 Mbps");
    });
  });

  it("lets hls.js retry a load failure before calling it an incident", () => {
    // hls.js retries manifests and fragments on its own and usually wins; one failure is noise.
    const sent = captureBeacons();
    expect(notePlaylistError({ details: "fragLoadError", code: 502 })).toBe(false);
    expect(notePlaylistError({ details: "fragLoadError", code: 502 })).toBe(false);
    expect(notePlaylistError({ details: "fragLoadError", code: 502 })).toBe(true);
    expect(sent).toHaveLength(1);
  });

  it("reports a FATAL load failure immediately", () => {
    const sent = captureBeacons();
    expect(notePlaylistError({ details: "manifestLoadError", code: 504, fatal: true })).toBe(true);
    expect(sent).toHaveLength(1);
  });
});
