import { useEffect, useRef } from "react";
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
// `timelineOffsetSec` is the seconds this HLS session's media timeline runs ahead of true content time
// (streamEngine's timelineOffsetFromInitPts; 0 on direct play). libpgs renders at
// `video.currentTime + timeOffset`, and the .sup's timestamps are true content time, so the renderer's
// clock has to be pulled BACK by the offset — hence the negation. It arrives from INIT_PTS_FOUND after
// the renderer is already mounted and re-rolls on every seek/discontinuity, so it's pushed through the
// live setter rather than baked in at construction (the constructor value only covers a late import).
export function usePgsSubtitle(videoRef, pgsUrl, timelineOffsetSec = 0) {
  const rendererRef = useRef(null);
  const offsetRef = useRef(timelineOffsetSec);
  offsetRef.current = timelineOffsetSec;

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
          timeOffset: -offsetRef.current,
        });
        rendererRef.current = renderer;
      })
      .catch(() => {
        /* libpgs failed to load — the PGS sub just won't show; playback is unaffected */
      });
    return () => {
      cancelled = true;
      rendererRef.current = null;
      try {
        renderer?.dispose();
      } catch {
        /* ignore */
      }
    };
  }, [videoRef, pgsUrl]);

  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer) return; // still importing — the constructor above picks up offsetRef instead
    try {
      renderer.timeOffset = -timelineOffsetSec; // setter re-renders the current subtitle
    } catch {
      /* ignore — the sub just keeps its previous timing */
    }
  }, [timelineOffsetSec, pgsUrl]);
}
