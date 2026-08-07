import { useEffect, useMemo, useRef, useState } from "react";
import { MovieAPI } from "../MovieAPI";
import { parseLrc, activeLineIndex } from "./lrc";
import { LYRICS_DEFAULTS, lyricsCreepPxPerSec, lyricsFontStack } from "./MusicLyricsSettings";
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
export default function MusicLyricsPane({
  trackId,
  position,
  variant = "pane",
  settings = LYRICS_DEFAULTS,
  // Untimed lyrics creep only while the music is actually going. Defaulted true so a caller that
  // doesn't track playback still behaves the way this always did.
  playing = true,
}) {
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
    // "Scroll with the song" off means the pane stays exactly where the reader left it — including
    // at the very top, which is the whole point of being able to switch this off.
    if (!settings.follow) return;
    if (container.scrollHeight <= container.clientHeight + 1) return; // nothing to scroll
    if (Date.now() - nudgedAtRef.current < MANUAL_SCROLL_GRACE_MS) return;
    const delta = el.getBoundingClientRect().top - container.getBoundingClientRect().top;
    const target = container.scrollTop + delta - (container.clientHeight - el.clientHeight) / 2;
    container.scrollTo({ top: Math.max(0, target), behavior: "smooth" });
  }, [active, settings.follow]);

  // ── Creep, for lyrics with no timestamps ───────────────────────────────────
  // Most of this library's lyrics are a plain block of text: there is no active line to follow, so
  // the pane simply sat still and every verse past the first had to be scrolled by hand while your
  // hands were elsewhere. This walks it down at reading pace instead.
  //
  // rAF rather than an interval so the rate is tied to real elapsed time — an interval that misses
  // ticks in a background tab would silently scroll at whatever rate it managed. `behavior: "auto"`
  // is explicit because the container sets scroll-behavior: smooth, which would otherwise turn every
  // sub-pixel step into its own animation.
  const synced = lines.length > 0;
  useEffect(() => {
    if (synced || !settings.follow || !playing) return undefined;
    const container = containerRef.current;
    if (!container) return undefined;
    const pxPerSec = lyricsCreepPxPerSec(settings.creep);
    let frame = 0;
    let last = 0;
    const tick = (now) => {
      frame = requestAnimationFrame(tick);
      if (!last) { last = now; return; }
      // Clamped: rAF stops firing in a hidden tab, so the first frame back can carry minutes of
      // elapsed time and would fling the lyrics to the end of the song in one step.
      const dt = Math.min((now - last) / 1000, 0.1);
      last = now;
      // Hands off while the reader is scrolling themselves, and stop dead at the end rather than
      // pinning against the bottom forever.
      if (Date.now() - nudgedAtRef.current < MANUAL_SCROLL_GRACE_MS) return;
      const max = container.scrollHeight - container.clientHeight;
      if (max <= 1 || container.scrollTop >= max - 0.5) return;
      container.scrollTo({ top: Math.min(max, container.scrollTop + pxPerSec * dt), behavior: "auto" });
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
    // scale is a dependency because it changes whether there is anything to scroll at all: bigger
    // text can push a block that fitted into one that doesn't.
  }, [synced, settings.follow, settings.creep, settings.scale, playing, state.status]);

  const mod = variant === "overlay" ? " music-np-lyrics--overlay" : "";
  // The scale rides a CSS variable so each surface keeps its own base size (the overlay's lines are
  // larger than the Now Playing column's, and both shrink on a phone) and the setting multiplies
  // whichever one applies — a flat font-size here would flatten all of that into one number.
  const styleVars = {
    "--music-lyrics-scale": settings.scale,
    fontFamily: lyricsFontStack(settings.font),
  };

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
        style={styleVars}
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
    <div className={`music-np-lyrics${mod}`} ref={containerRef} style={styleVars} data-testid="music-lyrics-plain">
      <pre className="music-np-plain">{state.plainText || state.syncedLrc}</pre>
    </div>
  );
}
