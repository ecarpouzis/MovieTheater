/**
 * Mounts the touch pad over the player: measures the player surface, picks the layout for this
 * system/game/screen shape (resolveLayout — the player's pick, an edited layout, or the suggested
 * preset), and shows either the play-mode controls or the editor. Lives INSIDE the player element so it
 * is drawn in fullscreen.
 */
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from "react";
import { message } from "antd";
import TouchControls from "./TouchControls";
import TouchLayoutEditor, { type SaveScope } from "./TouchLayoutEditor";
import {
  CUSTOM_PICK, bucketFor, gameKeyFor, gamePickKey, resolveLayout, systemKey, systemPickKey,
  type Layout, type LayoutStore, type PictureRect, type ResolvedLayout, type RoomAction,
} from "./touchLayout";
import { presetsFor, suggestedPreset } from "./touchPresets";
import type { LayerSize } from "./touchEngine";

/** What's on screen, reported up so the room's ☰ menu can show the active preset. */
export interface TouchResolved {
  presetId: string | null;
  source: ResolvedLayout["source"];
  pickScope: ResolvedLayout["pickScope"];
  /** An edited layout exists for this game / this system in the current screen shape. */
  hasGameLayout: boolean;
  hasSystemLayout: boolean;
}

export interface TouchLayerProps {
  shown: boolean;
  editing: boolean;
  onEditDone: () => void;
  system: string;
  inputSystem: string;
  gameKey?: string | null;
  systemName: string;
  gameTitle?: string | null;
  /** ArcadeGame.Controls (descriptor.controls) — drives the arcade presets and which one is suggested. */
  controls?: string | null;
  /** descriptor.coreKey — the arcade button map follows the core. */
  coreKey?: string | null;
  store: LayoutStore;
  save: (key: string, layout: Layout | null, picks?: Record<string, string | null>) => Promise<boolean>;
  onResolved?: (r: TouchResolved) => void;
  /** The game picture's box — generated layouts keep the minor buttons (Coin/Start, shoulders) off it. */
  pictureRef?: RefObject<HTMLElement | null>;
  onFrame: (mask: number, axes: [number, number, number, number]) => void;
  onAction: (action: RoomAction, engaged: boolean) => void;
  actionAllowed: (action: RoomAction) => boolean;
}

