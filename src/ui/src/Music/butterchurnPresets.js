// The visualizer's preset library (music-plan.md §2.8).
//
// Presets are STATIC ASSETS, not a bundled module. `scripts/build-butterchurn-presets.mjs` publishes
// the whole butterchurn-presets corpus to public/butterchurn/ — an index plus one small JSON file
// per preset — so the app fetches a ~5 kB preset when it actually plays one, instead of pulling a
// 646 kB webpack bundle to get the 100-preset base pack. That's what makes ~1,750 presets practical.

const BASE = "/butterchurn";

// Tiers come from which pack of the upstream package a preset shipped in — see the build script for
// why that's the quality signal. The default pool is the vetted 395, not the whole archive: a random
// pick out of everything lands on a dud often enough to notice, and "shuffle" is the main way people
// meet these presets.
export const POOLS = [
  { id: "featured", label: "Featured", maxTier: 0 },
  { id: "classic", label: "Classic", maxTier: 1 },
  { id: "all", label: "Everything", maxTier: 2 },
  { id: "favorites", label: "Favorites", maxTier: 2 },
];
export const DEFAULT_POOL = "classic";

/** Winamp-era names are "author - title"; split on the FIRST " - " so titles keep their own dashes. */
export function splitPresetName(name) {
  const at = (name || "").indexOf(" - ");
  if (at < 0) return { author: "", title: name || "" };
  return { author: name.slice(0, at), title: name.slice(at + 3) };
}

/** Presets in `pool`, in index order. `favorites` is a Set/array of slugs. */
export function presetsInPool(presets, pool, favorites) {
  if (pool === "favorites") {
    const keep = favorites instanceof Set ? favorites : new Set(favorites || []);
    return presets.filter((p) => keep.has(p.s));
  }
  const maxTier = POOLS.find((p) => p.id === pool)?.maxTier ?? 2;
  return presets.filter((p) => p.t <= maxTier);
}

/**
 * Search over 1,750 names has to be forgiving: people remember "geiss waterfall", not
 * "Geiss - Waterfall". Every whitespace-separated term must appear somewhere in the name.
 */
export function searchPresets(presets, query) {
  const terms = (query || "").toLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) return presets;
  return presets.filter((p) => {
    const hay = p.n.toLowerCase();
    return terms.every((t) => hay.includes(t));
  });
}

// ── Fetching ────────────────────────────────────────────────────────────────
// The SPA is served with a history fallback, so a WRONG url does not 404 — it returns index.html
// with a 200. (Same trap as the prod art-upload run: "HTTP 200" is not proof.) Every response is
// therefore checked for the shape butterchurn actually needs before it is handed to loadPreset,
// where a bad object would surface much later as "the visualizer broke".
function assertPreset(preset, what) {
  if (!preset || typeof preset !== "object" || !preset.baseVals || typeof preset.pixel_eqs_str !== "string") {
    throw new Error(`${what} did not return a butterchurn preset (is /butterchurn published?)`);
  }
  return preset;
}

async function getJson(url, what) {
  const res = await fetch(url, { credentials: "same-origin" });
  if (!res.ok) throw new Error(`${what} failed: HTTP ${res.status}`);
  const text = await res.text();
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`${what} did not return JSON (is /butterchurn published?)`);
  }
}

let indexPromise = null;

/** The preset catalogue. Fetched once per page load; the result is shared by every visualizer mount. */
export function loadPresetIndex() {
  if (!indexPromise) {
    indexPromise = getJson(`${BASE}/index.json`, "preset index")
      .then((data) => {
        const presets = Array.isArray(data?.presets) ? data.presets : null;
        if (!presets || presets.length === 0) throw new Error("preset index is empty");
        return presets;
      })
      .catch((err) => {
        indexPromise = null; // a transient failure must not poison every later open
        throw err;
      });
  }
  return indexPromise;
}

// Presets are tiny but there are a lot of them; a session that shuffles for an hour would otherwise
// hold every one it ever saw. Insertion-ordered Map = cheap LRU-ish eviction of the oldest.
const CACHE_MAX = 250;
const cache = new Map();
const inflight = new Map();

/** One preset, by slug. Cached, and concurrent requests for the same slug share a fetch. */
export function fetchPreset(slug) {
  if (cache.has(slug)) return Promise.resolve(cache.get(slug));
  if (inflight.has(slug)) return inflight.get(slug);

  const promise = getJson(`${BASE}/presets/${encodeURIComponent(slug)}.json`, `preset "${slug}"`)
    .then((preset) => {
      assertPreset(preset, `preset "${slug}"`);
      cache.set(slug, preset);
      while (cache.size > CACHE_MAX) cache.delete(cache.keys().next().value);
      return preset;
    })
    .finally(() => inflight.delete(slug));

  inflight.set(slug, promise);
  return promise;
}

/** Warm the cache without caring about the result — used to make the next auto-advance instant. */
export function prefetchPreset(slug) {
  if (!slug || cache.has(slug) || inflight.has(slug)) return;
  fetchPreset(slug).catch(() => { /* a failed prefetch is just a cache miss later */ });
}

export function pickRandom(list, avoidSlug) {
  if (!list || list.length === 0) return null;
  if (list.length === 1) return list[0];
  for (let i = 0; i < 8; i += 1) {
    const candidate = list[Math.floor(Math.random() * list.length)];
    if (candidate.s !== avoidSlug) return candidate;
  }
  return list[0];
}

// Test seam: the module-level caches are deliberately process-wide, so tests need a way to reset.
export function __resetPresetCaches() {
  indexPromise = null;
  cache.clear();
  inflight.clear();
}
