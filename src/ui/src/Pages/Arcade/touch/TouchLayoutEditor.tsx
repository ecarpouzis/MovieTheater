/**
 * The touch-pad EDITOR (SteamOS-style): the game keeps running underneath while you drag controls
 * where your thumbs are, resize them, rebind them, fade them to invisible, add and remove them.
 *
 * Everything is a draft until Save. Save writes either the SYSTEM layout (every game on this console)
 * or a per-GAME override; "Use the system layout" deletes the override. Layouts are per screen shape,
 * so what's being edited is always "this system, held this way" — the header says which.
 *
 * Popups (Select dropdowns, Slider tooltips) must render INSIDE the fullscreen element to be seen; the
 * room page wraps this in a ConfigProvider whose getPopupContainer points at the player.
 */
import { useMemo, useRef, useState, type CSSProperties, type PointerEvent as RPointerEvent } from "react";
import { Button, Input, Segmented, Select, Slider, Switch } from "antd";
import { ControlVisual, controlBox } from "./TouchControls";
import {
  ACTION_LABEL, addableButtons, defaultLayout, newControlId,
  type AxisPush, type Bucket, type ButtonControl, type Control, type Layout, type RoomAction, type StickOutput,
} from "./touchLayout";
import { bitChoicesFor, type BitName, type SystemTouchSpec } from "./touchSystems";
import type { TouchPreset } from "./touchPresets";
import type { LayerSize } from "./touchEngine";
import "./touch.css";

export type SaveScope = "system" | "game";

export interface TouchLayoutEditorProps {
  initial: Layout;
  source: "game" | "system" | "default";
  size: LayerSize;
  bucket: Bucket;
  inputSystem: string;
  systemName: string;
  /** Present when the room has a game the layout could be saved for. */
  gameTitle?: string | null;
  /** The room's built-in presets — "Start from" rebuilds the draft from any of them. */
  presets: TouchPreset[];
  startPresetId: string;
  /** The spec whose buttons "+ Add" offers (the preset on screen: a Genesis 6-button preset offers Mode, …). */
  paletteSpec: SystemTouchSpec;
  onSave: (layout: Layout, scope: SaveScope) => void;
  /** Delete this game's override (falls back to the system layout). */
  onUseSystemLayout?: () => void;
  onCancel: () => void;
}

const ACTION_CHOICES: { value: RoomAction; label: string }[] = [
  { value: "menu", label: "Menu (☰)" }, { value: "quickSave", label: "Quick save" }, { value: "quickLoad", label: "Quick load" },
  { value: "rewind", label: "Rewind (hold)" }, { value: "fastForward", label: "Fast-forward (hold)" }, { value: "reset", label: "Reset" },
];

const AXIS_CHOICES: { value: string; label: string }[] = [
  { value: "", label: "None" },
  { value: "2:-1", label: "Right stick ←" }, { value: "2:1", label: "Right stick →" },
  { value: "3:-1", label: "Right stick ↑" }, { value: "3:1", label: "Right stick ↓" },
  { value: "0:-1", label: "Left stick ←" }, { value: "0:1", label: "Left stick →" },
  { value: "1:-1", label: "Left stick ↑" }, { value: "1:1", label: "Left stick ↓" },
];

const OUTPUT_CHOICES: { value: StickOutput; label: string }[] = [
  { value: "left", label: "Left stick" }, { value: "right", label: "Right stick" }, { value: "dpad", label: "D-pad" },
];

const r4 = (n: number) => Math.round(n * 1000) / 1000;
const clamp = (n: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, n));

type Drag =
  | { kind: "move"; id: string; pid: number; sx: number; sy: number; ox: number; oy: number; moved: boolean }
  | { kind: "resize"; id: string; pid: number; cx: number; cy: number; d0: number; s0: number; w0: number; h0: number };

