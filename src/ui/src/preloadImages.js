// Warm the browser's HTTP cache for posters we know we'll show soon, so a lazy <img> renders straight
// from cache instead of fetching (and visibly snapping in) the moment it scrolls into view. Poster
// thumbnail URLs are versioned (?v=) and served `immutable`, so a detached Image() fetch is cached and
// reused by the real <img> with the same src. Deduped across calls (the same URL never re-fetches), and
// low priority by default so preloading never competes with what's currently on screen.
const requested = new Set();

export function preloadImages(urls, priority = "low") {
  if (!urls) return;
  for (const url of urls) {
    if (!url || requested.has(url)) continue;
    requested.add(url);
    const img = new Image();
    // Honored where supported (Chromium); harmlessly ignored elsewhere.
    try { img.fetchPriority = priority; } catch { /* older browsers */ }
    img.decoding = "async";
    img.src = url;
  }
}
