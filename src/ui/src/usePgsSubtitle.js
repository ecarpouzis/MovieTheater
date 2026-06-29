import { useEffect } from "react";
import pgsWorkerUrl from "libpgs/dist/libpgs.worker.js?url";

// Client-side PGS (Blu-ray bitmap) subtitle rendering via libpgs — a canvas overlay on the <video>,
// shared by both players. The point: rendering PGS in the browser lets the server stream-COPY the video
// instead of re-encoding it to burn the bitmap in (which forced a full transcode whenever a PGS sub was
// turned on). `pgsUrl` is the external .sup delivery URL, or null when no PGS subtitle is active.
//
// libpgs is dynamically imported so its code + (self-contained, wasm-free) worker only load when a PGS
// sub is actually selected. The worker ships as a single self-contained file, so Vite's `?url` import
// bundles it as a hashed asset with nothing else to co-locate. Mirrors jellyfin-web's renderPgs.
//
// timeOffset stays 0: our delivered subtitle timestamps already line up with player time exactly as our
// WebVTT <track> subs do (the transcode preserves source PTS), so PGS needs no extra offset.
export function usePgsSubtitle(videoRef, pgsUrl) {
  useEffect(() => {
    const video = videoRef.current;
    if (!video || !pgsUrl) return undefined;
    let renderer = null;
    let cancelled = false;
    import("libpgs")
      .then(({ PgsRenderer }) => {
        if (cancelled || !videoRef.current) return;
        renderer = new PgsRenderer({
          video: videoRef.current,
          subUrl: pgsUrl,
          workerUrl: pgsWorkerUrl,
          aspectRatio: "contain", // matches the players' object-fit: contain video
        });
      })
      .catch(() => {
        /* libpgs failed to load — the PGS sub just won't show; playback is unaffected */
      });
    return () => {
      cancelled = true;
      try {
        renderer?.dispose();
      } catch {
        /* ignore */
      }
    };
  }, [videoRef, pgsUrl]);
}
