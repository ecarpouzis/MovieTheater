// LRC parsing for the lyrics pane (music-plan.md §2.7). Kept as pure functions so the timing
// behaviour is unit-testable without an audio element.
//
// An LRC line is one or more timestamps followed by the text they apply to:
//   [00:12.34]Hello            → one line at 12.34s
//   [00:12.34][01:20.00]Hello  → the SAME text at two times (repeated chorus)
// Metadata tags ([ar:…], [ti:…], [offset:…]) carry no numeric timestamp and are skipped, so a file
// that is only metadata parses to zero lines and the caller falls back to plain text.

const TIMESTAMP = /\[(\d+):(\d{1,2}(?:[.:]\d{1,3})?)\]/g;

/**
 * @param {string} lrc raw LRC text
 * @returns {{time:number,text:string}[]} lines sorted by time; blank-text lines are kept (they're
 *   the instrumental gaps, and dropping them makes the highlight stick on the previous line).
 */
export function parseLrc(lrc) {
  if (!lrc || typeof lrc !== "string") return [];
  const out = [];

  for (const raw of lrc.split(/\r?\n/)) {
    TIMESTAMP.lastIndex = 0;
    const times = [];
    let end = 0;
    let match;
    while ((match = TIMESTAMP.exec(raw)) !== null) {
      // Timestamps must be a contiguous run at the START of the line; anything after the text is a
      // literal bracket, not a cue.
      if (match.index !== end) break;
      const minutes = parseInt(match[1], 10);
      const seconds = parseFloat(match[2].replace(":", "."));
      if (Number.isFinite(minutes) && Number.isFinite(seconds)) times.push(minutes * 60 + seconds);
      end = match.index + match[0].length;
    }
    if (times.length === 0) continue;

    const text = raw.slice(end).trim();
    for (const time of times) out.push({ time, text });
  }

  out.sort((a, b) => a.time - b.time);
  return out;
}

/**
 * Index of the line that should be highlighted at `time` — the last one whose cue has passed.
 * Returns -1 before the first cue (nothing highlighted during an intro).
 */
export function activeLineIndex(lines, time) {
  if (!lines || lines.length === 0 || !Number.isFinite(time)) return -1;
  let lo = 0;
  let hi = lines.length - 1;
  let found = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (lines[mid].time <= time) {
      found = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  return found;
}
