// Chord/hold-to-fire bindings: hold a combination of RetroPad bits for a bit to trigger a
// site-level action (quick-save / quick-load / reset) instead of using the on-screen buttons.
// Matches against the SAME final input mask cloudRetroClient.js already sends over the wire
// (keyboard bits OR'd with gamepad bits), so a chord works identically whether it's held on a
// gamepad, a keyboard, or a mix of both — no separate keyboard-combo logic needed.
//
// This one array IS "the default everyone gets" — editing it and redeploying is the whole
// admin story for changing what's bound to what (matches the existing PROFILES/faceSwap
// convention: a single-admin hobby project doesn't need a DB-backed config system for this).
//
// Fast-forward is deliberately NOT bound here: there is no wire packet type or worker-side
// RetroArch hook for it yet (unlike quick-save/quick-load/reset, which already ride existing
// GAME_SAVE/GAME_LOAD/GAME_RESET packets) — that's separate, larger backend work.
import { PAD } from "./cloudRetroClient";

function bitsToMask(bitNames) {
  return bitNames.reduce((mask, name) => mask | (1 << PAD[name]), 0);
}

export const DEFAULT_CHORDS = [
  { action: "quickSave", bits: ["L3", "R3"], holdMs: 600 },
  { action: "quickLoad", bits: ["L3", "R3", "SELECT"], holdMs: 600 },
  { action: "reset", bits: ["SELECT", "START", "L2", "R2"], holdMs: 900 },
];

/**
 * Build a poll(mask, now) function that fires `onFire(action)` once a chord has been held
 * continuously for its holdMs. Chords whose bits are a SUBSET of another currently-satisfied
 * chord are suppressed (a "specificity claim" pass, most-bits-first) so e.g. holding L3+R3+SELECT
 * fires quickLoad only — quickSave's clock never starts while it's a strict subset of a chord
 * that's also satisfied. A chord's own `fired` latch clears only when ITS bits are fully
 * released (independent of suppression), so it can't re-fire while still held.
 */
export function createChordWatcher(onFire, chords = DEFAULT_CHORDS) {
  const compiled = chords
    .map((c) => ({ ...c, mask: bitsToMask(c.bits) }))
    .sort((a, b) => b.bits.length - a.bits.length)
    .map((c) => ({ ...c, heldSinceMs: null, fired: false }));

  function poll(mask, now) {
    let claimedBits = 0;
    for (const c of compiled) {
      const satisfied = (mask & c.mask) === c.mask;
      const suppressed = satisfied && (c.mask & claimedBits) !== 0;
      if (satisfied && !suppressed) {
        if (c.heldSinceMs == null) c.heldSinceMs = now;
        if (!c.fired && now - c.heldSinceMs >= c.holdMs) {
          c.fired = true;
          onFire(c.action);
        }
        claimedBits |= c.mask;
      } else {
        c.heldSinceMs = null;
      }
      if (!satisfied) c.fired = false;
    }
  }

  return { poll };
}
