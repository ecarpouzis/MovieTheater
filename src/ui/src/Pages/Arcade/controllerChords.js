// Chord/hold-to-fire bindings: hold a combination of RetroPad bits for a bit to trigger a
// site-level action (quick-save / quick-load / rewind / fast-forward / reset) instead of using
// the on-screen buttons. Matches against the SAME final input mask cloudRetroClient.js already
// sends over the wire (keyboard bits OR'd with gamepad bits), so a chord works identically
// whether it's held on a gamepad, a keyboard, or a mix of both — no separate keyboard-combo
// logic needed.
//
// This one array IS "the default everyone gets" — editing it and redeploying is the whole
// admin story for changing what's bound to what (matches the existing PROFILES/faceSwap
// convention: a single-admin hobby project doesn't need a DB-backed config system for this).
//
// Two chord shapes:
//   one-shot (default) — fires onFire(action, true) once when the combo has been held holdMs.
//   hold (hold: true)  — engages with onFire(action, true) after holdMs, then fires
//                        onFire(action, false) the moment the combo breaks. Rewind and
//                        fast-forward are held experiences (t=115/114 {active}), not events.
//
// Both kinds report their mask while ENGAGED/fired via poll()'s return value so the input pump
// can strip those bits from the wire — once fast-forward is engaged the game should stop seeing
// Select+East held (Select opens a menu in half the SNES library). The pre-engage window still
// leaks its bits, unavoidably: until holdMs elapses it may yet be ordinary gameplay input.
import { PAD } from "./cloudRetroClient";

function bitsToMask(bitNames) {
  return bitNames.reduce((mask, name) => mask | (1 << PAD[name]), 0);
}

// SELECT is the de-facto hotkey modifier: it's on every pad, and it's the button with the least
// in-game meaning. Face-button names are RetroPad bits (the mapping OUTPUT): Y = west, A = east
// on a standard pad, regardless of the per-system profile's physical relabeling.
export const DEFAULT_CHORDS = [
  { action: "quickSave", bits: ["SELECT", "R3"], holdMs: 600 },
  { action: "quickLoad", bits: ["SELECT", "L3"], holdMs: 600 },
  { action: "rewind", bits: ["SELECT", "Y"], holdMs: 150, hold: true },
  { action: "fastForward", bits: ["SELECT", "A"], holdMs: 150, hold: true },
  { action: "reset", bits: ["SELECT", "START", "L2", "R2"], holdMs: 900 },
];

/**
 * Merge user-chosen button combos over the shipped defaults. `customBitsByAction` is
 * { quickSave: ["SELECT","B"], quickLoad: [...], ... } — a set of RetroPad bit NAMES per action, as
 * captured in the controller tool. A missing/empty/all-invalid entry keeps that action's default (a
 * chord needs at least one real bit). holdMs and the hold/one-shot shape are NOT user-editable —
 * the timing and semantics stay the default.
 * Returns the same shape DEFAULT_CHORDS has, ready for createChordWatcher.
 */
export function resolveChords(customBitsByAction) {
  const custom = customBitsByAction || {};
  return DEFAULT_CHORDS.map((d) => {
    const raw = custom[d.action];
    const bits = Array.isArray(raw) ? raw.filter((n) => typeof n === "string" && n in PAD) : null;
    return bits && bits.length > 0 ? { ...d, bits } : d;
  });
}

/**
 * Build a poll(mask, now) function that fires `onFire(action, engaged)` once a chord has been held
 * continuously for its holdMs (engaged=true; hold-type chords additionally fire engaged=false when
 * released). Chords whose bits are a SUBSET of another currently-satisfied chord are suppressed (a
 * "specificity claim" pass, most-bits-first) so a chord can't fire while it's a strict subset of a
 * chord that's also satisfied. A chord's own `fired` latch clears only when ITS bits are fully
 * released (independent of suppression), so it can't re-fire while still held.
 *
 * poll returns the OR'd mask of every currently-FIRED chord — the bits the input pump should strip
 * from the frame it sends, so an engaged chord stops leaking its buttons into the game.
 */
export function createChordWatcher(onFire, chords = DEFAULT_CHORDS) {
  const compiled = chords
    .map((c) => ({ ...c, mask: bitsToMask(c.bits) }))
    .sort((a, b) => b.bits.length - a.bits.length)
    .map((c) => ({ ...c, heldSinceMs: null, fired: false }));

  function poll(mask, now) {
    let claimedBits = 0;
    let firedBits = 0;
    for (const c of compiled) {
      const satisfied = (mask & c.mask) === c.mask;
      const suppressed = satisfied && (c.mask & claimedBits) !== 0;
      if (satisfied && !suppressed) {
        if (c.heldSinceMs == null) c.heldSinceMs = now;
        if (!c.fired && now - c.heldSinceMs >= c.holdMs) {
          c.fired = true;
          onFire(c.action, true);
        }
        claimedBits |= c.mask;
      } else {
        c.heldSinceMs = null;
        // A hold-type chord that was engaged releases the moment its combo breaks — whether by
        // the player letting go (unsatisfied) or by a bigger chord claiming its bits (suppressed).
        if (c.fired && c.hold) {
          c.fired = false;
          onFire(c.action, false);
        }
      }
      if (!satisfied) c.fired = false;
      if (c.fired) firedBits |= c.mask;
    }
    return firedBits;
  }

  return { poll };
}
