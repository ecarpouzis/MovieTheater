/**
 * The in-player overlay: a ☰ beside the fullscreen ✕ that opens a panel OVER the running game. It
 * exists because in fullscreen only the player element paints — the room's button bar, the antd
 * modals and every body-portalled popup are invisible there, so a touch player had no way to reach
 * Save, Rewind, the controllers or the touch-pad editor without leaving fullscreen.
 *
 * Deliberately a plain in-player panel, not an antd Modal: a Modal portals to <body> by default and
 * would vanish in fullscreen exactly like the ones this replaces.
 */
import type { MouseEvent, PointerEvent, ReactNode } from "react";
import type { RoomAction } from "./touchLayout";
import type { TouchPref } from "./useTouchLayouts";
import "./touch.css";

export interface RoomOverlayProps {
  open: boolean;
  setOpen: (open: boolean) => void;
  /** Whether the ☰ (and the ✕ beside it) is currently faded in. */
  chromeVisible: boolean;
  immersive: boolean;
  spectator: boolean;
  touchPref: TouchPref;
  touchShown: boolean;
  onTouchPref: (p: TouchPref) => void;
  onEditTouch: () => void;
  allowed: Record<Exclude<RoomAction, "menu">, boolean>;
  onAction: (action: RoomAction, engaged: boolean) => void;
  onControllers: () => void;
  onFullscreen: () => void;
  onExitFullscreen: () => void;
  onLeave: () => void;
  /** Extra rows from the page (disc swap, …). */
  children?: ReactNode;
}

const PREF_LABEL: Record<TouchPref, string> = { auto: "Auto", on: "Always", off: "Off" };

export default function RoomOverlay(p: RoomOverlayProps) {
  const hold = (action: RoomAction) => ({
    onPointerDown: (e: PointerEvent) => { e.preventDefault(); p.onAction(action, true); },
    onPointerUp: () => p.onAction(action, false),
    onPointerLeave: () => p.onAction(action, false),
    onPointerCancel: () => p.onAction(action, false),
    onContextMenu: (e: MouseEvent) => e.preventDefault(),
  });
  const once = (action: RoomAction) => () => { p.setOpen(false); p.onAction(action, true); };

  return (
    <>
      <button
        type="button"
        className="room-overlay__menu-btn"
        data-touch-control=""
        aria-label="Game menu"
        title="Game menu"
        onClick={() => p.setOpen(!p.open)}
        style={{ opacity: p.chromeVisible || p.open ? 1 : 0, pointerEvents: p.chromeVisible || p.open ? "auto" : "none", right: p.immersive ? undefined : 12 }}
      >
        ☰
      </button>
      {!p.immersive && (
        <button type="button" className="room-overlay__fs-btn" data-touch-control="" aria-label="Play fullscreen" title="Play fullscreen" onClick={p.onFullscreen}>
          ⛶
        </button>
      )}
      {p.open && (
        <div className="room-overlay__scrim" data-touch-control="" onPointerDown={(e) => { if (e.target === e.currentTarget) p.setOpen(false); }}>
          <div className="room-overlay__panel" role="dialog" aria-label="Game menu">
            <div className="room-overlay__head">
              <b>Game menu</b>
              <button type="button" className="room-overlay__x" onClick={() => p.setOpen(false)} aria-label="Close menu">✕</button>
            </div>

            {!p.spectator && (
              <section>
                <h4>Touch controls</h4>
                <div className="room-overlay__seg" role="radiogroup" aria-label="Touch controls">
                  {(["auto", "on", "off"] as TouchPref[]).map((v) => (
                    <button key={v} type="button" role="radio" aria-checked={p.touchPref === v}
                      className={p.touchPref === v ? "is-on" : ""} onClick={() => p.onTouchPref(v)}>
                      {PREF_LABEL[v]}
                    </button>
                  ))}
                </div>
                <p className="room-overlay__note">
                  {p.touchPref === "auto"
                    ? (p.touchShown ? "Shown — they hide when you pick up a controller." : "Hidden — they show on a touch screen when no controller is in use.")
                    : p.touchPref === "on" ? "Always shown." : "Never shown."}
                </p>
                <button type="button" className="room-overlay__row" onClick={() => { p.setOpen(false); p.onEditTouch(); }}>
                  ✎ Edit layout — move, resize, hide, add buttons
                </button>
              </section>
            )}

            {!p.spectator && (p.allowed.quickSave || p.allowed.rewind || p.allowed.fastForward || p.allowed.reset) && (
              <section>
                <h4>Game</h4>
                <div className="room-overlay__grid">
                  {p.allowed.quickSave && <button type="button" onClick={once("quickSave")}>💾 Save</button>}
                  {p.allowed.quickLoad && <button type="button" onClick={once("quickLoad")}>📂 Load</button>}
                  {p.allowed.rewind && <button type="button" {...hold("rewind")}>⏪ Hold to rewind</button>}
                  {p.allowed.fastForward && <button type="button" {...hold("fastForward")}>⏩ Hold to fast-forward</button>}
                  {p.allowed.reset && <button type="button" onClick={once("reset")}>↺ Reset</button>}
                </div>
              </section>
            )}

            {p.children}

            <section>
              <h4>Room</h4>
              {!p.spectator && (
                <button type="button" className="room-overlay__row" onClick={() => { p.setOpen(false); p.onControllers(); }}>🎮 Controllers</button>
              )}
              {p.immersive
                ? <button type="button" className="room-overlay__row" onClick={() => { p.setOpen(false); p.onExitFullscreen(); }}>⤡ Exit fullscreen</button>
                : <button type="button" className="room-overlay__row" onClick={() => { p.setOpen(false); p.onFullscreen(); }}>⛶ Fullscreen</button>}
              <button type="button" className="room-overlay__row room-overlay__row--danger" onClick={p.onLeave}>
                {p.spectator ? "Stop watching" : "End — leave the room"}
              </button>
            </section>
          </div>
        </div>
      )}
    </>
  );
}
