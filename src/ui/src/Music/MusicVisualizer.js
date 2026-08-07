import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  DEFAULT_POOL,
  POOLS,
  fetchPreset,
  loadPresetIndex,
  pickRandom,
  prefetchPreset,
  presetsInPool,
  searchPresets,
  splitPresetName,
} from "./butterchurnPresets";
import "./MusicVisualizer.css";

// ── Milkdrop visualization (music-plan.md §2.8) ─────────────────────────────
// Butterchurn is the real Milkdrop 2 preset engine in WebGL. The ENGINE is imported dynamically —
// it's heavy, and it stays off the wire entirely for anyone who never opens the visualizer. The
// PRESETS are not imported at all any more: they're static JSON under /butterchurn, fetched one at
// a time (see butterchurnPresets.js). That swap is what took this from the 100-preset base pack to
// the full ~1,750-preset corpus without adding a byte to the initial load.
//
// Mounting/unmounting this component must never interrupt playback: it only READS the shared audio
// graph the context owns (source → analyser → destination), never rebuilds it and never closes the
// AudioContext. The graph outlives every visualizer session.

const CYCLE_MS = 30000;    // auto-advance to another random preset every ~30s
const IDLE_MS = 2500;      // fullscreen: hide the controls + cursor after this much stillness
const TOAST_MS = 2400;     // fullscreen: how long the preset name stays up after a change
const BROWSER_ROWS = 300;  // rendered rows cap — 1,750 <li>s is a scroll-jank machine

const POOL_KEY = "music.viz.pool";
const FAVORITES_KEY = "music.viz.favorites";
const CYCLE_KEY = "music.viz.cycle";
const PRESET_KEY = "music.viz.preset";

function readStored(key, fallback) {
  try {
    const raw = window.localStorage.getItem(key);
    return raw === null ? fallback : raw;
  } catch {
    return fallback; // private mode
  }
}

function writeStored(key, value) {
  try { window.localStorage.setItem(key, value); } catch { /* private mode */ }
}

function readFavorites() {
  try {
    const parsed = JSON.parse(window.localStorage.getItem(FAVORITES_KEY) || "[]");
    return new Set(Array.isArray(parsed) ? parsed : []);
  } catch {
    return new Set();
  }
}

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

