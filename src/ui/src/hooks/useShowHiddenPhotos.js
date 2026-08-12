import { useEffect, useState } from "react";

// The admin "show hidden photos" switch (docs/photos-plan.md §2.9, Phase 4 addendum).
//
// Phase 2 put this toggle in the photos page's own toolbar and let any family member flip it. The
// owner's decision supersedes that: ANY member may hide or unhide a photo — that is ordinary curation
// — but the hidden pile is revealed only to a site admin, and the switch that reveals it lives in the
// navbar rather than in the page, because it is a property of the session rather than of the view.
//
// Two components need the same answer (the navbar draws the checkbox, the photos page re-queries when
// it changes) and they are not related by the React tree, so the value lives in localStorage — the way
// the site already persists theme, sort and the type scope — with a custom event so a change is picked
// up in the same tab. `storage` alone would not do: browsers fire it only in OTHER tabs.
//
// None of this is a gate. The server ignores includeHidden from a non-admin regardless of what is
// stored here, so a hand-edited localStorage value buys exactly nothing.

const KEY = "PhotosShowHidden";
const EVENT = "photos-show-hidden-changed";

export function loadShowHiddenPhotos() {
  try {
    return window.localStorage.getItem(KEY) === "1";
  } catch {
    // Private mode / storage disabled. Defaulting to false is the safe direction: it shows the
    // curated album, which is what everyone but an admin looking for junk wants anyway.
    return false;
  }
}

export function saveShowHiddenPhotos(on) {
  try {
    if (on) window.localStorage.setItem(KEY, "1");
    else window.localStorage.removeItem(KEY);
  } catch {
    /* private mode — the switch still works for this page's lifetime */
  }
  window.dispatchEvent(new CustomEvent(EVENT, { detail: !!on }));
}

/** Subscribes to the switch. Returns [showHidden, setShowHidden]. */
export default function useShowHiddenPhotos() {
  const [showHidden, setShowHidden] = useState(loadShowHiddenPhotos);

  useEffect(() => {
    const onChanged = (event) => setShowHidden(!!event.detail);
    const onStorage = (event) => {
      if (event.key === KEY) setShowHidden(loadShowHiddenPhotos());
    };
    window.addEventListener(EVENT, onChanged);
    window.addEventListener("storage", onStorage);
    return () => {
      window.removeEventListener(EVENT, onChanged);
      window.removeEventListener("storage", onStorage);
    };
  }, []);

  return [showHidden, saveShowHiddenPhotos];
}
