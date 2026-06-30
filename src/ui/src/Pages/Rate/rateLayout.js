// Reconstruct the Rate page's ordered item list from saved state, and diff computed scores for autosave.
import { computeScores } from "./computeScores";

export const movieKey = (card) => `${card.kind || "movie"}:${card.id}`;
export const anchorItemId = (a) => `anchor:${a.id}`;

function titleOf(card) {
  return (card.simpleTitle || card.title || "").toString();
}

function clampScore(v) {
  const n = Math.round(Number(v) || 0);
  return Math.min(100, Math.max(0, n));
}

function anchorItem(a) {
  return { type: "anchor", key: anchorItemId(a), id: a.id, value: clampScore(a.value) };
}

// Build the top→bottom ranked item list (movie + anchor bars) plus the "Unranked" tray, from:
//   cards    — every watched title's card ({ id, kind, title, simpleTitle, posterVersion, ... })
//   ratings  — userData.ratings, keyed "{kind}:{id}" → score (1..100)
//   anchors  — [{ id, value }] saved anchor bars
// Rated titles form the ranked list (score desc, tie-break by title); each anchor is re-inserted by its
// value (just above the first movie scoring below it), so positions follow the scores with no stored index.
export function reconstructLayout(cards, ratings, anchors) {
  const rated = [];
  const unranked = [];
  for (const c of cards || []) {
    const score = ratings?.[movieKey(c)];
    if (score == null) unranked.push(c);
    else rated.push({ card: c, score });
  }
  rated.sort((a, b) => b.score - a.score || titleOf(a.card).localeCompare(titleOf(b.card)));

  const sortedAnchors = [...(anchors || [])]
    .map((a) => ({ id: String(a.id), value: clampScore(a.value) }))
    .sort((a, b) => b.value - a.value);

  const items = [];
  let ai = 0;
  for (let i = 0; i < rated.length; i++) {
    // Drop any anchors whose value sits above this movie's score before placing the movie.
    while (ai < sortedAnchors.length && sortedAnchors[ai].value > rated[i].score) {
      items.push(anchorItem(sortedAnchors[ai]));
      ai++;
    }
    const c = rated[i].card;
    items.push({ type: "movie", key: movieKey(c), id: c.id, kind: c.kind || "movie", card: c });
  }
  // Anchors valued below every movie fall to the bottom.
  while (ai < sortedAnchors.length) {
    items.push(anchorItem(sortedAnchors[ai]));
    ai++;
  }

  return { items, unranked };
}

// Parse a movie key "{kind}:{id}" back into a rating write target.
function writeFromKey(key, value) {
  const idx = key.indexOf(":");
  return { id: Number(key.slice(idx + 1)), kind: key.slice(0, idx), value };
}

// Given the current ranked items and the last-saved baseline score map, return the minimal set of
// rating writes [{ id, kind, value }]: titles whose computed score changed, plus titles present in the
// baseline but no longer ranked (moved to the tray) emitted with value:null so the server clears them.
export function diffScores(currentItems, baselineScores) {
  const current = computeScores(currentItems);
  const writes = [];
  for (const [key, score] of current) {
    if (baselineScores.get(key) !== score) writes.push(writeFromKey(key, score));
  }
  for (const [key] of baselineScores) {
    if (!current.has(key)) writes.push(writeFromKey(key, null));
  }
  return { current, writes };
}

// The anchor payload to persist (bare array, matching the server's defensive parse).
export function anchorsToSave(items) {
  return (items || [])
    .filter((it) => it.type === "anchor")
    .map((it) => ({ id: String(it.id), value: clampScore(it.value) }));
}
