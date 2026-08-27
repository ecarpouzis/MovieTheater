import { useCallback, useEffect, useState } from "react";

// Light/dark theme state, persisted in localStorage. Light is the default for a first-time
// visitor (DESIGN_SPEC §1 — "light won for card scannability"); the toggle remembers the choice.
// The value lives as data-theme on <html>, which theme.css keys its token overrides off of.
const THEME_KEY = "Theme";

function readStoredTheme() {
  try {
    const raw = window.localStorage.getItem(THEME_KEY);
    return raw === "dark" ? "dark" : "light";
  } catch {
    return "light";
  }
}

// Apply synchronously at module load — BEFORE React first paints — so there's no light/dark flash
// (mirrors App.js's module-scope storedCardStyle read).
const initialTheme = readStoredTheme();
if (typeof document !== "undefined") {
  document.documentElement.dataset.theme = initialTheme;
}

// Somewhere OTHER than the toggle can need the site in a particular theme — the catalog's backdrop
// swatch grid shows all nine of a section's backdrops, and picking one from the other light/dark
// family asks for that family here rather than painting a dark page inside a light site (a swatch
// that did nothing would be an inert control). It is a REQUEST, not a write: this hook stays the
// single writer of `data-theme` and of the stored value.
export const THEME_REQUEST_EVENT = "site:theme";

export function requestSiteTheme(theme) {
  if (typeof window === "undefined") return;
  window.dispatchEvent(new CustomEvent(THEME_REQUEST_EVENT, { detail: theme === "dark" ? "dark" : "light" }));
}

export function useTheme() {
  const [theme, setTheme] = useState(initialTheme);

  useEffect(() => {
    const onRequest = (e) => setTheme(e.detail === "dark" ? "dark" : "light");
    window.addEventListener(THEME_REQUEST_EVENT, onRequest);
    return () => window.removeEventListener(THEME_REQUEST_EVENT, onRequest);
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    try {
      window.localStorage.setItem(THEME_KEY, theme);
    } catch {
      /* ignore — a non-persistable choice just resets to default next session */
    }
  }, [theme]);

  const toggleTheme = useCallback(() => {
    setTheme((t) => (t === "dark" ? "light" : "dark"));
  }, []);

  return { theme, toggleTheme };
}
