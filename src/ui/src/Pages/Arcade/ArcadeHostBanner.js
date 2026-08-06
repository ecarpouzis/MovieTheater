import { useEffect, useRef, useState } from "react";
import { Alert } from "antd";
import { MovieAPI } from "../../MovieAPI";

// How often we ask. The reporter on Ziggy posts on every state CHANGE (its cycle is 30 s), so this
// is the only other term in "how long until a player sees it" — 20 s keeps the worst case around a
// minute while costing one in-memory read per poll (no DB, no gateway call).
const POLL_MS = 20000;

// How long the "it's fixed" note stays up after WE watched it recover, on top of the server's own
// recently-recovered window. Somebody staring at the lobby when it clears deserves to be told why
// the picture is about to get better, but this must not become permanent furniture.
const RECOVERED_MS = 60000;

/**
 * Warns that the arcade's host is not on its physical console — someone left a remote desktop open,
 * or closed one without the console coming back.
 *
 * WHY THIS EXISTS: the emulators render into a real interactive Windows session on the host. With an
 * RDP client attached, the physical displays are replaced by the RDP display and everything renders
 * at ~32 Hz; a session left DISCONNECTED sits with a stalled DWM at a similar rate. Nothing errors —
 * the stream simply gets worse — so players blamed their own connection. This says the quiet part.
 *
 * It also reports the RECOVERY: the host reattaches its session to the console (automatically when
 * the pool is idle, and at the next room start regardless), and when that lands the banner flips to
 * a short "restored" note instead of just vanishing, so anyone who saw the warning learns it is over.
 */
export default function ArcadeHostBanner() {
  const [status, setStatus] = useState(null);
  // Set when we personally observe degraded -> healthy. The server also reports `recentlyRecovered`
  // (for someone who opened the page just after it happened); either one shows the note.
  const [recoveredAt, setRecoveredAt] = useState(null);
  const wasDegraded = useRef(false);

  useEffect(() => {
    let alive = true;
    const load = () =>
      MovieAPI.getArcadeHostStatus().then((s) => {
        if (!alive || !s) return;
        if (wasDegraded.current && !s.degraded) setRecoveredAt(Date.now());
        if (s.degraded) setRecoveredAt(null);
        wasDegraded.current = !!s.degraded;
        setStatus(s);
      });
    load();
    const id = setInterval(load, POLL_MS);
    return () => {
      alive = false;
      clearInterval(id);
    };
  }, []);

  // Nothing to say until the host has actually told us something. A silent reporter shows NO banner
  // rather than a stale one — the server already suppresses a degraded state it has stopped hearing
  // about (ArcadeHostSession.Stale). A warning that latches on when the watchdog dies would train
  // everyone to ignore the banner, which costs more than the missed warning.
  if (!status || !status.reported) return null;

  if (!status.degraded) {
    const showRecovered =
      status.recentlyRecovered || (recoveredAt && Date.now() - recoveredAt < RECOVERED_MS);
    if (!showRecovered) return null;
    return (
      <Alert
        className="arcade-host-banner"
        type="success"
        showIcon
        closable
        message="Full performance restored"
        description="The arcade host is back on its own screen — video is running at full frame rate again."
      />
    );
  }

  const remote = status.kind === "remote";
  return (
    <Alert
      className="arcade-host-banner"
      type="warning"
      showIcon
      message={
        remote
          ? "Someone is remoted into the arcade PC — video will be choppy"
          : status.recovering
            ? "Restoring the arcade PC's display…"
            : "The arcade PC isn't on its own screen — video will be choppy"
      }
      description={
        remote
          ? "While a remote desktop session is open, the host renders to the remote screen at about half the normal frame rate. Games still play; they just look and feel worse. Closing that remote desktop session fixes it."
          : status.recovering
            ? "A remote desktop session was left open and has been closed. The host is switching back to its own display — this clears on its own within a minute."
            : "A remote desktop session was left open on the host, so it is still rendering at about half the normal frame rate. It restores itself once nobody is playing, or as soon as the next game starts."
      }
    />
  );
}
