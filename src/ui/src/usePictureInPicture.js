import { useCallback, useEffect, useState } from "react";

// Picture-in-Picture, shared by both players. Returns { supported, active, toggle }. Feature-detects the
// standard API plus Safari's webkit presentation-mode fallback, and tracks active state from the element's
// enter/leave events so the button can reflect it.
export function usePictureInPicture(videoRef) {
  const [supported] = useState(() => {
    if (typeof document === "undefined") return false;
    if (document.pictureInPictureEnabled) return true;
    // Safari (incl. iPad) exposes presentation mode instead of the standard API.
    try {
      return typeof document.createElement("video").webkitSetPresentationMode === "function";
    } catch {
      return false;
    }
  });
  const [active, setActive] = useState(false);

  useEffect(() => {
    const v = videoRef.current;
    if (!v) return undefined;
    const onEnter = () => setActive(true);
    const onLeave = () => setActive(false);
    v.addEventListener("enterpictureinpicture", onEnter);
    v.addEventListener("leavepictureinpicture", onLeave);
    return () => {
      v.removeEventListener("enterpictureinpicture", onEnter);
      v.removeEventListener("leavepictureinpicture", onLeave);
    };
  }, [videoRef]);

  const toggle = useCallback(async () => {
    const v = videoRef.current;
    if (!v) return;
    try {
      if (document.pictureInPictureElement) {
        await document.exitPictureInPicture();
      } else if (v.requestPictureInPicture) {
        await v.requestPictureInPicture();
      } else if (typeof v.webkitSetPresentationMode === "function") {
        v.webkitSetPresentationMode(
          v.webkitPresentationMode === "picture-in-picture" ? "inline" : "picture-in-picture"
        );
      }
    } catch {
      /* blocked (no user gesture / disabled) — leave state as-is */
    }
  }, [videoRef]);

  return { supported, active, toggle };
}
