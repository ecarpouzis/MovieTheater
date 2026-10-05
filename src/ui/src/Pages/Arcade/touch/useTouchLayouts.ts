/**
 * The player's touch layouts, saved to their ACCOUNT (UserSettings "ArcadeTouchLayouts", one JSON blob)
 * so a layout made on the phone is there on the tablet too. A localStorage mirror lets the room draw
 * the right layout instantly. When the server answers, its copy wins — UNLESS this device holds an edit
 * whose save failed (the unsynced flag), which is pushed up instead of being overwritten. Writes happen
 * on the editor's Save only — never per drag.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { MovieAPI } from "../../../MovieAPI";
import { fitStore, isEmptyStore, parseStore, serializeStore, withLayout, withPick, type Layout, type LayoutStore } from "./touchLayout";

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
      MovieAPI.setArcadeTouchLayouts(isEmptyStore(storeRef.current) ? null : json)
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

  // Every write goes through here: local first (the room redraws at once), then the account.
  const commit = useCallback(async (wanted: LayoutStore): Promise<boolean> => {
    const { store: next, json } = fitStore(wanted);
    storeRef.current = next;
    setStore(next);
    writeMirror(json);
    writePending(true);
    try {
      const res = await MovieAPI.setArcadeTouchLayouts(isEmptyStore(next) ? null : json);
      const ok = !!res && res.ok;
      if (ok && storeRef.current === next) writePending(false); // a newer save owns the flag otherwise
      return ok;
    } catch { return false; }
  }, []);

  /**
   * Set (or with null, delete) one layout key, plus any pick changes in the SAME write (saving an edit
   * marks its scope "custom", so a preset picked earlier doesn't hide it). Resolves true when the account
   * copy saved.
   */
  const save = useCallback((key: string, layout: Layout | null, picks: Record<string, string | null> = {}) => {
    let next = withLayout(storeRef.current, key, layout);
    for (const [k, v] of Object.entries(picks)) next = withPick(next, k, v);
    return commit(next);
  }, [commit]);

  /**
   * Pick a preset (or "custom") for a pick key — `sys:<inputSystem>` / `game:<system>/<gameKey>` — or with
   * null, clear the pick. `alsoClear` drops other pick keys in the same write (choosing for the whole
   * system clears this game's own pick, or the game's pick would keep winning).
   */
  const savePick = useCallback((key: string, presetId: string | null, alsoClear: string[] = []) => {
    let next = withPick(storeRef.current, key, presetId);
    for (const k of alsoClear) next = withPick(next, k, null);
    return commit(next);
  }, [commit]);

  return { store, save, savePick };
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
