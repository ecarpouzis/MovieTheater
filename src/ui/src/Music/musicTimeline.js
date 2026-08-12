// ── Phase 3 of music-mse-plan.md: elementTime ⇄ (trackId, offsetSec) ─────────────────────────────
//
// The engine puts the WHOLE QUEUE in one SourceBuffer, so the element has exactly one clock and it
// counts queue-seconds. Every consumer that used to read `audio.currentTime` and mean "how far into
// this song" is wrong under the flag: the play bar reads 43-minute positions, the lock screen shows
// a 43-minute track, the lyrics scroll to the wrong line, and a scrub jumps to an arbitrary song.
//
// This module is the single mapping between the two. The plan calls it "the real surface area", and
// the reason it is one module rather than an offset subtracted in three places is that the offset
// changes at every boundary AND is corrected from the buffer — three copies would be three chances
// to drift, in code that only misbehaves while nobody is looking at it.
//
// Everything here is pure. `entries` are the engine's append records:
//   { trackId, startSec, durationSec, complete }
// `startSec` is read off the SourceBuffer at append time — buffered-corrected, not summed from DB
// durations, because over an hours-long queue that sum drifts and lyrics are the consumer that
// notices a boundary sliding mid-line.

/**
 * How long the track at `index` runs for, on the ELEMENT's clock.
 *
 * The minted payload's duration is the best answer and is used when present. The fallback — the
 * distance to the next entry's start — is better than it looks: it is measured from the buffer, so
 * it accounts for whatever the encoder actually produced, and it is the only answer available for a
 * track whose metadata never had a duration.
 */
export function entryDurationSec(entries, index) {
  const list = entries || [];
  const entry = list[index];
  if (!entry) return 0;
  const next = list[index + 1];
  if (next && next.startSec > entry.startSec) return next.startSec - entry.startSec;
  return Number(entry.durationSec) || 0;
}

/**
 * Element clock → (track, offset). The mapping every consumer goes through.
 *
 * Unaffected by eviction on purpose: `remove()` drops bytes but does NOT shift the timeline, so an
 * entry's startSec stays true for the whole session even after everything before it is gone. (A
 * mapping that tracked `buffered.start()` instead would slide every position forward by whatever had
 * been evicted — the bug this note exists to prevent.)
 */
export function trackTimeAt(entries, elementTime) {
  const list = entries || [];
  const t = Number.isFinite(elementTime) ? elementTime : 0;
  let found = -1;
  for (let i = 0; i < list.length; i++) {
    if (list[i].startSec <= t + 0.05) found = i;
    else break;
  }
  if (found < 0) return null;
  const entry = list[found];
  const durationSec = entryDurationSec(list, found);
  return {
    trackId: entry.trackId,
    index: found,
    startSec: entry.startSec,
    // Clamped into the track: a playhead a hair past the last append must not read as 0:00 of a
    // track that has not started, nor as a position past a duration the bar would render as a
    // thumb off the end of its own slider.
    offsetSec: Math.max(0, durationSec > 0 ? Math.min(t - entry.startSec, durationSec) : t - entry.startSec),
    durationSec,
  };
}

/** (track, offset) → element clock. Null when that track is not in the buffer's plan at all. */
export function elementTimeFor(entries, trackId, offsetSec) {
  const list = entries || [];
  const index = list.findIndex((e) => e.trackId === trackId);
  if (index < 0) return null;
  const duration = entryDurationSec(list, index);
  const offset = Math.max(0, duration > 0 ? Math.min(offsetSec || 0, duration) : offsetSec || 0);
  return list[index].startSec + offset;
}

/**
 * What a seek can actually do, given where the bytes are.
 *
 * ⚠ The physics, stated once so no caller has to rediscover it: the fMP4 and universal lanes are
 * piped ffmpeg stdout with no Range support, so there is no way to fetch "this track from 2:30".
 * A seek to a position that is not in the buffer therefore CANNOT be honoured as a mid-track
 * position — the honest options are to restart the engine at that track (it begins at 0:00) or to
 * refuse. Restarting is what a manual jump already does and keeps the session on MSE, so that is
 * the choice; the caller logs it, and the bar snapping to 0:00 is the truth rather than a glitch.
 *
 * What this must NEVER do is the thing that would look like a fix: assign a `src`, or append at a
 * position the SourceBuffer is not expecting. Either corrupts the buffer for the rest of the queue.
 */
export function seekPlan({ entries, bufferedStart = 0, bufferedEnd = 0, trackId, offsetSec }) {
  const list = entries || [];
  const index = list.findIndex((e) => e.trackId === trackId);
  if (index < 0) return { kind: "unavailable", reason: "this track is not in the buffer" };
  const target = elementTimeFor(list, trackId, offsetSec);
  if (target == null) return { kind: "unavailable", reason: "this track is not in the buffer" };
  // A hair inside the far edge: seeking exactly to bufferedEnd lands on nothing and stalls.
  if (target >= bufferedStart && target <= Math.max(bufferedStart, bufferedEnd - 0.25)) {
    return { kind: "inBuffer", elementTime: target, trackId, offsetSec };
  }
  return {
    kind: "restart",
    trackId,
    // Named because they are different stories: "evicted" is memory the plan spent deliberately,
    // "not appended yet" is simply the future.
    reason: target < bufferedStart ? "seek target was evicted" : "seek target is not appended yet",
  };
}

/**
 * The queue-end guard (Phase 4, from the field run 2026-08-11).
 *
 * After `endOfStream()` the element drained its last 161 s and then fired **`waiting`, not `ended`**,
 * at t=2586 with readyState CURRENT — and the session sat there. Harmless at the end of a queue,
 * but the cross-engine hand-off RELIES on that `ended` arriving after endOfStream(): a deck that was
 * pre-rolled for the boundary would wait forever for a flip that never came, which on a sleeping
 * phone is silence for the rest of the night.
 *
 * So a stall signal, on a stream we have already ended, with the playhead at the very end of what is
 * buffered, IS the end — whatever the browser chose to call it.
 */
export function isQueueEndStall({ endedStream, currentTime, bufferedEnd, toleranceSec = 1 }) {
  if (!endedStream) return false;
  if (!(bufferedEnd > 0)) return false;
  return (currentTime || 0) >= bufferedEnd - toleranceSec;
}
