import { useCallback, useEffect, useRef, useState } from "react";
import "./MusicVisualizer.css";

// ── Milkdrop visualization (music-plan.md §2.8) ─────────────────────────────
// Butterchurn is the real Milkdrop 2 preset engine in WebGL. Both packages are heavy, so they are
// imported DYNAMICALLY here — that keeps them out of the main bundle and off the wire entirely for
// anyone who never opens the visualizer.
//
// Mounting/unmounting this component must never interrupt playback: it only READS the shared audio
// graph the context owns (source → analyser → destination), never rebuilds it and never closes the
// AudioContext. The graph outlives every visualizer session.

const CYCLE_MS = 30000;    // auto-advance to another random preset every ~30s
const IDLE_MS = 2500;      // fullscreen: hide the controls + cursor after this much stillness
const TOAST_MS = 2400;     // fullscreen: how long the preset name stays up after a change

// ── Render surface (the fullscreen-blur fix) ────────────────────────────────
// Butterchurn NEVER touches the canvas element's width/height attributes, and its renderToScreen
// sets the GL viewport to the width/height it was handed — in raw drawing-buffer pixels. So the
// backing store is ours to own. Leaving it at the HTML default (300×150) while asking for a
// viewport of, say, 1920×1080 makes GL clip the viewport to the buffer: you get the bottom-left
// corner of the frame, then CSS stretches those 300×150 pixels across the whole box. That was the
// blur, and fullscreen made it ~6× worse because the gap between the two grows with the box.
//
// The rule this file now keeps: canvas.width/height and the numbers passed to butterchurn are the
// SAME device pixels, and pixelRatio stays 1 — butterchurn multiplies texsize by pixelRatio but
// NOT the screen viewport, so any other value desyncs the two again.
const MAX_RENDER_PIXELS = 2560 * 1440; // ~3.7M: sharp on a 4K panel without stalling the warp mesh

export function surfaceSize(wrap) {
  const cssW = Math.max(1, Math.floor(wrap.clientWidth));
  const cssH = Math.max(1, Math.floor(wrap.clientHeight));
  const dpr = Math.max(1, window.devicePixelRatio || 1);
  // Render at device resolution, but never above the pixel budget — a 4K fullscreen would otherwise
  // run the mesh + three blur passes over 8.3M pixels and drop frames, which reads as worse than
  // the mild upscale capping costs.
  const scale = Math.min(dpr, Math.sqrt(MAX_RENDER_PIXELS / (cssW * cssH)));
  return {
    width: Math.max(1, Math.round(cssW * scale)),
    height: Math.max(1, Math.round(cssH * scale)),
  };
}

// Both packages ship UMD bundles, and a UMD default export reaches ESM differently in dev
// (unbundled, esbuild-prebundled) than in the production rollup build — the real object turns up at
// the module, at .default, or at .default.default depending on which path you're on. Unwrap by
// looking for the member we actually need instead of guessing the interop shape.
function unwrapModule(module, member) {
  for (const candidate of [module, module?.default, module?.default?.default]) {
    if (candidate && typeof candidate[member] === "function") return candidate;
  }
  return module?.default ?? module;
}

