// Pure, testable scoring for the Rate Movies page.
//
// The page is a single top→bottom ordered list that mixes movie bars and user-placed "anchor" bars.
// A movie's 0–100 score is extrapolated from its rank position between the bracketing anchors:
//   - Implicit endpoints: the top of the list is 100 and the bottom is 0 unless an anchor overrides them.
//   - An anchor pegs an absolute value; the movies between two bracket values spread EVENLY across the
//     open interval (the run never lands exactly on an anchor's value).
//   - Anchor values are clamped monotonic non-increasing down the list (a lower anchor can't outrank a
//     higher one), matching the best-at-top layout.
//
// orderedItems: Array<{ type: 'movie', key } | { type: 'anchor', value: number }>, ordered top → bottom.
// Returns Map<movieKey, integerScore in [0,100]>. O(n), no side effects.
export function computeScores(orderedItems, { top = 100, bottom = 0 } = {}) {
  const scores = new Map();
  if (!Array.isArray(orderedItems)) return scores;

  let upper = top; // the bracket value above the current run; also the monotonic ceiling for anchors
  let run = []; // movie keys accumulated since the last bracket, top → bottom

  const flush = (lower) => {
    const gap = upper - lower;
    const n = run.length;
    for (let i = 0; i < n; i++) {
      // i = 0 is the highest-ranked movie in this run; spread evenly in the open interval (lower, upper).
      const raw = upper - (gap * (i + 1)) / (n + 1);
      scores.set(run[i], clampRound(raw));
    }
    run = [];
  };

  for (const item of orderedItems) {
    if (item && item.type === "anchor") {
      const v = Math.max(bottom, Math.min(upper, toNum(item.value))); // clamp ⇒ non-increasing
      flush(v);
      upper = v;
    } else if (item && item.type === "movie") {
      run.push(item.key);
    }
  }
  flush(bottom); // trailing run spreads down to the implicit bottom
  return scores;
}

// The value an anchor effectively pegs to, after the same monotonic clamp computeScores applies — so the
// UI can show the clamped number while editing, keeping display and computation in agreement.
export function effectiveAnchorValues(orderedItems, { top = 100, bottom = 0 } = {}) {
  const out = new Map();
  if (!Array.isArray(orderedItems)) return out;
  let upper = top;
  for (const item of orderedItems) {
    if (item && item.type === "anchor") {
      const v = Math.max(bottom, Math.min(upper, toNum(item.value)));
      out.set(item.id ?? item.key ?? item, v);
      upper = v;
    }
  }
  return out;
}

function toNum(v) {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
}

function clampRound(x) {
  return Math.min(100, Math.max(0, Math.round(x)));
}
