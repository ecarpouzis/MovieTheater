import { useEffect, useRef } from "react";

/**
 * Put the page back where it was, IF something took it to the top while a modal was open.
 *
 * A modal locks scrolling: antd injects `html body { overflow-y: hidden }` (@rc-component/portal's
 * useScrollLocker). On desktop that rule lands on nothing — the scroller there is `.app-content`. On
 * a phone (index.css ≤768px) `.app-content` hands its scrolling back to the window, so the rule
 * lands on the element that is actually scrolled, which is the shape of failure that gets reported
 * as "closing the photo puts me back at the top of the gallery".
 *
 * MEASURED 2026-08-14, and it did NOT reproduce: the lock preserved a 4,000px offset in both
 * Chromium and Firefox at 390×844, and so did a full open-and-close of the real grid inside a real
 * antd modal. The top-jump that WAS reproducible had a different cause entirely — the browse list
 * being remounted after a curation write (see PhotosPage's `curated`), which is fixed at its source.
 *
 * So this is a net, not a diagnosis, and it is deliberately shaped like one: it restores only when
 * the offset was CLOBBERED to zero, on the same page, with the content still tall enough to hold it.
 * A browser that kept the position, and a reader who scrolled to the top themselves, are untouched.
 * If the report survives everything else, this is the piece that will have caught it — and if it
 * never fires, it costs a ref and a rAF per opened photograph.
 */
export default function useScrollLockRestore(locked) {
  const savedRef = useRef(0);

  useEffect(() => {
    if (!locked) return undefined;
    const scroller = document.scrollingElement || document.documentElement;
    if (!scroller) return undefined;

    savedRef.current = scroller.scrollTop;
    // WHERE that offset belongs. Closing the modal is not the only way this effect ends: following a
    // link with a photo open unmounts the page too, and restoring 4,000px onto whatever came next
    // would be a far stranger bug than the one being guarded against.
    const from = window.location.pathname;

    return () => {
      const saved = savedRef.current;
      if (saved <= 2 || window.location.pathname !== from) return;
      // After the unlock: antd removes its <style> in a layout effect of its own, and the scrollport
      // is not scrollable until it is gone. One frame later it is.
      requestAnimationFrame(() => {
        const target = document.scrollingElement || document.documentElement;
        if (target && target.scrollTop <= 2 && target.scrollHeight > saved) target.scrollTop = saved;
      });
    };
  }, [locked]);
}
