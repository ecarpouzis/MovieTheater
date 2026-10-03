/**
 * The player's touch layouts, saved to their ACCOUNT (UserSettings "ArcadeTouchLayouts", one JSON blob)
 * so a layout made on the phone is there on the tablet too. A localStorage mirror lets the room draw
 * the right layout instantly. When the server answers, its copy wins — UNLESS this device holds an edit
 * whose save failed (the unsynced flag), which is pushed up instead of being overwritten. Writes happen
 * on the editor's Save only — never per drag.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { MovieAPI } from "../../../MovieAPI";
import { parseStore, serializeStore, withLayout, type Layout, type LayoutStore } from "./touchLayout";

const MIRROR_KEY = "arcade.touchLayouts";

function readMirror(): LayoutStore {
  try { return parseStore(localStorage.getItem(MIRROR_KEY)); } catch { return parseStore(null); }
}
function writeMirror(json: string) {
  try { localStorage.setItem(MIRROR_KEY, json); } catch { /* private mode: the account copy still saves */ }
}
// Set while the mirror holds an edit the account hasn't confirmed.
const PENDING_KEY = "arcade.touchLayouts.unsynced";
function readPending(): boolean {
  try { return localStorage.getItem(PENDING_KEY) === "1"; } catch { return false; }
}
function writePending(on: boolean) {
  try { if (on) localStorage.setItem(PENDING_KEY, "1"); else localStorage.removeItem(PENDING_KEY); } catch { /* */ }
}

export default function useTouchLayouts() {
  const [store, setStore] = useState<LayoutStore>(readMirror);
  const storeRef = useRef(store);
  storeRef.current = store;

  useEffect(() => {
    let cancelled = false;
    // An edit whose account save FAILED is still only on this device: push it up rather than let the
    // account's older copy overwrite it.
    if (readPending()) {
      const json = serializeStore(storeRef.current);
      MovieAPI.setArcadeTouchLayouts(Object.keys(storeRef.current.layouts).length ? json : null)
        .then((res) => { if (res && res.ok) writePending(false); })
        .catch(() => { /* still offline: try again next room */ });
      return () => { cancelled = true; };
    }
    MovieAPI.getArcadeTouchLayouts()
      .then((json: string | null) => {
        if (cancelled || readPending()) return; // an edit landed while this was in flight
        const s = parseStore(json);
        setStore(s);
        writeMirror(serializeStore(s));
      })
      .catch(() => { /* offline / not logged in: keep the mirror */ });
    return () => { cancelled = true; };
  }, []);

  /** Set (or with null, delete) one layout key. Resolves true when the account copy saved. */
  const save = useCallback(async (key: string, layout: Layout | null): Promise<boolean> => {
    const next = withLayout(storeRef.current, key, layout);
    storeRef.current = next;
    setStore(next);
    const json = serializeStore(next);
    writeMirror(json);
    writePending(true);
    try {
      const res = await MovieAPI.setArcadeTouchLayouts(Object.keys(next.layouts).length ? json : null);
      const ok = !!res && res.ok;
      if (ok && storeRef.current === next) writePending(false); // a newer save owns the flag otherwise
      return ok;
    } catch { return false; }
  }, []);

  return { store, save };
}

// ── Show/hide preference (per DEVICE: whether this phone wants the pad is about this phone) ──────────

export type TouchPref = "auto" | "on" | "off";
const PREF_KEY = "arcade.touchControls";

export function readTouchPref(): TouchPref {
  try {
    const v = localStorage.getItem(PREF_KEY);
    return v === "on" || v === "off" ? v : "auto";
  } catch { return "auto"; }
}
export function writeTouchPref(v: TouchPref) {
  try { if (v === "auto") localStorage.removeItem(PREF_KEY); else localStorage.setItem(PREF_KEY, v); } catch { /* */ }
}
