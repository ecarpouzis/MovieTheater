/**
 * Showing how widely heard a song is, and — the harder half — how far it drops from the biggest
 * song beside it (2026-08-31).
 *
 * THE PROBLEM THIS SOLVES. `popularity` is 0–100 and LOGARITHMIC, because listener counts across
 * the library span three orders of magnitude and a linear map would put everything except a dozen
 * megahits in the bottom two points. That scale is right for ordering and for a card badge, and it
 * is actively misleading about DROPS: on one real album 73 and 50 are 112,303 listeners and 2,905,
 * a 39× difference that reads as "23 points". Drawing a bar from the score would make a hit and a
 * deep cut look like near-neighbours.
 *
 * So the two channels carry different things, deliberately:
 *   • the NUMBER is the 0–100 score — absolute, comparable with every other popularity on the site
 *     (the album badge, the Sort pill), and the thing you read to compare two songs precisely;
 *   • the BAR is this song's share of the LOUDEST song in the list it appears in, computed from raw
 *     listeners so the collapse is shown at true scale.
 *
 * A bar that is relative to its list is only honest because the number beside it is not.
 */

/** The biggest audience in a list, as the bar's 100%. Null when nothing in the list is known. */
export function peakOf(tracks) {
  let listeners = 0;
  let popularity = 0;
  for (const t of tracks ?? []) {
    if (typeof t?.listeners === "number" && t.listeners > listeners) listeners = t.listeners;
    if (typeof t?.popularity === "number" && t.popularity > popularity) popularity = t.popularity;
  }
  if (!listeners && !popularity) return null;
  return { listeners, popularity };
}

/**
 * How long this song's bar is, 0–1, against the list's peak.
 *
 * Raw listeners when both ends are known — that is the whole point, and it is what makes one hit on
 * an album of deep cuts look like one hit on an album of deep cuts. Falls back to the score ratio
 * when the counts are missing (an older shelf, or a track the enrich pass reached before the counts
 * were banked), which understates the drop but never invents one.
 */
export function shareOf(track, peak) {
  if (!peak) return 0;
  const listeners = typeof track?.listeners === "number" ? track.listeners : null;
  if (listeners != null && peak.listeners > 0) {
    return clamp01(listeners / peak.listeners);
  }
  const score = typeof track?.popularity === "number" ? track.popularity : null;
  if (score != null && peak.popularity > 0) return clamp01(score / peak.popularity);
  return 0;
}

function clamp01(n) {
  if (!Number.isFinite(n)) return 0;
  return Math.max(0, Math.min(1, n));
}

/**
 * A listener count short enough to sit in a tooltip: 112,303 → "112K", 4,210,229 → "4.2M".
 *
 * One decimal only in the millions, where it carries real information; "112.3K" is noise at a
 * glance and the exact figure is never what this is for.
 */
export function formatListeners(n) {
  if (typeof n !== "number" || !Number.isFinite(n) || n < 0) return null;
  if (n >= 1_000_000) {
    const m = n / 1_000_000;
    return `${m >= 10 ? Math.round(m) : m.toFixed(1)}M`;
  }
  if (n >= 1_000) return `${Math.round(n / 1_000)}K`;
  return String(n);
}

/**
 * The full sentence behind a row's meter. Says what the number IS, because "73" next to a song in a
 * house that also shows 0–100 ratings would otherwise read as a verdict on the song.
 */
export function popularityTitle(track, peak) {
  const score = typeof track?.popularity === "number" ? track.popularity : null;
  if (score == null) return undefined;
  const parts = [`Popularity ${score}/100 — how widely heard, not how good`];
  const listeners = formatListeners(track?.listeners);
  if (listeners) parts.push(`${listeners} listeners`);
  // The comparison the bar is actually drawing, spelled out — a share is meaningless without its
  // denominator, and "37% as many listeners as the biggest song here" is the answer to "how big a
  // drop is that".
  if (peak && typeof track?.listeners === "number" && peak.listeners > 0 && track.listeners < peak.listeners) {
    const pct = Math.round((track.listeners / peak.listeners) * 100);
    parts.push(`${pct}% of the most-heard song here`);
  }
  return parts.join(" · ");
}
