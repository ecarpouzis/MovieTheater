// coverAspect — the width/height of a game's box art, which the browse grid needs BEFORE it can
// reserve the cover's slot (design README: "each cover renders at its natural aspect on a shared
// 118px height"). Real box art ranges from ~3:4 jewel cases to ~4:3 cartridge boxes.
//
// The DB doesn't store cover dimensions — /ArcadeImage/{id} lazily fetches and caches a 300px thumb,
// and no width/height column exists. So the image itself is the source: on load we read
// naturalWidth/naturalHeight and remember it, keyed by the card's artId. Once measured, the aspect
// survives re-renders, later pages, and back-navigation, so a cover only ever settles its width once
// per session. Everything is best-effort: a card with no remembered aspect just sizes itself from the
// image as it decodes.
const KEY = "arcade.coverAspect";

/** @type {Map<number, number>} artId → width/height */
const cache = new Map();

// Rehydrate from sessionStorage (not localStorage: art can be re-fetched/replaced server-side, and a
// stale ratio would mis-size a card for good). Corrupt/absent payload just starts empty.
try {
  const raw = window.sessionStorage.getItem(KEY);
  if (raw) {
    for (const [id, aspect] of Object.entries(JSON.parse(raw))) {
      const n = Number(aspect);
      if (Number.isFinite(n) && n > 0) cache.set(Number(id), n);
    }
  }
} catch {
  /* ignore — an unreadable cache just means we measure again */
}

// Writing on every image load would hammer sessionStorage during a 60-card page paint, so coalesce.
let flushHandle = null;
function scheduleFlush() {
  if (flushHandle != null) return;
  flushHandle = setTimeout(() => {
    flushHandle = null;
    try {
      window.sessionStorage.setItem(KEY, JSON.stringify(Object.fromEntries(cache)));
    } catch {
      /* quota / private mode — the in-memory cache still works for this page */
    }
  }, 500);
}

/** The remembered width/height for a card's art, or null if we've never seen it load. */
export function getCoverAspect(artId) {
  const a = cache.get(artId);
  return a > 0 ? a : null;
}

/** Record a cover's measured width/height. Ignores the degenerate 0×0 of a broken decode. */
export function rememberCoverAspect(artId, width, height) {
  if (!artId || !(width > 0) || !(height > 0)) return null;
  const aspect = width / height;
  if (cache.get(artId) === aspect) return aspect;
  cache.set(artId, aspect);
  scheduleFlush();
  return aspect;
}
