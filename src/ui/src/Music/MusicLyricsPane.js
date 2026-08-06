import { useEffect, useMemo, useRef, useState } from "react";
import { MovieAPI } from "../MovieAPI";
import { parseLrc, activeLineIndex } from "./lrc";

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

  // Auto-scroll inside the pane only — scrollIntoView would drag the whole page.
  useEffect(() => {
    const container = containerRef.current;
    const el = activeRef.current;
    if (!container || !el) return;
    const target = el.offsetTop - container.clientHeight / 2 + el.clientHeight / 2;
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
