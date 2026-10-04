// ── Which PeerConnection failures the room page hears about ─────────────────────────────────────────────
// The room page counts a "failed"/"disconnected" in a session's first seconds as a CRASH (a worker that dies at
// core load), and stops the room with "This game keeps crashing" on the second one. That is right for a
// connection that was up and then died. It is wrong for a connection that has never been up.
//
// 2026-10-04, relay rooms that "never connect": with trickle ICE a PeerConnection can pass through "failed" for
// a few milliseconds BEFORE it has ever connected — every candidate pair it knows so far has failed, and the
// one that will work has not arrived yet. (The TURN relay answered 403 for the worker's VPN and CGNAT
// candidates, which trickle in before its LAN one; Chrome fails such a pair instantly, so connectionState went
// connecting -> failed twice inside 10 ms and the page killed a room that was about to connect.) The relay no
// longer does that, but nothing guarantees the order candidates arrive in, on any network.
//
// The rule: once the connection has been "connected", failed/disconnected are reported at once, as before.
// Until then they are reported only if the connection is STILL in that state after settleMs — a connection
// that really cannot be made still fails, a few seconds later, and one that was only waiting for its next
// candidate never reports at all.

export const PRECONNECT_SETTLE_MS = 3000;

const isDown = (s) => s === "failed" || s === "disconnected";

/**
 * createConnectionStateGate({ report, current }) → { onState(s), everConnected(), cancel() }
 *   report(s)  — tell the page ("failed" | "disconnected")
 *   current()  — the PeerConnection's state right now (read when the settle timer fires)
 */
export function createConnectionStateGate({ report, current, settleMs = PRECONNECT_SETTLE_MS,
  setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
  let connected = false;
  let timer = null;
  const stop = () => { if (timer != null) { clearTimer(timer); timer = null; } };
  return {
    onState(s) {
      if (s === "connected") { connected = true; stop(); return; }
      if (!isDown(s)) return;
      if (connected) { report(s); return; }
      if (timer != null) return; // one pending verdict; it reads the state when it fires
      timer = setTimer(() => {
        timer = null;
        const now = current();
        if (!connected && isDown(now)) report(now);
      }, settleMs);
    },
    everConnected: () => connected,
    cancel: stop,
  };
}