export default function TouchLayer(p: TouchLayerProps) {
  const ref = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState<LayerSize>({ w: 0, h: 0 });
  const [picture, setPicture] = useState<PictureRect | null>(null);
  const { pictureRef } = p;

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return undefined;
    const measure = () => {
      const r = el.getBoundingClientRect();
      setSize((s) => (Math.round(r.width) === s.w && Math.round(r.height) === s.h ? s : { w: Math.round(r.width), h: Math.round(r.height) }));
      // As layer fractions, rounded so a sub-pixel wobble can't regenerate the layout (which drops every finger).
      const pic = pictureRef?.current?.getBoundingClientRect();
      const f = (n: number) => Math.round(n * 100) / 100;
      const next = pic && r.width > 0 && r.height > 0 && pic.width > 0 && pic.height > 0
        ? { x0: f((pic.left - r.left) / r.width), y0: f((pic.top - r.top) / r.height), x1: f((pic.right - r.left) / r.width), y1: f((pic.bottom - r.top) / r.height) }
        : null;
      setPicture((cur) => (cur && next && cur.x0 === next.x0 && cur.y0 === next.y0 && cur.x1 === next.x1 && cur.y1 === next.y1) || (!cur && !next) ? cur : next);
    };
    measure();
    if (typeof ResizeObserver === "undefined") {
      window.addEventListener("resize", measure);
      return () => window.removeEventListener("resize", measure);
    }
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    if (pictureRef?.current) ro.observe(pictureRef.current);
    return () => ro.disconnect();
    // `shown`: the room pins the picture to the top when the pad appears on an upright phone — it moves
    // without resizing, which no ResizeObserver reports.
  }, [pictureRef, p.shown]);

  const ready = size.w > 0 && size.h > 0;
  const bucket = bucketFor(size.w, size.h);
  const presets = useMemo(() => presetsFor(p.inputSystem, { controls: p.controls, coreKey: p.coreKey }), [p.inputSystem, p.controls, p.coreKey]);
  const suggested = useMemo(() => suggestedPreset(p.inputSystem, { controls: p.controls, coreKey: p.coreKey }), [p.inputSystem, p.controls, p.coreKey]);
  // Memoised, and that matters: TouchControls drops every finger when its layout object changes, and the
  // room page re-renders constantly (status, roster polls). A fresh default layout per render would
  // release a held button every time the roster refreshed.
  const aspect = ready ? size.w / size.h : 16 / 9;
  const resolved = useMemo(
    () => resolveLayout(p.store, { system: p.system, inputSystem: p.inputSystem, gameKey: p.gameKey, bucket, aspect, presets, suggested, picture }),
    [p.store, p.system, p.inputSystem, p.gameKey, bucket, aspect, presets, suggested, picture],
  );
  const { layout, source } = resolved;
  const activePreset = presets.find((x) => x.id === resolved.presetId) || null;

  const hasGameLayout = !!(p.gameKey && p.store.layouts[gameKeyFor(p.system, p.gameKey, bucket)]);
  const hasSystemLayout = !!p.store.layouts[systemKey(p.inputSystem, bucket)];
  const { onResolved } = p;
  useEffect(() => {
    onResolved?.({ presetId: resolved.presetId, source: resolved.source, pickScope: resolved.pickScope, hasGameLayout, hasSystemLayout });
  }, [onResolved, resolved.presetId, resolved.source, resolved.pickScope, hasGameLayout, hasSystemLayout]);

  // Escape closes the editor (desktop testing; a phone uses Cancel).
  const { editing, onEditDone } = p;
  useEffect(() => {
    if (!editing) return undefined;
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onEditDone(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [editing, onEditDone]);

  const gamePick = p.gameKey ? gamePickKey(p.system, p.gameKey) : null;

  async function onSave(next: Layout, scope: SaveScope) {
    const forGame = scope === "game" && !!p.gameKey;
    const key = forGame ? gameKeyFor(p.system, p.gameKey!, bucket) : systemKey(p.inputSystem, bucket);
    // Mark the saved scope "custom" so a preset picked earlier can't hide the edit. Saving for the whole
    // system also drops this game's own pick — it was showing that preset, and the player just chose the
    // system-wide edit instead.
    const picks: Record<string, string | null> = forGame
      ? { [gamePick!]: CUSTOM_PICK }
      : { [systemPickKey(p.inputSystem)]: CUSTOM_PICK, ...(gamePick ? { [gamePick]: null } : null) };
    p.onEditDone();
    const ok = await p.save(key, next, picks);
    if (ok) message.success(forGame ? `Saved for ${p.gameTitle || "this game"}` : `Saved for all ${p.systemName} games`);
    else message.warning("Saved on this device — couldn't reach your account; it will sync the next time you open a room.");
  }

  async function useSystemLayout() {
    if (!p.gameKey) return;
    p.onEditDone();
    await p.save(gameKeyFor(p.system, p.gameKey, bucket), null, gamePick ? { [gamePick]: null } : {});
    message.info(`Back to your ${p.systemName} layout`);
  }

  return (
    <div ref={ref} className="touch-host">
      {ready && p.editing && (
        <TouchLayoutEditor
          key={`${bucket}|${source}|${resolved.presetId ?? ""}`}
          initial={layout}
          source={source === "preset" ? "default" : source}
          size={size}
          bucket={bucket}
          inputSystem={p.inputSystem}
          systemName={p.systemName}
          gameTitle={p.gameKey ? (p.gameTitle || p.gameKey) : null}
          presets={presets}
          picture={picture}
          startPresetId={resolved.presetId ?? suggested}
          paletteSpec={(activePreset || presets.find((x) => x.id === suggested) || presets[0]).spec}
          onSave={onSave}
          onUseSystemLayout={source === "game" ? useSystemLayout : undefined}
          onCancel={p.onEditDone}
        />
      )}
      {ready && !p.editing && p.shown && (
        <TouchControls layout={layout} size={size} onFrame={p.onFrame} onAction={p.onAction} actionAllowed={p.actionAllowed} />
      )}
    </div>
  );
}