export default function TouchLayoutEditor({
  initial, source, size, bucket, inputSystem, systemName, gameTitle, presets, startPresetId, paletteSpec,
  onSave, onUseSystemLayout, onCancel,
}: TouchLayoutEditorProps) {
  const [startFrom, setStartFrom] = useState(() => (presets.some((x) => x.id === startPresetId) ? startPresetId : presets[0]?.id));
  const [draft, setDraft] = useState<Layout>(() => structuredClone(initial));
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [panel, setPanel] = useState<"none" | "add" | "settings" | "save">("none");
  const [dirty, setDirty] = useState(false);
  const dragRef = useRef<Drag | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const unit = Math.min(size.w, size.h);

  const selected = draft.controls.find((c) => c.id === selectedId) || null;
  const bitChoices = useMemo(() => bitChoicesFor(inputSystem), [inputSystem]);
  const addable = useMemo(() => addableButtons(inputSystem, draft, paletteSpec), [inputSystem, draft, paletteSpec]);

  function update(next: Layout) { setDraft(next); setDirty(true); }
  function patchControl(id: string, patch: Partial<Control>) {
    update({ ...draft, controls: draft.controls.map((c) => (c.id === id ? ({ ...c, ...patch } as Control) : c)) });
  }
  function add(c: Control) { update({ ...draft, controls: [...draft.controls, c] }); setSelectedId(c.id); setPanel("none"); }
  function remove(id: string) { update({ ...draft, controls: draft.controls.filter((c) => c.id !== id) }); setSelectedId(null); }
  function duplicate(c: Control) {
    add({ ...structuredClone(c), id: newControlId(c.kind), x: clamp(c.x + 0.05, 0, 1), y: clamp(c.y - 0.05, 0, 1) } as Control);
  }

  const local = (e: RPointerEvent) => {
    const r = rootRef.current!.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  };

  const onControlDown = (c: Control) => (e: RPointerEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* synthetic */ }
    const { x, y } = local(e);
    setSelectedId(c.id);
    setPanel("none");
    dragRef.current = { kind: "move", id: c.id, pid: e.pointerId, sx: x, sy: y, ox: c.x, oy: c.y, moved: false };
  };

  const onHandleDown = (c: Control) => (e: RPointerEvent<HTMLDivElement>) => {
    e.preventDefault();
    e.stopPropagation();
    try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* synthetic */ }
    const { x, y } = local(e);
    const cx = c.x * size.w, cy = c.y * size.h;
    dragRef.current = {
      kind: "resize", id: c.id, pid: e.pointerId, cx, cy, d0: Math.max(8, Math.hypot(x - cx, y - cy)),
      s0: c.s, w0: c.kind === "region" ? c.w : 0, h0: c.kind === "region" ? c.h : 0,
    };
  };

  const onPointerMove = (e: RPointerEvent<HTMLDivElement>) => {
    const d = dragRef.current;
    if (!d || d.pid !== e.pointerId) return;
    e.preventDefault();
    const { x, y } = local(e);
    if (d.kind === "move") {
      if (!d.moved && Math.hypot(x - d.sx, y - d.sy) < 4) return; // a tap selects; it doesn't nudge
      d.moved = true;
      patchControl(d.id, { x: r4(clamp(d.ox + (x - d.sx) / size.w, 0, 1)), y: r4(clamp(d.oy + (y - d.sy) / size.h, 0, 1)) });
    } else {
      const c = draft.controls.find((k) => k.id === d.id);
      if (!c) return;
      if (c.kind === "region") {
        patchControl(d.id, { w: r4(clamp((2 * Math.abs(x - d.cx)) / size.w, 0.05, 1)), h: r4(clamp((2 * Math.abs(y - d.cy)) / size.h, 0.05, 1)) } as Partial<Control>);
      } else {
        patchControl(d.id, { s: r4(clamp(d.s0 * (Math.hypot(x - d.cx, y - d.cy) / d.d0), 0.04, 0.6)) });
      }
    }
  };

  const onPointerUp = (e: RPointerEvent<HTMLDivElement>) => {
    if (dragRef.current?.pid === e.pointerId) dragRef.current = null;
  };

  // ── Adding ───────────────────────────────────────────────────────────────────────────────────────
  const centre = { x: 0.5, y: 0.5 };
  const addButton = (bit: BitName, label: string) =>
    add({ id: newControlId("btn"), kind: "button", label, bits: [bit], shape: "circle", mode: "press", ...centre, s: 0.13 });
  const addKinds: { label: string; make: () => Control }[] = [
    { label: "Custom button", make: () => ({ id: newControlId("btn"), kind: "button", label: "A", bits: ["A"], shape: "circle", mode: "press", ...centre, s: 0.13 }) },
    { label: "D-pad", make: () => ({ id: newControlId("dpad"), kind: "dpad", ...centre, s: 0.3 }) },
    { label: "Left stick", make: () => ({ id: newControlId("stick"), kind: "stick", output: "left", floating: false, deadzone: 0.12, ...centre, s: 0.28 }) },
    { label: "Right stick", make: () => ({ id: newControlId("stick"), kind: "stick", output: "right", floating: false, deadzone: 0.12, ...centre, s: 0.28 }) },
    {
      label: "Invisible stick zone (left half)",
      make: () => ({ id: newControlId("zone"), kind: "region", output: "left", deadzone: 0.1, x: 0.25, y: 0.6, w: 0.5, h: 0.8, s: 0.2, opacity: 0 }),
    },
    ...ACTION_CHOICES.map((a) => ({
      label: `Action: ${a.label}`,
      make: (): Control => ({ id: newControlId("act"), kind: "action", action: a.value, ...centre, s: 0.1 }),
    })),
  ];

  // ── Inspector ────────────────────────────────────────────────────────────────────────────────────
  // A render function, not a nested component: a component type declared in here would be a NEW type
  // every render, remounting the panel — and dropping the label input's focus — on every keystroke.
  function renderInspector(c: Control) {
    const opacityPct = Math.round((c.opacity ?? 1) * 100);
    return (
      <div className="tle-panel tle-inspector" style={{ [c.x > 0.5 ? "left" : "right"]: 12 } as CSSProperties}
        onPointerDown={(e) => e.stopPropagation()}>
        <div className="tle-panel__head">
          <b>{c.kind === "button" ? "Button" : c.kind === "dpad" ? "D-pad" : c.kind === "stick" ? "Stick" : c.kind === "region" ? "Stick zone" : "Action"}</b>
          <Button size="small" type="text" onClick={() => setSelectedId(null)} aria-label="Close">✕</Button>
        </div>

        {c.kind !== "dpad" && (
          <label className="tle-row">Label
            <Input size="small" maxLength={16} value={c.label ?? ""} placeholder={c.kind === "action" ? ACTION_LABEL[c.action] : ""}
              onChange={(e) => patchControl(c.id, { label: e.target.value || undefined })} />
          </label>
        )}

        {c.kind === "button" && (
          <>
            <label className="tle-row">Presses
              <Select size="small" mode="multiple" maxCount={4} value={c.bits}
                options={bitChoices.map((b) => ({ value: b.bit, label: b.label === b.bit ? b.bit : `${b.label} (${b.bit})` }))}
                onChange={(bits: BitName[]) => patchControl(c.id, { bits } as Partial<ButtonControl>)} />
            </label>
            <label className="tle-row">Pushes a stick
              <Select size="small" value={c.axis ? `${c.axis.i}:${c.axis.v}` : ""} options={AXIS_CHOICES}
                onChange={(v: string) => {
                  if (!v) { const rest: ButtonControl = { ...c }; delete rest.axis; update({ ...draft, controls: draft.controls.map((k) => (k.id === c.id ? rest : k)) }); return; }
                  const [i, sv] = v.split(":").map(Number);
                  patchControl(c.id, { axis: { i, v: sv } as AxisPush } as Partial<ButtonControl>);
                }} />
            </label>
            <div className="tle-row">Mode
              <Segmented size="small" value={c.mode} options={[{ value: "press", label: "Press" }, { value: "toggle", label: "Toggle" }, { value: "turbo", label: "Turbo" }]}
                onChange={(mode) => patchControl(c.id, { mode } as Partial<ButtonControl>)} />
            </div>
            <div className="tle-row">Shape
              <Segmented size="small" value={c.shape} options={[{ value: "circle", label: "●" }, { value: "pill", label: "▬" }, { value: "square", label: "■" }]}
                onChange={(shape) => patchControl(c.id, { shape } as Partial<ButtonControl>)} />
            </div>
          </>
        )}

        {(c.kind === "stick" || c.kind === "region") && (
          <>
            <label className="tle-row">Drives
              <Select size="small" value={c.output} options={OUTPUT_CHOICES} onChange={(output: StickOutput) => patchControl(c.id, { output })} />
            </label>
            {c.kind === "stick" && (
              <div className="tle-row">Centre where my thumb lands
                <Switch size="small" checked={c.floating} onChange={(floating) => patchControl(c.id, { floating })} />
              </div>
            )}
            <div className="tle-row tle-row--slider">Deadzone {Math.round(c.deadzone * 100)}%
              <Slider min={0} max={50} value={Math.round(c.deadzone * 100)} onChange={(v: number) => patchControl(c.id, { deadzone: v / 100 })} />
            </div>
          </>
        )}

        {c.kind === "action" && (
          <label className="tle-row">Does
            <Select size="small" value={c.action} options={ACTION_CHOICES} onChange={(action: RoomAction) => patchControl(c.id, { action })} />
          </label>
        )}

        {c.kind !== "region" && (
          <div className="tle-row tle-row--slider">Size
            <Slider min={4} max={60} value={Math.round(c.s * 100)} onChange={(v: number) => patchControl(c.id, { s: v / 100 })} />
          </div>
        )}
        <div className="tle-row tle-row--slider">Opacity {opacityPct}%{opacityPct === 0 ? " — invisible, still works" : ""}
          <Slider min={0} max={100} step={5} value={opacityPct} onChange={(v: number) => patchControl(c.id, { opacity: v / 100 })} />
        </div>

        <div className="tle-actions">
          <Button size="small" onClick={() => duplicate(c)}>Duplicate</Button>
          <Button size="small" danger onClick={() => remove(c.id)}>Remove</Button>
        </div>
      </div>
    );
  }

  const scopeText = source === "game" && gameTitle ? `${gameTitle} only` : `All ${systemName} games`;

  return (
    <div ref={rootRef} className="tle" data-touch-control=""
      onPointerMove={onPointerMove} onPointerUp={onPointerUp} onPointerCancel={onPointerUp}
      onPointerDown={(e) => { if (e.target === e.currentTarget) { setSelectedId(null); setPanel("none"); } }}>
      {draft.controls.map((c) => {
        const sel = c.id === selectedId;
        return (
          <div key={c.id} className={`tc tc--${c.kind} tle-ctl${sel ? " is-selected" : ""}`}
            style={{ ...controlBox(c, size), "--tc-opacity": Math.max(0.45, c.opacity ?? 1) } as CSSProperties}
            onPointerDown={onControlDown(c)}>
            <ControlVisual c={c} size={size} />
            {(c.opacity ?? 1) === 0 && <span className="tle-ghost">invisible</span>}
            {sel && <div className="tle-handle" onPointerDown={onHandleDown(c)} aria-label="Resize" />}
          </div>
        );
      })}

      <div className="tle-bar" onPointerDown={(e) => e.stopPropagation()}>
        <span className="tle-bar__title">Touch controls · {scopeText} · {bucket}</span>
        <Button size="small" onClick={() => setPanel(panel === "add" ? "none" : "add")}>+ Add</Button>
        <Button size="small" onClick={() => setPanel(panel === "settings" ? "none" : "settings")}>⚙ Layout</Button>
        <Button size="small" onClick={onCancel}>Cancel</Button>
        <Button size="small" type="primary" onClick={() => (gameTitle ? setPanel(panel === "save" ? "none" : "save") : onSave(draft, "system"))}>
          Save{dirty ? " •" : ""}
        </Button>
      </div>

      {panel === "save" && (
        <div className="tle-panel tle-panel--center" onPointerDown={(e) => e.stopPropagation()}>
          <Button block type={source !== "game" ? "primary" : "default"} onClick={() => onSave(draft, "system")}>Save for all {systemName} games</Button>
          <Button block type={source === "game" ? "primary" : "default"} onClick={() => onSave(draft, "game")}>Save for {gameTitle} only</Button>
        </div>
      )}

      {panel === "add" && (
        <div className="tle-panel tle-panel--center tle-add" onPointerDown={(e) => e.stopPropagation()}>
          {addable.length > 0 && <div className="tle-sub">{systemName} buttons</div>}
          <div className="tle-chips">
            {addable.map((b) => <Button key={b.bit} size="small" onClick={() => addButton(b.bit, b.label)}>{b.label}</Button>)}
          </div>
          <div className="tle-sub">Controls</div>
          <div className="tle-chips">
            {addKinds.map((k) => <Button key={k.label} size="small" onClick={() => add(k.make())}>{k.label}</Button>)}
          </div>
        </div>
      )}

      {panel === "settings" && (
        <div className="tle-panel tle-panel--center" onPointerDown={(e) => e.stopPropagation()}>
          <div className="tle-row tle-row--slider">Overall opacity {Math.round(draft.opacity * 100)}%
            <Slider min={0} max={100} step={5} value={Math.round(draft.opacity * 100)} onChange={(v: number) => update({ ...draft, opacity: v / 100 })} />
          </div>
          <div className="tle-row">Fade out when I&apos;m not touching
            <Switch size="small" checked={!!draft.idleFade} onChange={(on) => update({ ...draft, idleFade: on ? { afterMs: 4000, to: 0.15 } : null })} />
          </div>
          {draft.idleFade && (
            <>
              <div className="tle-row tle-row--slider">…after {Math.round(draft.idleFade.afterMs / 1000)} s
                <Slider min={1} max={20} value={Math.round(draft.idleFade.afterMs / 1000)} onChange={(v: number) => update({ ...draft, idleFade: { ...draft.idleFade!, afterMs: v * 1000 } })} />
              </div>
              <div className="tle-row tle-row--slider">…down to {Math.round(draft.idleFade.to * 100)}%
                <Slider min={0} max={100} step={5} value={Math.round(draft.idleFade.to * 100)} onChange={(v: number) => update({ ...draft, idleFade: { ...draft.idleFade!, to: v / 100 } })} />
              </div>
            </>
          )}
          <div className="tle-row">Flash a control when pressed
            <Switch size="small" checked={draft.flashOnPress} onChange={(flashOnPress) => update({ ...draft, flashOnPress })} />
          </div>
          <div className="tle-row">Slide between buttons
            <Switch size="small" checked={draft.slide} onChange={(slide) => update({ ...draft, slide })} />
          </div>
          <div className="tle-row">Vibrate on press (Android)
            <Switch size="small" checked={draft.haptics} onChange={(haptics) => update({ ...draft, haptics })} />
          </div>
          <label className="tle-row">Start over from
            <Select size="small" value={startFrom} onChange={(v: string) => setStartFrom(v)}
              options={presets.map((x) => ({ value: x.id, label: x.name }))} />
          </label>
          <div className="tle-actions">
            <Button size="small" onClick={() => {
              const preset = presets.find((x) => x.id === startFrom);
              update(defaultLayout(inputSystem, bucket, size.w / Math.max(1, size.h), preset?.spec));
              setSelectedId(null);
            }}>
              Start over
            </Button>
            {source === "game" && onUseSystemLayout && (
              <Button size="small" onClick={onUseSystemLayout}>Use the {systemName} layout</Button>
            )}
          </div>
        </div>
      )}

      {selected && panel === "none" && renderInspector(selected)}
      {!selected && panel === "none" && (
        <div className="tle-hint">Drag to move · tap to edit · drag the corner dot to resize · {unit < 360 ? "rotate for more room" : "tap empty space to deselect"}</div>
      )}
    </div>
  );
}
