import { useState, useEffect, useRef } from "react";
import Hls from "hls.js";
import { MovieAPI } from "../../MovieAPI";
import { createHls } from "../../streamEngine";
import FallbackImage from "../../Components/FallbackImage";
import { clockLabel } from "./ChannelGrid";
import { PREVIEW_BPS, PREVIEW_DEBOUNCE_MS, previewCapabilities, minutesLeft } from "./guideModel";

const TICKS_PER_SECOND = 10_000_000;
const KEEPALIVE_MS = 10_000; // Jellyfin's HLS job dies without a beat inside 60 s (segment fetches don't count)

/**
 * The guide's live preview (guide v2): the selected programme playing muted in the detail panel, the
 * way a cable box previews the highlighted channel. It is a real stream — one cheap re-encode
 * (PREVIEW_BPS, ≤ 854 px wide) — so it is spent carefully:
 *
 *   * it runs only for the programme airing NOW on a channel that is not frozen, and only once the
 *     viewer has clicked a programme (`armed`); an auto-selected programme shows its poster and a
 *     ▶ Preview button instead, so merely opening /channels spawns nothing;
 *   * selections are debounced, so flicking across cells spawns one ffmpeg, not one per click;
 *   * it PEEKS (`getChannelNow(..., { peek: true })`) instead of calling the room's Now — no vote
 *     weight, no telemetry, never auto-resumes a frozen channel — and reports `passive` progress
 *     beats, which write no resume position and never mark anything watched;
 *   * a hidden tab stops it (and restarts it on return), and unmount/pagehide stop the session by
 *     playSessionId, so a room open in another tab is never killed with it.
 *
 * Any failure — the theater is full (503), a fatal decode error, no answer — falls back to the poster
 * with a quiet "Preview unavailable"; it never retry-loops.
 */
