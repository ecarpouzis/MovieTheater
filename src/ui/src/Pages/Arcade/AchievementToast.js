import { useEffect, useRef } from "react";
import { createPortal } from "react-dom";
import "./AchievementToast.css";

// Steam-style achievement pop: a card that slides in from the bottom-right corner with the achievement's
// badge art, sits for a few seconds, then slides out. Stacks when several fire close together. Rendered
// via a portal into the current fullscreen element (or body), so it shows OVER the game in fullscreen —
// only the fullscreen element + its descendants paint, which is why the plain top toast was invisible there.

const TOAST_MS = 6500;

function Toast({ toast, onExpire }) {
  // Keep the timer to ONE per toast regardless of parent re-renders (the room re-renders often) — a
  // deps:[onExpire] effect would reset the countdown each render and the pop would never dismiss.
  const expireRef = useRef(onExpire);
  expireRef.current = onExpire;
  useEffect(() => {
    const id = setTimeout(() => expireRef.current(), TOAST_MS);
    return () => clearTimeout(id);
  }, []);

  // Legitimacy is OBSERVED: clean until something dirties the run. The room's competitive mode is a
  // guardrail, not a qualifier — a casual room earns the trophy pop too.
  const legit = !toast.cheat && !toast.savescum && !toast.timeplay;
  const taints = [
    toast.cheat && { icon: "🔧", label: "Cheats were enabled" },
    toast.savescum && { icon: "💾", label: "A save state was loaded" },
    toast.timeplay && { icon: "⏩", label: "Fast-forward / rewind used" },
  ].filter(Boolean);

  return (
    <div className={"ach-toast" + (legit ? " ach-toast--legit" : "")}>
      <div className="ach-toast__badge">
        {toast.badgeUrl
          ? <img src={toast.badgeUrl} alt="" draggable={false} />
          : <span className="ach-toast__badge-fallback">{legit ? "🏆" : "🎖️"}</span>}
      </div>
      <div className="ach-toast__body">
        <div className="ach-toast__head">
          {legit ? "🏆 Achievement Unlocked" : "🎖️ Achievement Unlocked"}
        </div>
        <div className="ach-toast__title" title={toast.title}>{toast.title || "Unknown"}</div>
        <div className="ach-toast__meta">
          {toast.points ? <span className="ach-toast__pts">{toast.points} pts</span> : null}
          {taints.map((t) => <span key={t.icon} className="ach-toast__taint" title={t.label}>{t.icon}</span>)}
        </div>
      </div>
    </div>
  );
}

export default function AchievementToaster({ toasts, onExpire, container }) {
  const target = container || (typeof document !== "undefined" ? document.body : null);
  if (!target || !toasts || toasts.length === 0) return null;
  return createPortal(
    <div className="ach-toaster">
      {toasts.map((t) => <Toast key={t.key} toast={t} onExpire={() => onExpire(t.key)} />)}
    </div>,
    target
  );
}