export default function MusicVisualizer({ player, onClose }) {
  const wrapRef = useRef(null);
  const canvasRef = useRef(null);
  const vizRef = useRef(null);
  const presetsRef = useRef(null);
  const rafRef = useRef(0);

  const [status, setStatus] = useState("loading"); // loading | ready | unsupported | error
  const [presetNames, setPresetNames] = useState([]);
  const [presetName, setPresetName] = useState("");
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [idle, setIdle] = useState(false);

  // The context value's identity changes on every play/pause and every track change. Depending on
  // `player` here would tear down and rebuild the whole WebGL visualizer each time; ensureAudioGraph
  // is a useCallback with no deps, so it is the stable handle to depend on.
  const ensureAudioGraph = player?.ensureAudioGraph;

  const loadPreset = useCallback((name, blend = 2.0) => {
    const presets = presetsRef.current;
    const viz = vizRef.current;
    if (!presets || !viz || !presets[name]) return;
    viz.loadPreset(presets[name], blend);
    setPresetName(name);
  }, []);

  /// Point the backing store AND the renderer at the same device-pixel box. Safe before the
  /// visualizer exists (boot calls it to size the canvas first), and cheap enough to call on
  /// every resize/fullscreen/monitor-change event.
  const applySize = useCallback(() => {
    const wrap = wrapRef.current;
    const canvas = canvasRef.current;
    if (!wrap || !canvas) return null;
    const size = surfaceSize(wrap);
    if (canvas.width !== size.width || canvas.height !== size.height) {
      canvas.width = size.width;
      canvas.height = size.height;
    }
    vizRef.current?.setRendererSize(size.width, size.height);
    return size;
  }, []);

  // Boot: WebGL2 check → dynamic import → visualizer wired to the shared graph → rAF render loop.
  useEffect(() => {
    let cancelled = false;

    // Butterchurn needs WebGL2; a browser without it gets a friendly message, not a broken canvas.
    const probe = document.createElement("canvas");
    if (!probe.getContext("webgl2")) {
      setStatus("unsupported");
      return undefined;
    }

    const graph = ensureAudioGraph?.();
    if (!graph) {
      setStatus("unsupported");
      return undefined;
    }

    (async () => {
      try {
        const [butterchurnModule, presetsModule] = await Promise.all([
          import("butterchurn"),
          import("butterchurn-presets"),
        ]);
        if (cancelled) return;

        const butterchurn = unwrapModule(butterchurnModule, "createVisualizer");
        const presetPack = unwrapModule(presetsModule, "getPresets");
        const presets = typeof presetPack.getPresets === "function" ? presetPack.getPresets() : presetPack;
        presetsRef.current = presets;

        const canvas = canvasRef.current;
        const wrap = wrapRef.current;
        if (!canvas || !wrap) return;

        // Size the backing store BEFORE creating the visualizer, then hand butterchurn the very
        // same numbers — see the MAX_RENDER_PIXELS note above for why they must agree.
        const size = surfaceSize(wrap);
        canvas.width = size.width;
        canvas.height = size.height;

        const visualizer = butterchurn.createVisualizer(graph.audioContext, canvas, {
          width: size.width,
          height: size.height,
          pixelRatio: 1,
          textureRatio: 1,
        });
        // connectAudio builds butterchurn's own analysers off our source node; the source stays
        // connected to the destination, so the speakers are unaffected.
        visualizer.connectAudio(graph.source);
        vizRef.current = visualizer;

        const names = Object.keys(presets);
        setPresetNames(names);
        const first = names[Math.floor(Math.random() * names.length)];
        visualizer.loadPreset(presets[first], 0.0);
        setPresetName(first);
        setStatus("ready");

        const tick = () => {
          rafRef.current = window.requestAnimationFrame(tick);
          try { visualizer.render(); } catch { /* a bad preset must not kill the loop */ }
        };
        rafRef.current = window.requestAnimationFrame(tick);
      } catch (err) {
        // Surfaced, not swallowed: a preset pack that fails to parse or a WebGL context the browser
        // refuses is otherwise invisible behind the friendly message.
        console.error("[music] visualizer failed to start", err);
        if (!cancelled) setStatus("error");
      }
    })();

    return () => {
      cancelled = true;
      if (rafRef.current) window.cancelAnimationFrame(rafRef.current);
      rafRef.current = 0;
      vizRef.current = null;
      // Deliberately NOT closing the AudioContext or disconnecting the source: the graph belongs to
      // the <audio> element, and tearing it down would silence playback permanently.
    };
  }, [ensureAudioGraph]);

  // Keep the render surface matched to the box. ResizeObserver covers layout and the fullscreen
  // promotion; the window listener additionally catches a devicePixelRatio change (dragging the
  // window to a monitor with a different scale factor leaves the CSS box the same size).
  useEffect(() => {
    const wrap = wrapRef.current;
    if (!wrap) return undefined;
    const observer = typeof ResizeObserver !== "undefined" ? new ResizeObserver(() => applySize()) : null;
    observer?.observe(wrap);
    window.addEventListener("resize", applySize);
    return () => {
      observer?.disconnect();
      window.removeEventListener("resize", applySize);
    };
  }, [applySize]);

  // Fullscreen: re-size immediately rather than waiting on the observer, and track the state so the
  // button label and the idle-hide behaviour know where they are.
  useEffect(() => {
    const onChange = () => {
      setIsFullscreen(document.fullscreenElement === wrapRef.current);
      setIdle(false);
      applySize();
    };
    document.addEventListener("fullscreenchange", onChange);
    return () => document.removeEventListener("fullscreenchange", onChange);
  }, [applySize]);

  // Fullscreen is a "watch it" mode: the chrome and the pointer get out of the way once the mouse
  // stops, and come straight back on any movement.
  useEffect(() => {
    if (!isFullscreen) {
      setIdle(false);
      return undefined;
    }
    let timer = window.setTimeout(() => setIdle(true), IDLE_MS);
    const wake = () => {
      setIdle(false);
      window.clearTimeout(timer);
      timer = window.setTimeout(() => setIdle(true), IDLE_MS);
    };
    const wrap = wrapRef.current;
    wrap?.addEventListener("pointermove", wake);
    wrap?.addEventListener("pointerdown", wake);
    wrap?.addEventListener("keydown", wake);
    return () => {
      window.clearTimeout(timer);
      wrap?.removeEventListener("pointermove", wake);
      wrap?.removeEventListener("pointerdown", wake);
      wrap?.removeEventListener("keydown", wake);
    };
  }, [isFullscreen]);

  // Random preset every ~30s, so leaving it open stays interesting.
  useEffect(() => {
    if (status !== "ready" || presetNames.length === 0) return undefined;
    const timer = window.setInterval(() => {
      loadPreset(presetNames[Math.floor(Math.random() * presetNames.length)]);
    }, CYCLE_MS);
    return () => window.clearInterval(timer);
  }, [status, presetNames, loadPreset]);

  // The preset picker is hidden in fullscreen, so name changes announce themselves instead.
  const [toast, setToast] = useState(false);
  useEffect(() => {
    if (!isFullscreen || !presetName) return undefined;
    setToast(true);
    const timer = window.setTimeout(() => setToast(false), TOAST_MS);
    return () => window.clearTimeout(timer);
  }, [presetName, isFullscreen]);

  const step = (delta) => {
    if (presetNames.length === 0) return;
    const at = presetNames.indexOf(presetName);
    const next = (at + delta + presetNames.length) % presetNames.length;
    loadPreset(presetNames[next]);
  };

  const toggleFullscreen = () => {
    const wrap = wrapRef.current;
    if (!wrap) return;
    if (document.fullscreenElement) document.exitFullscreen?.();
    else wrap.requestFullscreen?.().catch(() => {});
  };

  const className = [
    "music-viz",
    isFullscreen ? "music-viz--fullscreen" : "",
    isFullscreen && idle ? "music-viz--idle" : "",
  ].filter(Boolean).join(" ");

  return (
    <div className={className} ref={wrapRef} data-testid="music-visualizer" tabIndex={-1}>
      <canvas className="music-viz-canvas" ref={canvasRef} data-testid="music-visualizer-canvas" />

      {status !== "ready" && (
        <div className="music-viz-message">
          {status === "loading" && "Starting the visualizer…"}
          {status === "unsupported" && "This browser can't run the visualizer (WebGL2 / Web Audio unavailable)."}
          {status === "error" && "The visualizer failed to load."}
        </div>
      )}

      {isFullscreen && toast && presetName && (
        <div className="music-viz-toast" data-testid="music-visualizer-toast">{presetName}</div>
      )}

      <div className="music-viz-controls">
        <button onClick={() => step(-1)} title="Previous preset" aria-label="Previous preset">◀</button>
        <select
          value={presetName}
          onChange={(e) => loadPreset(e.target.value)}
          aria-label="Preset"
          disabled={presetNames.length === 0}
        >
          {presetNames.map((n) => (
            <option key={n} value={n}>{n}</option>
          ))}
        </select>
        <button onClick={() => step(1)} title="Next preset" aria-label="Next preset">▶</button>
        <button
          onClick={toggleFullscreen}
          title={isFullscreen ? "Exit fullscreen" : "Fullscreen"}
          aria-label={isFullscreen ? "Exit fullscreen" : "Fullscreen"}
        >
          {isFullscreen ? "⤡" : "⛶"}
        </button>
        {onClose && <button onClick={onClose} title="Close visualizer" aria-label="Close visualizer">✕</button>}
      </div>
    </div>
  );
}
