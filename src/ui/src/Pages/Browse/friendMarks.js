import { useEffect, useState } from "react";
import { readTweaks, subscribeTweaks } from "../../catalog/tweaks/useTweaks";
import { sameUser } from "../../hooks/useUserLists";

/**
 * The ⚙ panel's "Friends’ marks" lever (2026-09-05) and the index the cards read.
 *
 * Off · Wants only · Seen + wants. "Wants only" is the actionable half — who to watch it with, whom
 * not to suggest it to — and stays calm on popular titles; seen-by-everyone is the noisy half, and it
 * always remains in the sheet's line and the tooltip. Device-scoped like every tweak (never per user).
 */
export const FRIEND_MARKS_KEY = "friendMarks";
export const FRIEND_MARKS_DEFAULT = "all";
export const FRIEND_MARKS_EXTRA = {
  key: FRIEND_MARKS_KEY,
  label: "Friends’ marks",
  options: [
    { value: "off", label: "Off" },
    { value: "want", label: "Wants only" },
    { value: "all", label: "Seen + wants" },
  ],
};

export function friendMarksModeOf(tweaks) {
  const v = tweaks?.extras?.[FRIEND_MARKS_KEY];
  return v === "off" || v === "want" || v === "all" ? v : FRIEND_MARKS_DEFAULT;
}

/** The lever's current value for the movies section, live (the panel writes; this hears it). */
export function useFriendMarksMode(section = "movies") {
  const [mode, setMode] = useState(() => friendMarksModeOf(readTweaks(section)));
  useEffect(() => {
    setMode(friendMarksModeOf(readTweaks(section)));
    return subscribeTweaks(section, () => setMode(friendMarksModeOf(readTweaks(section))));
  }, [section]);
  return mode;
}

/**
 * title id → { seen: [names], want: [names] } over everybody but the viewer, under the lever. Built
 * once per change of the communal copy (O(total ids)), so a card's lookup is one Map get.
 */
export function buildMarksIndex(peers, viewerUsername, mode = FRIEND_MARKS_DEFAULT) {
  const index = new Map();
  if (mode === "off" || !Array.isArray(peers)) return index;
  const bucket = (id, list) => {
    let e = index.get(id);
    if (!e) index.set(id, (e = { seen: [], want: [] }));
    return e[list];
  };
  for (const p of peers) {
    if (!p || sameUser(p.username, viewerUsername)) continue;
    if (mode === "all") for (const id of p.moviesSeen ?? []) bucket(id, "seen").push(p.username);
    for (const id of p.moviesToWatch ?? []) bucket(id, "want").push(p.username);
  }
  return index;
}
