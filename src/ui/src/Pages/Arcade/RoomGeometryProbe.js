import { useEffect, useState } from "react";
import { readStored, writeStored } from "../../utils/storage";

/**
 * A field instrument for "the picture is the wrong size on MY phone" — hidden unless switched on
 * (below). Headless Chrome lays rotated (vertical-cab) games out correctly while a real Android
 * phone showed them ~2x magnified (Donkey Kong, 1942 — 2026-10-05), so the numbers have to come from
 * the device itself: the stream's frame size, the <video>'s inline + computed size and transform, the
 * box it sits in, and the browser. Read-only; it touches nothing it measures. One interval while
 * shown, none otherwise.
 *
 * Switched on PER DEVICE from the LOBBY (`/arcade?geo=1`, off with `?geo=0`), because reloading a room
 * URL to add the param drops the player out of the room. `?geo=1` on the room itself still works.
 */
export const GEO_PROBE_KEY = "arcade.geoProbe";

/** Apply a `geo=1` / `geo=0` from a URL to this device's flag (the lobby calls this). */
export function applyGeoProbeParam(search) {
  const v = new URLSearchParams(search).get("geo");
  if (v === "1") writeStored(GEO_PROBE_KEY, "1");
  else if (v === "0") writeStored(GEO_PROBE_KEY, null);
}

/** Should a room show the readout? The device flag, or `?geo=1` on the room URL. */
export function geoProbeOn(search) {
  return new URLSearchParams(search).get("geo") === "1" || readStored(GEO_PROBE_KEY) === "1";
}
function read(video) {
  if (!video) return null;
  const vr = video.getBoundingClientRect();
  const box = video.parentElement;
  const br = box ? box.getBoundingClientRect() : null;
  const cs = getComputedStyle(video);
  return {
    frame: `${video.videoWidth}x${video.videoHeight}`,
    videoRect: `${Math.round(vr.width)}x${Math.round(vr.height)} @${Math.round(vr.left)},${Math.round(vr.top)}`,
    boxRect: br ? `${Math.round(br.width)}x${Math.round(br.height)}` : "-",
    inlineSize: `${video.style.width} / ${video.style.height}`,
    computedSize: `${cs.width} / ${cs.height}`,
    transform: cs.transform,
    objectFit: cs.objectFit,
    dpr: window.devicePixelRatio,
    viewport: `${window.innerWidth}x${window.innerHeight}`,
    ua: navigator.userAgent.replace(/^Mozilla\/5\.0 /, "").slice(0, 120),
  };
}

export default function RoomGeometryProbe({ videoRef, coreAspect, coreRot }) {
  const [snap, setSnap] = useState(null);
  useEffect(() => {
    const tick = () => setSnap(read(videoRef.current));
    tick();
    const t = setInterval(tick, 1000);
    return () => clearInterval(t);
  }, [videoRef]);
  if (!snap) return null;
  return (
    <pre
      aria-label="Room geometry"
      style={{ position: "fixed", left: 8, right: 8, bottom: 8, zIndex: 1450, margin: 0, padding: "8px 10px", borderRadius: 8,
        background: "rgba(0,0,0,0.82)", color: "#9ef0a0", font: "11px/1.35 ui-monospace, monospace", whiteSpace: "pre-wrap", pointerEvents: "none" }}
    >
      {`core ar=${coreAspect ?? "-"} rot=${coreRot ?? 0}\n` + Object.entries(snap).map(([k, v]) => `${k}: ${v}`).join("\n")}
    </pre>
  );
}
