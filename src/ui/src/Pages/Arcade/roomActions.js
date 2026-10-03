import { HEAVY_LANE_SYSTEMS, hasSaveStates } from "./arcadeSystems";

/**
 * Which room actions this seat may take right now — the ONE gate shared by the pad chords, the room's
 * buttons, the touch pad's action buttons and the in-player overlay menu, so no route can offer what
 * another refuses.
 *
 * - Fast-forward is pacing-only: every libretro-lane system has it; the heavy/capture lanes stream a
 *   native app with no retro_run to pace.
 * - Rewind is answered by the SERVER (descriptor.canRewind): the ring is armed per CORE, and which core a
 *   room booted is decided server-side. An absent flag reads as "no" — an unarmed worker accepts t=115
 *   and silently does nothing.
 * - Competitive rooms withhold all of it: rewind and state loads are save-scumming, fast-forward is the
 *   time manipulation the leaderboards keep out.
 * - Reset is the room owner's (slot 0) — unrecoverable for everyone in a shared room.
 * - A spectator holds no controller port: nothing.
 *
 * @returns {{quickSave: boolean, quickLoad: boolean, rewind: boolean, fastForward: boolean, reset: boolean}}
 */
export function roomActionAvailability({ system, competitive, spectator, canRewind, slot } = {}) {
  const sys = String(system || "").toLowerCase();
  const player = !spectator;
  const open = player && !competitive;
  return {
    quickSave: open && hasSaveStates(sys),
    quickLoad: open && hasSaveStates(sys),
    rewind: open && !!canRewind,
    fastForward: open && !HEAVY_LANE_SYSTEMS.has(sys),
    reset: player && slot === 0,
  };
}

/** Why an action is refused, in the player's words (for the toast a chord or touch press gets instead). */
export function refusalMessage(action, { system, competitive, spectator, slot } = {}) {
  if (spectator) return "You're watching — the controls belong to the players.";
  if (action === "reset") return slot === 0 ? null : "Only the room owner can reset.";
  if (competitive) {
    return action === "rewind" || action === "fastForward"
      ? "Competitive room — time controls are off."
      : "Competitive room — save states are off.";
  }
  if (action === "quickSave" || action === "quickLoad") {
    return hasSaveStates(system) ? null : "This system has no save states — your progress saves inside the game itself.";
  }
  if (action === "rewind") return "Rewind isn't available for this system.";
  if (action === "fastForward") return "Fast-forward isn't available for this system.";
  return null;
}

/** Actions held down (engage on press, release on lift) rather than fired once. */
export const HOLD_ACTIONS = new Set(["rewind", "fastForward"]);
