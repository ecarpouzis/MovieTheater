import { lazy } from "react";

// A deploy replaces every hashed file under /assets. A tab that loaded index.html before the deploy
// still names the OLD chunks, so the first route it lazy-loads afterwards 404s — and because the SPA
// host answers any unknown path with index.html, the browser reports it as "expected a module, got
// text/html" rather than a plain 404. No client-side retry can bring that chunk back: it is gone from
// the server. The only cure is to go fetch the current index.html, which is exactly what a reload does.
//
// Guarded by a timestamp so a genuinely broken build can't put the page in a reload loop — a second
// failure within the window lets the error surface instead.
const RELOAD_KEY = "chunkReloadAt";
const RELOAD_WINDOW_MS = 15000;

function reloadForNewBuild() {
  const last = Number(window.sessionStorage.getItem(RELOAD_KEY) || 0);
  if (last && Date.now() - last < RELOAD_WINDOW_MS) return false;
  window.sessionStorage.setItem(RELOAD_KEY, String(Date.now()));
  window.location.reload();
  return true;
}

// Vite's preload helper fires this (and rejects the import) when a chunk's JS or CSS can't be fetched.
window.addEventListener("vite:preloadError", (event) => {
  if (reloadForNewBuild()) event.preventDefault();
});

export function lazyWithReload(importer) {
  return lazy(() =>
    importer().catch((err) => {
      // Never resolve if we're reloading — the page is on its way out, and resolving would flash a
      // half-rendered route first.
      if (reloadForNewBuild()) return new Promise(() => {});
      throw err;
    })
  );
}
