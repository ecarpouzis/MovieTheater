import { useEffect, useMemo, useRef, useState } from "react";
import { MovieAPI } from "../MovieAPI";
import { parseLrc, activeLineIndex } from "./lrc";
// The pane's own stylesheet. It must NOT come from whichever page happens to render it — the play
// bar shows this on every route, while the Now Playing route is lazy-loaded. See the file header.
import "./MusicLyricsPane.css";

// After a manual scroll, leave the reader where they put themselves for this long instead of
// yanking the pane back on the next line.
const MANUAL_SCROLL_GRACE_MS = 6000;

// The lyrics view, shared by the Now Playing page and the play-bar overlay (music-plan.md §2.7).
//
// It lives here rather than inside the page because there are now two surfaces showing lyrics, and
// a second copy would drift on the parts that are easy to get subtly wrong — which line counts as
// active, and scrolling the PANE rather than the page.
//
// `position` is a prop, not an internal subscription: both callers already track the playhead for
// their own seek bar/time readout, and the context deliberately doesn't carry it (a ~4 Hz tick in
// context would re-render the whole app).
export default function MusicLyricsPane({ trackId, position, variant = "pane" }) {
  const [state, setState] = useState({ status: "loading" });
  const containerRef = useRef(null);
  const activeRef = useRef(null);
  const nudgedAtRef = useRef(0);

  useEffect(() => {
    if (trackId == null) {
      setState({ status: "empty" });
      return undefined;
    }
    let alive = true;
    setState({ status: "loading" });
    MovieAPI.getMusicTrackLyrics(trackId)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((data) => alive && setState({ status: "ready", ...data }))
      .catch(() => alive && setState({ status: "empty" }));
    return () => { alive = false; };
  }, [trackId]);

  const lines = useMemo(() => parseLrc(state.syncedLrc), [state.syncedLrc]);
  const active = activeLineIndex(lines, position);

  // Note when the reader scrolls by hand so the follow below backs off for a few seconds. Keyed on
  // the rendered shape because the scroll container element is remounted when it changes.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return undefined;
    const mark = () => { nudgedAtRef.current = Date.now(); };
    container.addEventListener("wheel", mark, { passive: true });
    container.addEventListener("touchmove", mark, { passive: true });
    container.addEventListener("pointerdown", mark);
    return () => {
      container.removeEventListener("wheel", mark);
      container.removeEventListener("touchmove", mark);
      container.removeEventListener("pointerdown", mark);
    };
  }, [state.status, lines.length]);

  // Auto-scroll inside the pane only — scrollIntoView would drag the whole page.
  //
  // Measured with getBoundingClientRect, NOT offsetTop: offsetTop is relative to the nearest
  // POSITIONED ancestor, which in the overlays is the overlay itself rather than this pane, so the
  // old maths handed scrollTo a page-sized number and the pane just sat pinned at its bottom.
  useEffect(() => {
    const container = containerRef.current;
    const el = activeRef.current;
    if (!container || !el) return;
    if (container.scrollHeight <= container.clientHeight + 1) return; // nothing to scroll
    if (Date.now() - nudgedAtRef.current < MANUAL_SCROLL_GRACE_MS) return;
    const delta = el.getBoundingClientRect().top - container.getBoundingClientRect().top;
    const target = container.scrollTop + delta - (container.clientHeight - el.clientHeight) / 2;
    container.scrollTo({ top: Math.max(0, target), behavior: "smooth" });
  }, [active]);

  const mod = variant === "overlay" ? " music-np-lyrics--overlay" : "";

  if (state.status === "loading")
    return <div className={`music-np-lyrics-empty${mod}`}>Loading lyrics…</div>;

  if (state.status === "empty")
    return (
      <div className={`music-np-lyrics-empty${mod}`}>
        No lyrics for this track yet.
        <span>Lyrics come from the file's own tags, a sidecar .lrc, or LRCLIB.</span>
      </div>
    );

  if (lines.length > 0) {
    return (
      <div
        className={`music-np-lyrics music-np-lyrics--synced${mod}`}
        ref={containerRef}
        data-testid="music-lyrics-synced"
      >
        {lines.map((line, i) => (
          <p
            key={`${line.time}-${i}`}
            ref={i === active ? activeRef : null}
            className={`music-np-line${i === active ? " music-np-line--active" : ""}`}
          >
            {line.text || "♪"}
          </p>
        ))}
      </div>
    );
  }

  return (
    <div className={`music-np-lyrics${mod}`} ref={containerRef} data-testid="music-lyrics-plain">
      <pre className="music-np-plain">{state.plainText || state.syncedLrc}</pre>
    </div>
  );
}
