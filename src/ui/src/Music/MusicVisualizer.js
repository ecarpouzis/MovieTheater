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

const CYCLE_MS = 30000; // auto-advance to another random preset every ~30s

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

  const loadPreset = useCallback((name, blend = 2.0) => {
    const presets = presetsRef.current;
    const viz = vizRef.current;
    if (!presets || !viz || !presets[name]) return;
    viz.loadPreset(presets[name], blend);
    setPresetName(name);
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

    const graph = player?.ensureAudioGraph?.();
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
        const width = Math.max(1, Math.floor(wrap.clientWidth));
        const height = Math.max(1, Math.floor(wrap.clientHeight));

        const visualizer = butterchurn.createVisualizer(graph.audioContext, canvas, {
          width,
          height,
          pixelRatio: window.devicePixelRatio || 1,
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
  }, [player]);

  // Keep the render surface matched to the box (and to fullscreen).
  useEffect(() => {
    const wrap = wrapRef.current;
    if (!wrap || typeof ResizeObserver === "undefined") return undefined;
    const observer = new ResizeObserver(() => {
      const viz = vizRef.current;
      if (!viz) return;
      const width = Math.max(1, Math.floor(wrap.clientWidth));
      const height = Math.max(1, Math.floor(wrap.clientHeight));
      viz.setRendererSize(width, height);
    });
    observer.observe(wrap);
    return () => observer.disconnect();
  }, []);

  // Random preset every ~30s, so leaving it open stays interesting.
  useEffect(() => {
    if (status !== "ready" || presetNames.length === 0) return undefined;
    const timer = window.setInterval(() => {
      loadPreset(presetNames[Math.floor(Math.random() * presetNames.length)]);
    }, CYCLE_MS);
    return () => window.clearInterval(timer);
  }, [status, presetNames, loadPreset]);

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

  return (
    <div className="music-viz" ref={wrapRef} data-testid="music-visualizer">
      <canvas className="music-viz-canvas" ref={canvasRef} data-testid="music-visualizer-canvas" />

      {status !== "ready" && (
        <div className="music-viz-message">
          {status === "loading" && "Starting the visualizer…"}
          {status === "unsupported" && "This browser can't run the visualizer (WebGL2 / Web Audio unavailable)."}
          {status === "error" && "The visualizer failed to load."}
        </div>
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
        <button onClick={toggleFullscreen} title="Fullscreen" aria-label="Fullscreen">⛶</button>
        {onClose && <button onClick={onClose} title="Close visualizer" aria-label="Close visualizer">✕</button>}
      </div>
    </div>
  );
}
