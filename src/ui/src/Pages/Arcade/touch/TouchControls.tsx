/**
 * The touch pad in PLAY mode: draws a layout over the game and turns fingers into RetroPad frames.
 *
 * Hot path stays out of React: fingers live in a ref, compose() (touchEngine.ts) decides what they
 * mean, and the result goes straight to `onFrame` (→ session.setVirtualInput) plus direct class/transform
 * writes for the lit state and stick knobs. React re-renders only when the layout or the layer size
 * changes.
 *
 * Layering contract (ArcadeRoomPage): the layer is `pointer-events: none` and only the controls take
 * touches, so a tap on the picture still reaches the DS/3DS touchscreen handler on the <video>; every
 * control carries `data-touch-control` so the room's fullscreen-chrome reveal ignores presses on it.
 */
import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type PointerEvent as RPointerEvent } from "react";
import { compose, floatingRadius, hitTest, knobOffset, type EngineState, type LayerSize } from "./touchEngine";
import { ACTION_LABEL, type Control, type Layout, type RoomAction } from "./touchLayout";
import "./touch.css";

export interface TouchControlsProps {
  layout: Layout;
  size: LayerSize;
  /** The frame the pad is holding, sent on every change (and a neutral frame on release/unmount). */
  onFrame: (mask: number, axes: [number, number, number, number]) => void;
  /** Action controls: engaged=true on press, false on lift (hold actions use both, one-shots the press). */
  onAction: (action: RoomAction, engaged: boolean) => void;
  /** Hide action controls the room's gate refuses (a competitive room has no ⏪). */
  actionAllowed: (action: RoomAction) => boolean;
}

const TURBO_HALF_PERIOD_MS = 33; // ~15 presses a second

/** The drawn part of a control, shared with the editor. Outer element = the touch target. */
export function controlBox(c: Control, size: LayerSize): CSSProperties {
  const u = Math.min(size.w, size.h);
  if (c.kind === "region") {
    return { left: (c.x - c.w / 2) * size.w, top: (c.y - c.h / 2) * size.h, width: c.w * size.w, height: c.h * size.h };
  }
  // The target is the engine's hit circle (1.15 × the drawn size), so DOM hits and engine hits agree.
  const d = c.s * u * 1.15;
  return { left: c.x * size.w - d / 2, top: c.y * size.h - d / 2, width: d, height: d };
}

export function ControlVisual({ c, size }: { c: Control; size: LayerSize }) {
  const u = Math.min(size.w, size.h);
  const px = c.kind === "region" ? 0 : c.s * u;
  const fontSize = Math.max(10, Math.min(28, px * 0.32));
  switch (c.kind) {
    case "button": {
      const pill = c.shape === "pill";
      const style: CSSProperties = { width: px, height: pill ? px * 0.55 : px, fontSize: pill ? fontSize * 0.8 : fontSize };
      const label = c.label ?? c.bits.join("+");
      return (
        <div className={`tc-face tc-face--${c.shape}`} style={style}>
          <span>{label}</span>
          {c.mode !== "press" && <i className="tc-badge">{c.mode === "turbo" ? "T" : "⇅"}</i>}
        </div>
      );
    }
    case "dpad":
      return (
        <div className="tc-face tc-face--dpad" style={{ width: px, height: px }}>
          <svg viewBox="0 0 100 100" aria-hidden="true">
            <path d="M36 4h28v32h32v28H64v32H36V64H4V36h32z" />
            <path className="tc-dpad-arrow" d="M50 12l-7 10h14zM50 88l-7-10h14zM12 50l10-7v14zM88 50l-10-7v14z" />
          </svg>
        </div>
      );
    case "stick":
      return (
        <div className="tc-face tc-face--stick" style={{ width: px, height: px }}>
          <div className="tc-knob" style={{ width: px * 0.46, height: px * 0.46 }} />
          {c.label && <span className="tc-stick-label">{c.label}</span>}
        </div>
      );
    case "region":
      return (
        <div className="tc-face tc-face--region">
          <span>{c.label || (c.output === "right" ? "Right stick zone" : c.output === "dpad" ? "D-pad zone" : "Stick zone")}</span>
        </div>
      );
    case "action":
      return (
        <div className="tc-face tc-face--action" style={{ width: px, height: px, fontSize: fontSize * 0.9 }}>
          <span>{c.label || ACTION_LABEL[c.action]}</span>
        </div>
      );
    default:
      return null;
  }
}

