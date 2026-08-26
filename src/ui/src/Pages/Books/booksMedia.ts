/**
 * The media plane: bytes come straight from the Books host's public origin, never through the site
 * pods — `{baseUrl}/m/{token}/thumbs/{id}.webp`, pages, EPUB resources, downloads, folder icons. The
 * TOKEN is minted once per session (`GET /API/Books/media-token`, 12 h, host-minted) and cached here:
 * in memory and in `sessionStorage` keyed by the signed-in username, so a logout/login on the same
 * tab re-mints and a reload does not. Every builder is null-safe: an unconfigured media plane
 * (`configured:false`) yields `null` URLs and the cards fall back to their hue tiles.
 *
 * "Refresh on 403": an `<img>` error carries no status, so `reportMediaFailure(url)` re-mints only
 * when the failing URL's token is not the current one or the token has expired — a 404 is a missing
 * thumbnail, and the card's own retry/placeholder path owns that. Fetch-based media (the reader's
 * hi-res reads, text regions) see real status codes and call `refreshMediaToken()` on a 403.
 */
import { useSyncExternalStore } from "react";
import { BooksApiError, fetchMediaToken } from "./booksApi";

export interface MediaToken {
  token: string;
  baseUrl: string;
  expiresUtc: string;
  username: string;
}

const STORAGE_KEY = "books.mediaToken.v1";
/** Re-mint when this close to expiry. */
const REFRESH_AHEAD_MS = 15 * 60 * 1000;
/** After an unconfigured/failed mint, don't ask again for this long (a request storm otherwise). */
const UNAVAILABLE_TTL_MS = 60 * 1000;

let current: MediaToken | null = null;
let unavailableUntil = 0;
let inflight: Promise<MediaToken | null> | null = null;
let epoch = 0;
let username: string | null = null;
const listeners = new Set<() => void>();

function notify() {
  epoch += 1;
  for (const l of listeners) l();
}

function readSession(): MediaToken | null {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const t = JSON.parse(raw) as MediaToken;
    return t && t.token && t.baseUrl && t.expiresUtc ? t : null;
  } catch {
    return null;
  }
}

function writeSession(t: MediaToken | null) {
  try {
    if (t) window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(t));
    else window.sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    /* private mode — the in-memory copy still serves the session */
  }
}

function fresh(t: MediaToken | null, now = Date.now()): t is MediaToken {
  if (!t) return false;
  if (username != null && t.username !== username) return false;
  const exp = Date.parse(t.expiresUtc);
  return Number.isFinite(exp) && exp - now > REFRESH_AHEAD_MS;
}

/** Who is signed in — a change invalidates the cached token (the host mints per identity). */
export function setMediaUser(name: string | null | undefined): void {
  const next = name ?? null;
  if (next === username) return;
  username = next;
  if (current && current.username !== next) {
    current = null;
    writeSession(null);
    notify();
  }
}

export function currentMediaToken(): MediaToken | null {
  if (fresh(current)) return current;
  const stored = readSession();
  if (fresh(stored)) {
    current = stored;
    return current;
  }
  return null;
}

export async function getMediaToken(): Promise<MediaToken | null> {
  const have = currentMediaToken();
  if (have) return have;
  if (Date.now() < unavailableUntil) return null;
  if (inflight) return inflight;
  inflight = (async () => {
    try {
      const r = await fetchMediaToken();
      if (!r.configured || !r.token || !r.baseUrl || !r.expiresUtc) {
        unavailableUntil = Date.now() + UNAVAILABLE_TTL_MS;
        return null;
      }
      current = { token: r.token, baseUrl: r.baseUrl.replace(/\/+$/, ""), expiresUtc: r.expiresUtc, username: username ?? "" };
      writeSession(current);
      notify();
      return current;
    } catch (e) {
      // 503 = unconfigured on the host; anything else is transient — either way, back off.
      unavailableUntil = Date.now() + (e instanceof BooksApiError && e.status === 503 ? UNAVAILABLE_TTL_MS : 10_000);
      return null;
    } finally {
      inflight = null;
    }
  })();
  return inflight;
}

/** Drop the cached token and mint again (a real 403 from a fetch-based media read). */
export function refreshMediaToken(): Promise<MediaToken | null> {
  current = null;
  writeSession(null);
  unavailableUntil = 0;
  notify();
  return getMediaToken();
}

/** Only re-mints when the failing URL was built from a token that is no longer current. */
export function reportMediaFailure(url: string): void {
  const t = current ?? readSession();
  const stillCurrent = t && url.includes(`/m/${t.token}/`) && fresh(t);
  if (stillCurrent) return;
  void refreshMediaToken();
}

// ── URL builders (mirror BooksMediaRoutes on the host) ──

const base = () => currentMediaToken();

export function thumbUrl(id: number): string | null {
  const t = base();
  return t ? `${t.baseUrl}/m/${t.token}/thumbs/${id}.webp` : null;
}

export function pageUrl(id: number, page: number, maxWidth?: number): string | null {
  const t = base();
  if (!t) return null;
  return `${t.baseUrl}/m/${t.token}/pages/${id}/${page}${maxWidth ? `?maxWidth=${Math.round(maxWidth)}` : ""}`;
}

/** `pagesUrlTemplate` from `/items/{id}` contains `{page}`; it already carries the token the host minted for that response. */
export function fillPagesTemplate(template: string | null | undefined, page: number, maxWidth?: number): string | null {
  if (!template) return null;
  const url = template.replace("{page}", String(page));
  if (!maxWidth) return url;
  return `${url}${url.includes("?") ? "&" : "?"}maxWidth=${Math.round(maxWidth)}`;
}

export function epubResourceUrl(id: number, path: string): string | null {
  const t = base();
  if (!t) return null;
  const clean = path.replace(/^\/+/, "").split("/").map(encodeURIComponent).join("/");
  return `${t.baseUrl}/m/${t.token}/epub/${id}/${clean}`;
}

export function downloadUrl(id: number): string | null {
  const t = base();
  return t ? `${t.baseUrl}/m/${t.token}/download/${id}` : null;
}

export function folderIconUrl(id: number): string | null {
  const t = base();
  return t ? `${t.baseUrl}/m/${t.token}/folders/${id}/icon` : null;
}

// ── React seam ──

function subscribe(cb: () => void) {
  listeners.add(cb);
  return () => { listeners.delete(cb); };
}

/** Re-renders when the token changes; kicks a mint on first use. Returns the epoch so URLs rebuild. */
export function useMediaToken(): { token: MediaToken | null; epoch: number } {
  const e = useSyncExternalStore(subscribe, () => epoch, () => 0);
  const token = currentMediaToken();
  if (!token && Date.now() >= unavailableUntil && !inflight) void getMediaToken();
  return { token, epoch: e };
}

export const subscribeMedia = subscribe;

/** Test seam: a minted token, as if `/media-token` had answered. */
export function __setMediaForTests(t: MediaToken | null): void {
  current = t;
  unavailableUntil = 0;
  writeSession(t);
  notify();
}

/** Test seam. */
export function __resetMediaForTests(): void {
  current = null;
  unavailableUntil = 0;
  inflight = null;
  username = null;
  writeSession(null);
}
