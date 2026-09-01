import { Alert } from "antd";
import useMediaReachable from "../hooks/useMediaReachable";

/**
 * Tells a visitor what their network means for playback, instead of letting them discover it as
 * silence — two verdicts, two very different voices:
 *
 * "unreachable" (warning, NOT closable): the media host answers on no route at all — every fetch
 * dies at the network layer with no status code, so the player can only sit at 0:00 flipping
 * between play and pause (exactly how 2026-09-01 was reported). Since the VPS door went in
 * (docs/site-ipv4-door.md) plain v4-only networks work fine, so landing here now means something
 * genuinely restrictive — a firewalled corporate/public network, or the media host actually down.
 * Not closable because it is not an announcement; it is the reason the thing they came to do will
 * not work. It clears itself the moment a probe succeeds (a new tab re-probes).
 *
 * "ok-v4" (info, closable): everything works, but over IPv4 via the relay — a small detour for
 * them, and relay bandwidth for us, which a watch party multiplies. The fix really is usually one
 * router setting (IPv6 is widely supported and just switched off), so the banner says that.
 * Closable because it is advice, not a blocker — and the App-level mount means a close lasts the
 * whole SPA session, not one page.
 */
export default function MediaReachBanner() {
  const reachable = useMediaReachable();

  if (reachable === "unreachable") {
    return (
      <Alert
        className="media-reach-banner"
        type="warning"
        showIcon
        message="This network can't reach the media server — nothing will play"
        description={
          "Browsing works, but music, movies, photos and books come from a separate media server "
          + "this network can't reach at all. That usually means a very restrictive network (some "
          + "corporate and public wifi). On a phone, trying again on cellular is the quickest way "
          + "to confirm it's the network."
        }
      />
    );
  }

  if (reachable === "ok-v4") {
    return (
      <Alert
        className="media-reach-banner"
        type="info"
        showIcon
        closable
        message="Tip: turning on IPv6 would give you a faster connection"
        description={
          "You're reaching the media server through our backup relay — everything works, but it "
          + "takes a small detour. Most home routers support IPv6 and just ship with it switched "
          + "off; turning it on connects you directly, which is a bit faster for you and keeps the "
          + "relay free for watch parties."
        }
      />
    );
  }

  return null;
}