export default function TouchControls({ layout, size, onFrame, onAction, actionAllowed }: TouchControlsProps) {
  const layerRef = useRef<HTMLDivElement>(null);
  const elRefs = useRef(new Map<string, HTMLDivElement>());
  const ringRef = useRef(new Map<string, HTMLDivElement>()); // floating-stick rings, by control id
  const stateRef = useRef<EngineState>({ fingers: new Map(), toggled: new Set(), turboOn: false });
  const lastRef = useRef<string>("");
  const litRef = useRef<Set<string>>(new Set());
  const turboTimer = useRef<ReturnType<typeof setInterval> | null>(null);
  const [idle, setIdle] = useState(false);
  const idleTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Everything the event handlers read, kept current without re-binding them.
  const live = useRef({ layout, size, onFrame, onAction });
  live.current = { layout, size, onFrame, onAction };

  const visibleControls = layout.controls.filter((c) => c.kind !== "action" || actionAllowed(c.action));

  function push(force = false) {
    const { layout: l, size: sz, onFrame: send } = live.current;
    const st = stateRef.current;
    const f = compose(l, sz, st);
    const key = `${f.mask}|${f.axes.join(",")}`;
    if (force || key !== lastRef.current) { lastRef.current = key; send(f.mask, f.axes); }
    // Lit state + stick knobs, straight onto the DOM.
    for (const id of litRef.current) if (!f.lit.has(id)) elRefs.current.get(id)?.classList.remove("is-lit");
    for (const id of f.lit) elRefs.current.get(id)?.classList.add("is-lit");
    litRef.current = f.lit;
    drawKnobs();
    // Turbo needs a clock only while a turbo button is actually held.
    const turboHeld = [...st.fingers.values()].some((fi) => {
      const c = l.controls.find((k) => k.id === fi.controlId);
      return c?.kind === "button" && c.mode === "turbo";
    });
    if (turboHeld && !turboTimer.current) {
      turboTimer.current = setInterval(() => { stateRef.current.turboOn = !stateRef.current.turboOn; push(); }, TURBO_HALF_PERIOD_MS);
    } else if (!turboHeld && turboTimer.current) {
      clearInterval(turboTimer.current); turboTimer.current = null; st.turboOn = false;
    }
  }

  function drawKnobs() {
    const { layout: l, size: sz } = live.current;
    const fingers = [...stateRef.current.fingers.values()];
    for (const c of l.controls) {
      if (c.kind !== "stick" && c.kind !== "region") continue;
      const f = fingers.find((fi) => fi.controlId === c.id);
      const knob = elRefs.current.get(c.id)?.querySelector<HTMLElement>(".tc-knob");
      const ring = ringRef.current.get(c.id);
      if (!f) {
        if (knob) knob.style.transform = "";
        if (ring) ring.style.display = "none";
        continue;
      }
      const k = knobOffset(f, c, sz);
      if (c.kind === "stick" && !c.floating && knob) knob.style.transform = `translate(${k.dx}px, ${k.dy}px)`;
      if (ring && (c.kind === "region" || (c.kind === "stick" && c.floating))) {
        const r = floatingRadius(c, sz);
        ring.style.display = "block";
        ring.style.left = `${k.ox - r}px`; ring.style.top = `${k.oy - r}px`;
        ring.style.width = ring.style.height = `${2 * r}px`;
        const inner = ring.firstElementChild as HTMLElement | null;
        if (inner) inner.style.transform = `translate(${k.dx}px, ${k.dy}px)`;
      }
    }
  }

  function releaseAll() {
    const st = stateRef.current;
    for (const f of st.fingers.values()) {
      const c = live.current.layout.controls.find((k) => k.id === f.controlId);
      if (c?.kind === "action") live.current.onAction(c.action, false);
    }
    st.fingers.clear();
    st.toggled.clear();
    push(true);
  }

  function wakeIdle() {
    const fade = live.current.layout.idleFade;
    setIdle(false);
    if (idleTimer.current) clearTimeout(idleTimer.current);
    if (fade) idleTimer.current = setTimeout(() => setIdle(true), fade.afterMs);
  }

  const local = (e: RPointerEvent) => {
    const r = layerRef.current!.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  };

  function flash(id: string) {
    const el = elRefs.current.get(id);
    if (!el || !live.current.layout.flashOnPress) return;
    el.classList.remove("is-flash");
    void el.offsetWidth; // restart the animation
    el.classList.add("is-flash");
  }

  function buzz() {
    if (!live.current.layout.haptics) return;
    try { navigator.vibrate?.(8); } catch { /* not supported */ }
  }

  const onDown = (c: Control) => (e: RPointerEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* synthetic events */ }
    const { x, y } = local(e);
    const st = stateRef.current;
    st.fingers.set(e.pointerId, { controlId: c.id, x, y, ox: x, oy: y });
    if (c.kind === "button" && c.mode === "toggle") {
      if (st.toggled.has(c.id)) st.toggled.delete(c.id); else st.toggled.add(c.id);
    }
    if (c.kind === "action") live.current.onAction(c.action, true);
    flash(c.id);
    buzz();
    wakeIdle();
    push();
  };

  const onMove = (e: RPointerEvent<HTMLDivElement>) => {
    const st = stateRef.current;
    const f = st.fingers.get(e.pointerId);
    if (!f) return;
    e.preventDefault();
    const { x, y } = local(e);
    f.x = x; f.y = y;
    const { layout: l, size: sz } = live.current;
    const cur = l.controls.find((k) => k.id === f.controlId);
    // Slide: a thumb that started on a press-button can roll onto a neighbour (face-button rolls, jump→shoot).
    if (l.slide && cur?.kind === "button" && cur.mode === "press") {
      const hit = hitTest(l, sz, x, y, (k) => k.kind === "button" && k.mode === "press");
      if (hit && hit.id !== f.controlId) { f.controlId = hit.id; f.ox = x; f.oy = y; flash(hit.id); buzz(); }
    }
    push();
  };

  const onUp = (e: RPointerEvent<HTMLDivElement>) => {
    const st = stateRef.current;
    const f = st.fingers.get(e.pointerId);
    if (!f) return;
    st.fingers.delete(e.pointerId);
    const c = live.current.layout.controls.find((k) => k.id === f.controlId);
    if (c?.kind === "action") live.current.onAction(c.action, false);
    push();
  };

  // A new layout object mid-touch: release only fingers (and toggles) on controls that no longer exist.
  // Releasing ALL of them was wrong: a default layout is regenerated whenever the screen's aspect changes,
  // and on an iPhone in pseudo-fullscreen the Safari toolbar showing/hiding does exactly that — every held
  // button would let go mid-game. A finger on a control that survived keeps pressing it at its new spot.
  const prevLayoutRef = useRef(layout);
  useLayoutEffect(() => {
    const ids = new Set(layout.controls.map((c) => c.id));
    const st = stateRef.current;
    for (const [pid, f] of st.fingers) {
      if (ids.has(f.controlId)) continue;
      st.fingers.delete(pid);
      // A held hold-action (⏪/⏩) whose control vanished still owes the worker its release.
      const gone = prevLayoutRef.current.controls.find((c) => c.id === f.controlId);
      if (gone?.kind === "action") live.current.onAction(gone.action, false);
    }
    for (const id of st.toggled) if (!ids.has(id)) st.toggled.delete(id);
    prevLayoutRef.current = layout;
    push(true);
    // push reads everything through refs; only a new layout should re-run this.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [layout]);

  useEffect(() => {
    const onBlur = () => releaseAll();
    const onVis = () => { if (document.visibilityState !== "visible") releaseAll(); };
    window.addEventListener("blur", onBlur);
    document.addEventListener("visibilitychange", onVis);
    wakeIdle();
    return () => {
      window.removeEventListener("blur", onBlur);
      document.removeEventListener("visibilitychange", onVis);
      if (turboTimer.current) clearInterval(turboTimer.current);
      if (idleTimer.current) clearTimeout(idleTimer.current);
      // Unmount (hidden, editor opened, room left): never leave a button held on the worker.
      stateRef.current.fingers.clear();
      stateRef.current.toggled.clear();
      live.current.onFrame(0, [0, 0, 0, 0]);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const fadeTo = idle && layout.idleFade ? layout.idleFade.to : 1;

  return (
    <div ref={layerRef} className="touch-layer" style={{ "--tc-layer-opacity": layout.opacity * fadeTo } as CSSProperties}>
      {visibleControls.map((c) => (
        <div
          key={c.id}
          ref={(el) => { if (el) elRefs.current.set(c.id, el); else elRefs.current.delete(c.id); }}
          className={`tc tc--${c.kind}`}
          data-touch-control=""
          data-control-id={c.id}
          style={{ ...controlBox(c, size), "--tc-opacity": c.opacity ?? 1 } as CSSProperties}
          onPointerDown={onDown(c)}
          onPointerMove={onMove}
          onPointerUp={onUp}
          onPointerCancel={onUp}
          onLostPointerCapture={onUp}
          onContextMenu={(e) => e.preventDefault()}
        >
          <ControlVisual c={c} size={size} />
          <div className="tc-flash" />
        </div>
      ))}
      {/* Floating-stick rings are drawn where the thumb LANDS, so they live on the layer, not in the control. */}
      {visibleControls.filter((c) => c.kind === "region" || (c.kind === "stick" && c.floating)).map((c) => (
        <div key={`ring-${c.id}`} className="tc-ring" style={{ display: "none" }}
          ref={(el) => { if (el) ringRef.current.set(c.id, el); else ringRef.current.delete(c.id); }}>
          <div className="tc-ring-knob" />
        </div>
      ))}
    </div>
  );
}