// butterchurn ships a UMD bundle, and a UMD default export reaches ESM differently in dev
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
  const rafRef = useRef(0);
  // Every preset application is async now, so a fast ▶▶▶ can resolve out of order. Latest wins.
  const applyTokenRef = useRef(0);

  const [status, setStatus] = useState("loading"); // loading | ready | unsupported | error
  const [presets, setPresets] = useState([]);
  const [current, setCurrent] = useState(null);    // an index entry: { s, n, t }
  const [pool, setPool] = useState(() => {
    const stored = readStored(POOL_KEY, DEFAULT_POOL);
    return POOLS.some((p) => p.id === stored) ? stored : DEFAULT_POOL;
  });
  const [favorites, setFavorites] = useState(readFavorites);
  const [cycling, setCycling] = useState(() => readStored(CYCLE_KEY, "1") !== "0");
  const [browserOpen, setBrowserOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [idle, setIdle] = useState(false);

  // The context value's identity changes on every play/pause and every track change. Depending on
  // `player` here would tear down and rebuild the whole WebGL visualizer each time; ensureAudioGraph
  // is a useCallback with no deps, so it is the stable handle to depend on.
  const ensureAudioGraph = player?.ensureAudioGraph;

  // Mirrors so the boot effect can read the CURRENT pool/favorites for its first pick without
  // taking them as dependencies — that would tear down and rebuild WebGL every time you switch pool.
  const poolRef = useRef(pool);
  const favoritesRef = useRef(favorites);
  useEffect(() => { poolRef.current = pool; }, [pool]);
  useEffect(() => { favoritesRef.current = favorites; }, [favorites]);

  const poolList = useMemo(
    () => presetsInPool(presets, pool, favorites),
    [presets, pool, favorites]
  );
  // Counts for the pool chips. Memoised because this is four passes over ~1,750 entries and the
  // browser re-renders on every keystroke in the search box.
  const poolCounts = useMemo(
    () => Object.fromEntries(POOLS.map((p) => [p.id, presetsInPool(presets, p.id, favorites).length])),
    [presets, favorites]
  );

  /// Fetch a preset and hand it to butterchurn. The label updates immediately (optimistic) so the
  /// UI never feels like it ignored the click; a failed fetch leaves the previous visuals running.
  const applyPreset = useCallback((entry, blend = 2.0) => {
    if (!entry) return;
    const token = (applyTokenRef.current += 1);
    setCurrent(entry);
    fetchPreset(entry.s)
      .then((preset) => {
        if (applyTokenRef.current !== token || !vizRef.current) return;
        vizRef.current.loadPreset(preset, blend);
        writeStored(PRESET_KEY, entry.s);
      })
      .catch((err) => console.warn("[music] preset failed to load", entry.s, err));
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

  // Boot: WebGL2 check → engine + catalogue in parallel → visualizer wired to the shared graph →
  // first preset → rAF render loop.
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
        const [butterchurnModule, index] = await Promise.all([
          import("butterchurn"),
          loadPresetIndex(),
        ]);
        if (cancelled) return;

        const butterchurn = unwrapModule(butterchurnModule, "createVisualizer");

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

        setPresets(index);
        setStatus("ready");

        // Reopen on whatever was last showing, so the visualizer feels like it has a memory;
        // otherwise a random pick out of the active pool.
        const remembered = readStored(PRESET_KEY, null);
        const startList = presetsInPool(index, poolRef.current, favoritesRef.current);
        const start =
          index.find((p) => p.s === remembered) ||
          pickRandom(startList.length ? startList : index, null);
        if (start) {
          setCurrent(start);
          fetchPreset(start.s)
            .then((preset) => {
              if (!cancelled && vizRef.current) vizRef.current.loadPreset(preset, 0.0);
            })
            .catch((err) => console.warn("[music] first preset failed to load", start.s, err));
        }

        const tick = () => {
          rafRef.current = window.requestAnimationFrame(tick);
          try { visualizer.render(); } catch { /* a bad preset must not kill the loop */ }
        };
        rafRef.current = window.requestAnimationFrame(tick);
      } catch (err) {
        // Surfaced, not swallowed: a preset index that never got published or a WebGL context the
        // browser refuses is otherwise invisible behind the friendly message.
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
  // stops, and come straight back on any movement. The browser panel pins them open — you can't
  // pick from a list that fades out from under the cursor.
  useEffect(() => {
    if (!isFullscreen || browserOpen) {
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
  }, [isFullscreen, browserOpen]);

  // Auto-advance. The next preset is CHOSEN and PREFETCHED now rather than at fire time, so the
  // 30-second change is instant instead of a visible fetch-then-blend. Re-arms on every change, so
  // a manual pick also gets a full window before the next shuffle.
  useEffect(() => {
    if (status !== "ready" || !cycling || poolList.length < 2) return undefined;
    const next = pickRandom(poolList, current?.s);
    prefetchPreset(next?.s);
    const timer = window.setTimeout(() => applyPreset(next), CYCLE_MS);
    return () => window.clearTimeout(timer);
  }, [status, cycling, poolList, current, applyPreset]);

  // The preset picker is out of the way in fullscreen, so name changes announce themselves instead.
  const [toast, setToast] = useState(false);
  useEffect(() => {
    if (!isFullscreen || !current) return undefined;
    setToast(true);
    const timer = window.setTimeout(() => setToast(false), TOAST_MS);
    return () => window.clearTimeout(timer);
  }, [current, isFullscreen]);

  const step = useCallback((delta) => {
    if (poolList.length === 0) return;
    const at = poolList.findIndex((p) => p.s === current?.s);
    // Not in the current pool (you just switched pools) — step from the start rather than nowhere.
    const next = at < 0 ? poolList[delta > 0 ? 0 : poolList.length - 1]
                        : poolList[(at + delta + poolList.length) % poolList.length];
    applyPreset(next);
  }, [poolList, current, applyPreset]);

  const shuffle = useCallback(() => {
    applyPreset(pickRandom(poolList, current?.s));
  }, [poolList, current, applyPreset]);

  const toggleFullscreen = useCallback(() => {
    const wrap = wrapRef.current;
    if (!wrap) return;
    if (document.fullscreenElement) document.exitFullscreen?.();
    else wrap.requestFullscreen?.().catch(() => {});
  }, []);

  const toggleCycling = useCallback(() => {
    setCycling((on) => {
      writeStored(CYCLE_KEY, on ? "0" : "1");
      return !on;
    });
  }, []);

  const choosePool = useCallback((id) => {
    setPool(id);
    writeStored(POOL_KEY, id);
  }, []);

  const toggleFavorite = useCallback((slug) => {
    setFavorites((prev) => {
      const next = new Set(prev);
      if (next.has(slug)) next.delete(slug);
      else next.add(slug);
      writeStored(FAVORITES_KEY, JSON.stringify([...next]));
      return next;
    });
  }, []);

  // Keyboard: the visualizer is usually the only thing on screen, especially in fullscreen where
  // there are no visible controls at all once the mouse rests. Typing in the search box is not a
  // shortcut, so text fields are excluded.
  useEffect(() => {
    if (status !== "ready") return undefined;
    const onKey = (e) => {
      if (e.metaKey || e.ctrlKey || e.altKey) return;
      const tag = e.target?.tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || e.target?.isContentEditable) {
        if (e.key === "Escape") setBrowserOpen(false);
        return;
      }
      switch (e.key) {
        case "ArrowRight": step(1); break;
        case "ArrowLeft": step(-1); break;
        case "r": case "R": shuffle(); break;
        case "f": case "F": toggleFullscreen(); break;
        case "l": case "L": toggleCycling(); break;
        case "b": case "B": case "/": e.preventDefault(); setBrowserOpen((o) => !o); break;
        case "Escape": setBrowserOpen(false); break;
        default: break;
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [status, step, shuffle, toggleFullscreen, toggleCycling]);

  const matches = useMemo(() => searchPresets(poolList, query), [poolList, query]);
  const shown = matches.length > BROWSER_ROWS ? matches.slice(0, BROWSER_ROWS) : matches;
  const currentName = current?.n || "";
  const { author, title } = splitPresetName(currentName);

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

      {isFullscreen && toast && currentName && (
        <div className="music-viz-toast" data-testid="music-visualizer-toast">
          <span className="music-viz-toast-title">{title}</span>
          {author && <span className="music-viz-toast-author">{author}</span>}
        </div>
      )}

      {browserOpen && status === "ready" && (
        <div className="music-viz-browser" data-testid="music-visualizer-browser">
          <div className="music-viz-browser-head">
            <input
              className="music-viz-browser-search"
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search presets — try “geiss”, “fractal”, “rovastar”"
              aria-label="Search presets"
              autoFocus
            />
            <button
              className="music-viz-browser-close"
              onClick={() => setBrowserOpen(false)}
              title="Close preset browser"
              aria-label="Close preset browser"
            >
              ✕
            </button>
          </div>

          <div className="music-viz-browser-pools" role="group" aria-label="Preset collection">
            {POOLS.map((p) => (
              <button
                key={p.id}
                className={`music-viz-pool${pool === p.id ? " music-viz-pool--on" : ""}`}
                onClick={() => choosePool(p.id)}
                aria-pressed={pool === p.id}
              >
                {p.label}
                <span className="music-viz-pool-count">{poolCounts[p.id]}</span>
              </button>
            ))}
          </div>

          <div className="music-viz-browser-list" data-testid="music-visualizer-preset-list">
            {shown.length === 0 && (
              <div className="music-viz-browser-empty">
                {pool === "favorites" && !query
                  ? "No favorites yet — hit the ★ on a preset you like."
                  : "Nothing matches that search."}
              </div>
            )}
            {shown.map((entry) => {
              const parts = splitPresetName(entry.n);
              const isCurrent = entry.s === current?.s;
              return (
                <div
                  key={entry.s}
                  className={`music-viz-row${isCurrent ? " music-viz-row--current" : ""}`}
                >
                  <button
                    className="music-viz-row-pick"
                    onClick={() => applyPreset(entry)}
                    title={entry.n}
                  >
                    <span className="music-viz-row-title">{parts.title}</span>
                    {parts.author && <span className="music-viz-row-author">{parts.author}</span>}
                  </button>
                  <button
                    className={`music-viz-row-fav${favorites.has(entry.s) ? " music-viz-row-fav--on" : ""}`}
                    onClick={() => toggleFavorite(entry.s)}
                    title={favorites.has(entry.s) ? "Remove from favorites" : "Add to favorites"}
                    aria-label={favorites.has(entry.s) ? "Remove from favorites" : "Add to favorites"}
                    aria-pressed={favorites.has(entry.s)}
                  >
                    ★
                  </button>
                </div>
              );
            })}
          </div>

          <div className="music-viz-browser-foot">
            {matches.length.toLocaleString()} preset{matches.length === 1 ? "" : "s"}
            {matches.length > shown.length && ` · showing the first ${BROWSER_ROWS} — keep typing to narrow it`}
          </div>
        </div>
      )}

      <div className="music-viz-controls">
        <button onClick={() => step(-1)} title="Previous preset (←)" aria-label="Previous preset">◀</button>
        <button onClick={() => step(1)} title="Next preset (→)" aria-label="Next preset">▶</button>
        {/* Text glyphs, not emoji — the rest of the music UI (⏮ ⏸ ▶ ⏭ ◉ ♪ ☰ ✕) is monochrome, and
            an emoji falls back to a tofu box anywhere the colour font is missing. */}
        <button onClick={shuffle} title="Random preset (R)" aria-label="Random preset">⇄</button>

        <button
          className="music-viz-name"
          onClick={() => setBrowserOpen((o) => !o)}
          title={currentName ? `${currentName} — browse presets (B)` : "Browse presets (B)"}
          aria-label="Browse presets"
          aria-expanded={browserOpen}
          data-testid="music-visualizer-browse"
        >
          <span className="music-viz-name-title">{title || "Presets"}</span>
          {author && <span className="music-viz-name-author">{author}</span>}
        </button>

        {/* One glyph, two states: the pressed styling carries on/off, so there's no second symbol to
            learn (and no "is the lock the on state or the off state?" moment). */}
        <button
          className={`music-viz-cycle${cycling ? " music-viz-cycle--on" : ""}`}
          onClick={toggleCycling}
          title={cycling ? "Changing preset every 30s — click to keep this one (L)" : "Holding this preset — click to resume auto-change (L)"}
          aria-label={cycling ? "Stop changing presets automatically" : "Change presets automatically"}
          aria-pressed={cycling}
        >
          ↻
        </button>
        <button
          className={`music-viz-fav${current && favorites.has(current.s) ? " music-viz-fav--on" : ""}`}
          onClick={() => current && toggleFavorite(current.s)}
          disabled={!current}
          title={current && favorites.has(current.s) ? "Remove from favorites" : "Add to favorites"}
          aria-label={current && favorites.has(current.s) ? "Remove from favorites" : "Add to favorites"}
          aria-pressed={!!current && favorites.has(current.s)}
        >
          ★
        </button>

        <button
          onClick={toggleFullscreen}
          title={isFullscreen ? "Exit fullscreen (F)" : "Fullscreen (F)"}
          aria-label={isFullscreen ? "Exit fullscreen" : "Fullscreen"}
        >
          {isFullscreen ? "⤡" : "⛶"}
        </button>
        {onClose && <button onClick={onClose} title="Close visualizer" aria-label="Close visualizer">✕</button>}
      </div>
    </div>
  );
}
