/**
 * Play telemetry for the music player — the data behind "Most played" (R9 closing pass).
 *
 * Until this shipped the vertical recorded no plays at all, so the sort the plan asked for had
 * nothing to sort on. The rule it implements is deliberately narrow, because a play count that
 * counts the wrong things is worse than none:
 *
 * - **A play is a track that got LISTENED to**, not one that got started. The threshold is 30 s or
 *   half the track, whichever comes first — which is the right shape at both ends: a 40-second
 *   interlude counts at 20 s, a nine-minute side counts at 30 s. Skipping through a queue looking
 *   for something records nothing.
 * - **Once per play, and a seek is not a play.** The reporter holds ONE session, opened when the
 *   player moves to a track and closed by reporting; scrubbing backwards past the threshold and
 *   forwards again cannot fire it twice, because the session is already reported. Putting the same
 *   record on again later is a new session, which is correct — and the server tells the two apart
 *   by the minute playback started.
 * - **Fire and forget.** The send is a `sendBeacon` with `text/plain` (CORS-simple on purpose, the
 *   same reason `/API/Music/Incident` is: the last one goes from `pagehide`, when the page is being
 *   frozen and will not survive a preflight). A refused beacon keeps its report and tries again on
 *   the next one or at `pagehide`, and the endpoint is idempotent per user × track × started-at
 *   minute, so a duplicate costs nothing.
 *
 * The module is pure apart from `beaconSend`, so the rules above are testable without a player.
 */

/** A play is 30 s of listening… */
export const PLAY_THRESHOLD_SEC = 30;
/** …or half the track, whichever comes first (so a short track is not unreportable). */
export const PLAY_THRESHOLD_FRACTION = 0.5;
/** One flush carries at most this many — the server caps at the same number. */
export const MAX_PLAYS_PER_SEND = 50;

export const PLAY_ENDPOINT = "/API/Music/Play";

/** Has this playhead position earned a play for a track of this length? */
export function playThresholdReached(position, duration) {
  const pos = Number(position);
  if (!Number.isFinite(pos) || pos <= 0) return false;
  if (pos >= PLAY_THRESHOLD_SEC) return true;
  const dur = Number(duration);
  if (!Number.isFinite(dur) || dur <= 0) return false;
  return pos >= dur * PLAY_THRESHOLD_FRACTION;
}

/**
 * The default sender: a CORS-simple beacon. Returns whether the browser accepted it for delivery —
 * `false` means "still ours to retry", which is what keeps a refused report in the queue.
 */
export function beaconSend(payload) {
  const body = JSON.stringify(payload);
  try {
    if (typeof navigator !== "undefined" && typeof navigator.sendBeacon === "function") {
      const blob = typeof Blob === "function" ? new Blob([body], { type: "text/plain" }) : body;
      return navigator.sendBeacon(PLAY_ENDPOINT, blob) === true;
    }
    if (typeof fetch === "function") {
      // No beacon: keepalive still lets the request outlive the page in most engines.
      fetch(PLAY_ENDPOINT, { method: "POST", body, keepalive: true, credentials: "include", headers: { "Content-Type": "text/plain" } })
        .catch(() => {});
      return true;
    }
  } catch {
    // A send that throws is a send that did not happen — keep the report.
  }
  return false;
}

/**
 * The reporter. `begin` opens a session for the track the player moved to; `note` is fed the
 * playhead and decides; `flush` re-sends whatever a refused beacon left behind (the `pagehide` call).
 */
export function createPlayReporter({ send = beaconSend, now = () => Date.now() } = {}) {
  let session = null;
  const pending = [];

  /** The player moved to a track. A null id closes the session without reporting anything. */
  function begin(trackId, startedAt) {
    session = trackId == null ? null : { trackId, startedAt: startedAt ?? now(), reported: false };
  }

  function flush() {
    if (pending.length === 0) return false;
    const batch = pending.slice(0, MAX_PLAYS_PER_SEND);
    const ok = send({ plays: batch });
    if (ok) pending.splice(0, batch.length);
    return ok;
  }

  /** Feed the playhead. Returns true only on the tick that actually recorded the play. */
  function note(position, duration) {
    if (!session || session.reported) return false;
    if (!playThresholdReached(position, duration)) return false;
    session.reported = true;
    pending.push({ trackId: session.trackId, startedAt: new Date(session.startedAt).toISOString() });
    flush();
    return true;
  }

  return {
    begin,
    note,
    flush,
    /** Test seam: what a refused beacon is still holding. */
    pending: () => pending.slice(),
    /** Test seam: the open session, if any. */
    session: () => (session ? { ...session } : null),
  };
}
