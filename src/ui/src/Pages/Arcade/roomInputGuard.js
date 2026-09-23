// The room page's defence against a controller driving the BROWSER instead of the game.
//
// On Android a Bluetooth pad's buttons reach the page twice: through the Gamepad API the session polls,
// and again as ordinary key events, because the OS gives every pad button a keyboard fallback — A becomes
// Enter on whatever control has focus (the End button, say), B becomes BACK, the d-pad scrolls / moves
// focus, Start becomes Enter, Select becomes Menu. Chrome's Android shell eats those key events itself once
// a page polls gamepads; Firefox (GeckoView) and Edge's own Android shell do not, so on those browsers
// "press jump" left the room (reported 2026-09-22). Two layers, both installed for the life of the room page:
//
// 1. Key swallow: preventDefault on every plain key event while a pad is connected, or when the event
//    carries no physical key code at all (how a pad-derived event looks). Modifier combos, the function
//    row (the tape's F9/F10 and the browser's own F5/F11) and Escape (antd modals) pass, and so does
//    anything typed into a text field.
// 2. Back trap: a sentinel history entry that popstate keeps re-pushing while the page is up, for the
//    browser that resolves B to BACK before the page sees any key event. Leaving is the End button's job —
//    it must navigate with history.replace so the sentinel is consumed and the stack ends up as it was.

export function isEditableTarget(t) {
  if (!t || typeof t !== "object") return false;
  if (t.isContentEditable) return true;
  const tag = (t.tagName || "").toUpperCase();
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

const PAD_DERIVED_KEYS = new Set(["Unidentified", "GoBack", "BrowserBack", "GoHome", "ContextMenu"]);

// Pure decision so it can be tested without a window: swallow when the event is plausibly a pad's
// keyboard fallback and swallowing cannot cost a keyboard user anything they need.
export function shouldSwallowKey(e, { padConnected }) {
  if (!e) return false;
  if (isEditableTarget(e.target)) return false;
  if (e.ctrlKey || e.altKey || e.metaKey) return false;
  const key = e.key || "";
  if (key === "Escape") return false;
  if (/^F\d{1,2}$/.test(key)) return false;
  const code = e.code || "";
  const padShaped = PAD_DERIVED_KEYS.has(key) || code === "" || code === "Unidentified";
  return padConnected || padShaped;
}

export function anyPadConnected() {
  try {
    const pads = navigator.getGamepads ? navigator.getGamepads() : [];
    for (let i = 0; i < pads.length; i++) if (pads[i]) return true;
  } catch { /* getGamepads throws in some insecure/iframe contexts */ }
  return false;
}

// Capture phase on window so the decision is made before any component handler (antd's own key handling
// included) and so it cannot be short-circuited by a stopPropagation lower down.
export function installRoomKeySwallow({ padConnected = anyPadConnected } = {}) {
  const onKey = (e) => { if (shouldSwallowKey(e, { padConnected: padConnected() })) e.preventDefault(); };
  window.addEventListener("keydown", onKey, true);
  window.addEventListener("keyup", onKey, true);
  return () => {
    window.removeEventListener("keydown", onKey, true);
    window.removeEventListener("keyup", onKey, true);
  };
}

const TRAP_FLAG = "arcadeBackTrap";

// Push the sentinel on top of the current entry, keeping the router's own state object (react-router stores
// {key, state} in history.state — losing it would drop location.state.descriptor across a reload). A
// remount whose current entry is already the sentinel (back from the lobby) must not stack a second one.
export function installBackTrap({ onTrapped } = {}) {
  const h = window.history;
  const url = window.location.href;
  const carry = (base) => ({ ...(base && typeof base === "object" ? base : {}), [TRAP_FLAG]: true });
  if (!h.state?.[TRAP_FLAG]) {
    try { h.pushState(carry(h.state), "", url); } catch { return () => {}; }
  }
  const onPop = () => {
    // Landed on the entry beneath the sentinel: put it back and stay.
    if (h.state?.[TRAP_FLAG]) return;
    try { h.pushState(carry(h.state), "", url); } catch { return; }
    onTrapped?.();
  };
  window.addEventListener("popstate", onPop);
  return () => window.removeEventListener("popstate", onPop);
}

export const BACK_TRAP_FLAG = TRAP_FLAG;
