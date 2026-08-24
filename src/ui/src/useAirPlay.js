import { useCallback, useEffect, useState } from "react";

// ── AirPlay ───────────────────────────────────────────────────────────────────
//
// The other half of "play this on the television", and the half that covers every iPhone and iPad:
// Apple does not permit the Google Cast SDK on iOS at all, in any browser, so the cast button in
// these players can never appear there. AirPlay is what those viewers have.
//
// It is a much smaller thing to build than the cast sender, because the negotiation problem doesn't
// exist. AirPlay hands the RECEIVER the same element the browser is already playing — there is no
// second device profile to guess, no separate session to start, no bitrate ceiling to pick, and no
// CORS boundary to widen. The stream that works here is the stream that works there.
//
// Safari's API for this predates the standard Remote Playback API and is what actually ships, so it
// is what's used: `webkitShowPlaybackTargetPicker` to open the picker,
// `webkitplaybacktargetavailabilitychanged` for whether any receiver exists, and
// `webkitcurrentplaybacktargetiswirelesschanged` for whether one is currently in use.
//
// `contentIsRemotePlayable` is the caller's promise that what the element is playing CAN travel —
// see canRemotePlay in streamEngine. Without that gate the button appears on desktop Safari, where
// receivers are discoverable but MediaSource content cannot be sent, and picking one blacks out the
// television.

/**
 * @param videoRef the media element
 * @param contentIsRemotePlayable whether the current source can go to a remote target at all
 * @returns { supported, available, active, show }
 *   `supported` — the browser has the API and this source qualifies.
 *   `available` — a receiver has actually been seen on the network (drives whether to render at all).
 *   `active`    — playback is currently going to a receiver.
 */
export function useAirPlay(videoRef, contentIsRemotePlayable = true) {
  const [hasApi] = useState(() => {
    if (typeof window === "undefined") return false;
    // Both halves are needed: the availability EVENT constructor tells us the picker is meaningful,
    // and the method is what opens it. Safari has had both since 9; nothing else has either.
    try {
      return (
        typeof window.WebKitPlaybackTargetAvailabilityEvent !== "undefined" &&
        typeof document.createElement("video").webkitShowPlaybackTargetPicker === "function"
      );
    } catch {
      return false;
    }
  });
  const [available, setAvailable] = useState(false);
  const [active, setActive] = useState(false);

  useEffect(() => {
    const video = videoRef.current;
    if (!hasApi || !video) return undefined;

    // Fires once shortly after listening (with the current answer) and again whenever a receiver
    // appears or goes away, so there's nothing to poll and no initial state to seed.
    const onAvailability = (event) => setAvailable(event.availability === "available");
    const onWirelessChange = () => setActive(!!video.webkitCurrentPlaybackTargetIsWireless);

    video.addEventListener("webkitplaybacktargetavailabilitychanged", onAvailability);
    video.addEventListener("webkitcurrentplaybacktargetiswirelesschanged", onWirelessChange);
    onWirelessChange(); // a reload while already AirPlaying keeps the target — reflect it immediately
    return () => {
      video.removeEventListener("webkitplaybacktargetavailabilitychanged", onAvailability);
      video.removeEventListener("webkitcurrentplaybacktargetiswirelesschanged", onWirelessChange);
    };
  }, [videoRef, hasApi]);

  // Opening the picker is all a sender can do: the choice of receiver, and cancelling, belong to the
  // system sheet. There is no programmatic disconnect, which is why this hook has no stop().
  const show = useCallback(() => {
    try {
      videoRef.current?.webkitShowPlaybackTargetPicker?.();
    } catch {
      /* no gesture, or the picker is already up */
    }
  }, [videoRef]);

  return { supported: hasApi && contentIsRemotePlayable, available, active, show };
}
