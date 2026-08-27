/**
 * The play beacon's rules (R9 closing pass). Everything here is a claim about what does NOT get
 * counted — a play count that counts the wrong things is worse than no play count, and each of
 * these is a way of getting it wrong: counting a skip, counting a scrub, counting a wake retry,
 * counting the same play twice because the beacon was refused and retried.
 */
import { describe, expect, it, vi } from "vitest";
import { createPlayReporter, playThresholdReached, PLAY_THRESHOLD_SEC } from "./musicPlays";

const reporter = (send) => createPlayReporter({ send, now: () => Date.parse("2026-08-27T12:00:30Z") });

describe("playThresholdReached", () => {
  it("is 30 seconds, or half the track, whichever comes first", () => {
    // A long side: the seconds win.
    expect(playThresholdReached(29, 540)).toBe(false);
    expect(playThresholdReached(PLAY_THRESHOLD_SEC, 540)).toBe(true);
    // A 40-second interlude would be unreportable on seconds alone — half of it counts.
    expect(playThresholdReached(19, 40)).toBe(false);
    expect(playThresholdReached(20, 40)).toBe(true);
  });

  it("never fires on a position or duration it cannot trust", () => {
    expect(playThresholdReached(0, 300)).toBe(false);
    expect(playThresholdReached(-5, 300)).toBe(false);
    expect(playThresholdReached(NaN, 300)).toBe(false);
    // No duration yet (an append still in flight): only the 30 s rule can decide.
    expect(playThresholdReached(10, 0)).toBe(false);
    expect(playThresholdReached(31, undefined)).toBe(true);
  });
});

describe("createPlayReporter", () => {
  it("reports a track ONCE, however many ticks pass the threshold", () => {
    const send = vi.fn(() => true);
    const r = reporter(send);
    r.begin(7);
    expect(r.note(29, 300)).toBe(false);
    expect(r.note(30, 300)).toBe(true);
    expect(r.note(31, 300)).toBe(false);
    expect(r.note(200, 300)).toBe(false);
    expect(send).toHaveBeenCalledTimes(1);
    expect(send.mock.calls[0][0]).toEqual({ plays: [{ trackId: 7, startedAt: "2026-08-27T12:00:30.000Z" }] });
  });

  it("a scrub back and forward inside the same play is not a second play", () => {
    const send = vi.fn(() => true);
    const r = reporter(send);
    r.begin(7);
    r.note(35, 300);
    // …the listener drags back to the start and lets it run past the threshold again.
    r.note(2, 300);
    r.note(40, 300);
    r.note(120, 300);
    expect(send).toHaveBeenCalledTimes(1);
  });

  it("counts nothing for a track skipped before the threshold", () => {
    const send = vi.fn(() => true);
    const r = reporter(send);
    r.begin(1); r.note(4, 300);
    r.begin(2); r.note(9, 300);
    r.begin(3); r.note(2, 300);
    expect(send).not.toHaveBeenCalled();
  });

  it("putting the same record on again is a new play", () => {
    const send = vi.fn(() => true);
    const r = reporter(send);
    r.begin(7); r.note(40, 300);
    r.begin(7); r.note(40, 300);
    expect(send).toHaveBeenCalledTimes(2);
  });

  it("re-opening the session for the same track is what makes a wake retry safe to NOT do", () => {
    // The player opens a session on the queue position, not on a load — proved here from the other
    // side: as long as `begin` is not called again, no number of ticks can report twice.
    const send = vi.fn(() => true);
    const r = reporter(send);
    r.begin(7);
    r.note(45, 300);
    for (let i = 0; i < 50; i += 1) r.note(45 + i, 300);
    expect(send).toHaveBeenCalledTimes(1);
  });

  it("keeps a refused beacon and hands it over at the next chance (pagehide)", () => {
    let accept = false;
    const send = vi.fn(() => accept);
    const r = reporter(send);
    r.begin(7);
    r.note(40, 300);
    expect(r.pending()).toEqual([{ trackId: 7, startedAt: "2026-08-27T12:00:30.000Z" }]);

    // Still refused on the next track's report: both are now waiting, in order.
    r.begin(8);
    r.note(40, 300);
    expect(r.pending().map((p) => p.trackId)).toEqual([7, 8]);

    accept = true;
    expect(r.flush()).toBe(true);
    expect(r.pending()).toEqual([]);
    expect(send).toHaveBeenLastCalledWith({ plays: [{ trackId: 7, startedAt: "2026-08-27T12:00:30.000Z" }, { trackId: 8, startedAt: "2026-08-27T12:00:30.000Z" }] });
    // Nothing left to say.
    expect(r.flush()).toBe(false);
  });

  it("a closed session reports nothing", () => {
    const send = vi.fn(() => true);
    const r = reporter(send);
    r.note(40, 300);          // never begun
    r.begin(7);
    r.begin(null);            // the queue emptied
    r.note(40, 300);
    expect(send).not.toHaveBeenCalled();
  });
});
