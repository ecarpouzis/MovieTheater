import { useEffect, useRef } from "react";

// Shared OS Media Session wiring (lock-screen / media-key now-playing card), used by both the Watch and
// TV players. Sets the metadata, registers the transport action handlers the caller asks for, and — the
// part browsers stop deriving on their own once you set a custom handler — pushes an accurate
// `setPositionState` so the lock-screen scrubber tracks real playback instead of freezing.
//
// `actions` maps Media Session action names ("play","pause","seekto","seekbackward","seekforward",
// "previoustrack","nexttrack") to handlers; only the provided names are registered (Watch wires
// play/pause/seek; TV wires play/pause/prev/next, no seek). Handlers are read through a ref so they never
// go stale and we don't re-register every render. Position is driven by the <video> element's own events,
// so there's no polling timer.
export function useMediaSession({ videoRef, title, subtitle, poster, actions }) {
  const actionsRef = useRef(actions);
  actionsRef.current = actions;

  useEffect(() => {
    if (typeof navigator === "undefined" || !("mediaSession" in navigator)) return undefined;
    const ms = navigator.mediaSession;
    try {
      ms.metadata = new window.MediaMetadata({
        title: title || "MovieTheater",
        artist: subtitle || "",
        artwork: poster
          ? [{ src: poster, sizes: "512x512", type: poster.endsWith(".png") ? "image/png" : "image/jpeg" }]
          : [],
      });
    } catch {
      /* MediaMetadata unsupported */
    }

    // Thin wrappers so the live handler comes from the ref (never stale); unsupported actions throw → skip.
    const names = Object.keys(actionsRef.current || {});
    for (const name of names) {
      try {
        ms.setActionHandler(name, (details) => actionsRef.current?.[name]?.(details));
      } catch {
        /* action unsupported by this browser */
      }
    }

    const video = videoRef.current;
    const syncPosition = () => {
      if (!video) return;
      const duration = video.duration;
      if (!Number.isFinite(duration) || duration <= 0) return; // live/unknown — leave the scrubber alone
      try {
        ms.setPositionState({
          duration,
          position: Math.min(Math.max(video.currentTime || 0, 0), duration),
          playbackRate: video.playbackRate || 1,
        });
      } catch {
        /* setPositionState unsupported */
      }
    };
    const syncPlayback = () => {
      try {
        ms.playbackState = video && !video.paused ? "playing" : "paused";
      } catch {
        /* ignore */
      }
    };
    const posEvents = ["timeupdate", "durationchange", "ratechange", "seeked", "loadedmetadata"];
    const playEvents = ["play", "pause", "playing", "waiting"];
    if (video) {
      posEvents.forEach((e) => video.addEventListener(e, syncPosition));
      playEvents.forEach((e) => video.addEventListener(e, syncPlayback));
      syncPosition();
      syncPlayback();
    }

    return () => {
      for (const name of names) {
        try {
          ms.setActionHandler(name, null);
        } catch {
          /* ignore */
        }
      }
      if (video) {
        posEvents.forEach((e) => video.removeEventListener(e, syncPosition));
        playEvents.forEach((e) => video.removeEventListener(e, syncPlayback));
      }
    };
    // actions read via ref; re-run only when the displayed title/art change or the element appears.
  }, [title, subtitle, poster, videoRef]);
}
