// Safe localStorage access, shared. Storage can THROW (Safari private mode, storage disabled,
// quota) — a bare read at module scope was a white screen there. These never throw: a failed read
// returns the fallback, a failed write is dropped. Sections with genuinely custom persistence
// (the music queue's hydration-order invariant, coverAspect's coalesced flush, the arcade pad-map
// migrations) keep their own code on purpose — this is for the plain scalar read/write everyone
// else kept re-wrapping (or forgetting to wrap).

export function readStored(key, fallback = null) {
  try {
    const raw = window.localStorage.getItem(key);
    return raw == null ? fallback : raw;
  } catch {
    return fallback;
  }
}

export function writeStored(key, value) {
  try {
    if (value == null) window.localStorage.removeItem(key);
    else window.localStorage.setItem(key, String(value));
  } catch {
    /* storage blocked — the preference just doesn't persist this session */
  }
}

// The Watch player and the TV player SHARE the persisted quality rung — they used to agree on the
// literal "StreamQuality" by coincidence (a comment in TvPage acknowledged the coupling). The
// constant makes the coupling a fact of the code.
export const STREAM_QUALITY_KEY = "StreamQuality";
