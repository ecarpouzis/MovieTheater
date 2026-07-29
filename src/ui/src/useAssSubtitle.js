import { useEffect, useRef } from "react";

// Client-side ASS/SSA rendering via @jellyfin/libass-wasm (SubtitlesOctopus), shared by both players.
// Renders the full ASS — positioning, signs, karaoke, colors — over the video, vs our default of letting
// Jellyfin flatten ASS to plain WebVTT (all typesetting lost). `assUrl` is the raw .ass delivery URL, or
// null when no ASS sub is active. libass is dynamically imported so its code + 2.3 MB wasm only load when
// an ASS sub is actually selected.
//
// The worker + wasm + legacy (asm.js) worker + fallback font are served from /libass/, copied there from
// node_modules by scripts/copy-libass.mjs (prestart/prebuild) — the worker fetches its wasm sibling by a
// relative path, so they must be co-located, hence the static copy rather than a bundled import.
//
// `timelineOffsetSec` is the seconds this HLS session's media timeline runs ahead of true content time
// (streamEngine's timelineOffsetFromInitPts; 0 on direct play). SubtitlesOctopus renders at
// `video.currentTime + timeOffset` and the .ass is authored in true content time, so its clock has to be
// pulled BACK by the offset — hence the negation. It arrives from INIT_PTS_FOUND after the renderer is
// mounted and re-rolls on seeks/discontinuities, so it's pushed through the live setter (which re-seeks
// the renderer) rather than baked in at construction. Embedded fonts (ASS \fn references) are a
// follow-up — without them, signs fall back to the bundled font; positioning/timing/karaoke are correct.
const WORKER_URL = "/libass/subtitles-octopus-worker.js";
const LEGACY_WORKER_URL = "/libass/subtitles-octopus-worker-legacy.js";
const FALLBACK_FONT = "/libass/default.woff2";

export function useAssSubtitle(videoRef, assUrl, timelineOffsetSec = 0) {
  const instanceRef = useRef(null);
  const offsetRef = useRef(timelineOffsetSec);
  offsetRef.current = timelineOffsetSec;

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !assUrl) return undefined;
    let instance = null;
    let cancelled = false;
    import("@jellyfin/libass-wasm")
      .then(({ default: SubtitlesOctopus }) => {
        if (cancelled || !videoRef.current) return;
        instance = new SubtitlesOctopus({
          video: videoRef.current,
          subUrl: assUrl,
          workerUrl: WORKER_URL,
          legacyWorkerUrl: LEGACY_WORKER_URL,
          fallbackFont: FALLBACK_FONT,
          timeOffset: -offsetRef.current,
        });
        instanceRef.current = instance;
      })
      .catch(() => {
        /* libass failed to load — the ASS sub won't render; playback is unaffected */
      });
    return () => {
      cancelled = true;
      instanceRef.current = null;
      try {
        instance?.dispose();
      } catch {
        /* ignore */
      }
    };
  }, [videoRef, assUrl]);

  useEffect(() => {
    const instance = instanceRef.current;
    if (!instance) return; // still importing — the constructor above picks up offsetRef instead
    try {
      instance.timeOffset = -timelineOffsetSec;
    } catch {
      /* ignore — the sub just keeps its previous timing */
    }
  }, [timelineOffsetSec, assUrl]);
}