export default function GuidePreview({ channelId, program, live, paused, armed, onArm, nowMs, poster }) {
  const videoRef = useRef(null);
  const hlsRef = useRef(null);
  const sessionRef = useRef(null);
  const [phase, setPhase] = useState("idle"); // idle | loading | playing | unavailable
  const [muted, setMuted] = useState(true);
  const [hidden, setHidden] = useState(() => typeof document !== "undefined" && document.hidden);

  const active = !!(armed && live && !paused && channelId && program?.playableId);

  // A hidden tab holds no picture anyone can see — stop the encode, and come back when the tab does.
  useEffect(() => {
    const onVis = () => setHidden(document.hidden);
    document.addEventListener("visibilitychange", onVis);
    return () => document.removeEventListener("visibilitychange", onVis);
  }, []);

  useEffect(() => {
    if (!active || hidden) {
      setPhase("idle");
      return undefined;
    }
    let cancelled = false;
    let keepalive = null;
    const ctrl = new AbortController();
    setPhase("loading"); // the caption reads "Tuning preview…" through the debounce too

    const stopSession = (useBeacon = false) => {
      const s = sessionRef.current;
      sessionRef.current = null;
      if (!s) return;
      const payload = { playSessionId: s.playSessionId, playableId: s.playableId };
      if (useBeacon) MovieAPI.beaconStopStream(payload);
      else MovieAPI.stopStream(payload);
    };
    const onPageHide = () => stopSession(true);

    const run = async () => {
      try {
        const now = await MovieAPI.getChannelNow(channelId, ctrl.signal, { peek: true });
        if (cancelled) return;
        // The channel moved on (or froze) between the click and the peek — the parent re-selects on
        // its next tick; show the poster meanwhile rather than a stream of a different programme.
        if (!now.current || now.paused || now.current.playableId !== program.playableId) {
          setPhase("idle");
          return;
        }
        const joinAt = Math.max(0, Math.floor(now.current.offsetSeconds || 0));
        const startResponse = await MovieAPI.startStream({
          playableId: now.current.playableId,
          startSeconds: joinAt,
          maxBitrateBps: PREVIEW_BPS,
          capabilities: previewCapabilities(),
        });
        if (cancelled) return;
        if (!startResponse.ok) throw Object.assign(new Error("start failed"), { status: startResponse.status });
        const session = await startResponse.json();
        if (cancelled) {
          MovieAPI.stopStream({ playSessionId: session.playSessionId, playableId: now.current.playableId });
          return;
        }
        sessionRef.current = { playSessionId: session.playSessionId, playableId: now.current.playableId };
        window.addEventListener("pagehide", onPageHide);

        const video = videoRef.current;
        if (!video) throw new Error("no element");
        video.muted = true;
        const start = () => video.play().catch(() => {});
        if (session.isHls === false || !Hls.isSupported()) {
          // Direct play can't happen under the preview ceiling, but Safari's native HLS can — either
          // way the join lever is a seek on metadata.
          video.src = session.hlsUrl;
          video.addEventListener("loadedmetadata", () => { video.currentTime = joinAt; start(); }, { once: true });
        } else {
          const hls = createHls({
            backBufferLength: 5,
            startPosition: joinAt,
            onFatal: () => { if (!cancelled) setPhase("unavailable"); },
          });
          hlsRef.current = hls;
          hls.on(Hls.Events.MANIFEST_PARSED, start);
          hls.loadSource(session.hlsUrl);
          hls.attachMedia(video);
        }
        video.addEventListener("playing", () => { if (!cancelled) setPhase("playing"); }, { once: true });

        keepalive = setInterval(() => {
          const s = sessionRef.current;
          const v = videoRef.current;
          if (!s) return;
          MovieAPI.reportStreamProgress({
            playSessionId: s.playSessionId,
            playableId: s.playableId,
            positionTicks: Math.floor((v?.currentTime || 0) * TICKS_PER_SECOND),
            paused: false,
            passive: true,
          });
        }, KEEPALIVE_MS);
      } catch {
        if (!cancelled) setPhase("unavailable");
      }
    };

    const debounce = setTimeout(run, PREVIEW_DEBOUNCE_MS);
    return () => {
      cancelled = true;
      clearTimeout(debounce);
      clearInterval(keepalive);
      ctrl.abort();
      window.removeEventListener("pagehide", onPageHide);
      if (hlsRef.current) {
        hlsRef.current.destroy();
        hlsRef.current = null;
      }
      const v = videoRef.current;
      if (v) {
        v.pause();
        v.removeAttribute("src");
        v.load();
      }
      stopSession();
    };
  }, [active, hidden, channelId, program?.playableId, program?.startUtc]);

  useEffect(() => {
    if (videoRef.current) videoRef.current.muted = muted;
  }, [muted, phase]);

  const startMs = Date.parse(program?.startUtc);
  const endMs = Date.parse(program?.endUtc);
  const left = minutesLeft(program, nowMs);
  const elapsedPct = live && Number.isFinite(startMs) && endMs > startMs ? Math.min(100, Math.max(0, ((nowMs - startMs) / (endMs - startMs)) * 100)) : 0;
  const showVideo = active && !hidden && phase !== "unavailable";

  let caption = null;
  if (paused) caption = "Paused";
  else if (!live) caption = Number.isFinite(startMs) && startMs > nowMs ? `Starts ${clockLabel(startMs)}` : "Ended";
  else if (phase === "unavailable") caption = "Preview unavailable";
  else if (phase === "loading") caption = "Tuning preview…";

  const playing = phase === "playing" && showVideo;
  return (
    <div className={`guide-preview${playing ? " guide-preview--playing" : ""}`}>
      <div className="guide-preview__frame">
        {poster ? (
          <FallbackImage src={poster} alt="" className="guide-preview__poster" fallback={<div className="guide-preview__poster guide-preview__poster--empty" />} />
        ) : (
          <div className="guide-preview__poster guide-preview__poster--empty" />
        )}
        {showVideo && <video ref={videoRef} className="guide-preview__video" muted playsInline autoPlay aria-label="Live preview" />}
        {live && !paused && !armed && (
          <button type="button" className="guide-preview__arm" onClick={onArm}>▶ Preview</button>
        )}
        {caption && <span className="guide-preview__caption">{caption}</span>}
        {playing && <span className="guide-preview__live" aria-hidden="true"><span className="guide-preview__live-dot" />Live</span>}
        {playing && (
          <button type="button" className="guide-preview__mute" aria-pressed={!muted} onClick={() => setMuted((m) => !m)} title={muted ? "Unmute preview" : "Mute preview"} aria-label={muted ? "Unmute preview" : "Mute preview"}>
            {muted ? (
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M11 5 6 9H3v6h3l5 4z" /><path d="m22 9-6 6M16 9l6 6" /></svg>
            ) : (
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M11 5 6 9H3v6h3l5 4z" /><path d="M15.5 8.5a5 5 0 0 1 0 7M18.5 5.5a9 9 0 0 1 0 13" /></svg>
            )}
          </button>
        )}
      </div>
      {Number.isFinite(startMs) && Number.isFinite(endMs) && (
        <div className="guide-preview__slot">
          <span className="guide-preview__bar" aria-hidden="true"><span className="guide-preview__bar-fill" style={{ width: `${elapsedPct}%` }} /></span>
          <span className="guide-preview__times">
            <span>{clockLabel(startMs)} – {clockLabel(endMs)}</span>
            {live && left != null && <span>{left} min left</span>}
          </span>
        </div>
      )}
    </div>
  );
}
