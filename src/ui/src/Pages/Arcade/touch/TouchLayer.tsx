/**
 * Mounts the touch pad over the player: measures the player surface, picks the layout for this
 * system/game/screen shape (resolveLayout), and shows either the play-mode controls or the editor.
 * Lives INSIDE the player element so it is drawn in fullscreen.
 */
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { message } from "antd";
import TouchControls from "./TouchControls";
import TouchLayoutEditor, { type SaveScope } from "./TouchLayoutEditor";
import { bucketFor, gameKeyFor, resolveLayout, systemKey, type Layout, type LayoutStore, type RoomAction } from "./touchLayout";
import type { LayerSize } from "./touchEngine";

export interface TouchLayerProps {
  shown: boolean;
  editing: boolean;
  onEditDone: () => void;
  system: string;
  inputSystem: string;
  gameKey?: string | null;
  systemName: string;
  gameTitle?: string | null;
  store: LayoutStore;
  save: (key: string, layout: Layout | null) => Promise<boolean>;
  onFrame: (mask: number, axes: [number, number, number, number]) => void;
  onAction: (action: RoomAction, engaged: boolean) => void;
  actionAllowed: (action: RoomAction) => boolean;
}

export default function TouchLayer(p: TouchLayerProps) {
  const ref = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState<LayerSize>({ w: 0, h: 0 });

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return undefined;
    const measure = () => {
      const r = el.getBoundingClientRect();
      setSize((s) => (Math.round(r.width) === s.w && Math.round(r.height) === s.h ? s : { w: Math.round(r.width), h: Math.round(r.height) }));
    };
    measure();
    if (typeof ResizeObserver === "undefined") {
      window.addEventListener("resize", measure);
      return () => window.removeEventListener("resize", measure);
    }
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const ready = size.w > 0 && size.h > 0;
  const bucket = bucketFor(size.w, size.h);
  // Memoised, and that matters: TouchControls drops every finger when its layout object changes, and the
  // room page re-renders constantly (status, roster polls). A fresh default layout per render would
  // release a held button every time the roster refreshed.
  const aspect = ready ? size.w / size.h : 16 / 9;
  const { layout, source } = useMemo(
    () => resolveLayout(p.store, { system: p.system, inputSystem: p.inputSystem, gameKey: p.gameKey, bucket, aspect }),
    [p.store, p.system, p.inputSystem, p.gameKey, bucket, aspect],
  );

  // Escape closes the editor (desktop testing; a phone uses Cancel).
  const { editing, onEditDone } = p;
  useEffect(() => {
    if (!editing) return undefined;
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onEditDone(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [editing, onEditDone]);

  async function onSave(next: Layout, scope: SaveScope) {
    const key = scope === "game" && p.gameKey ? gameKeyFor(p.system, p.gameKey, bucket) : systemKey(p.inputSystem, bucket);
    p.onEditDone();
    const ok = await p.save(key, next);
    if (ok) message.success(scope === "game" ? `Saved for ${p.gameTitle || "this game"}` : `Saved for all ${p.systemName} games`);
    else message.warning("Saved on this device — couldn't reach your account; it will sync the next time you open a room.");
  }

  async function useSystemLayout() {
    if (!p.gameKey) return;
    p.onEditDone();
    await p.save(gameKeyFor(p.system, p.gameKey, bucket), null);
    message.info(`Back to your ${p.systemName} layout`);
  }

  return (
    <div ref={ref} className="touch-host">
      {ready && p.editing && (
        <TouchLayoutEditor
          key={`${bucket}|${source}`}
          initial={layout}
          source={source}
          size={size}
          bucket={bucket}
          inputSystem={p.inputSystem}
          systemName={p.systemName}
          gameTitle={p.gameKey ? (p.gameTitle || p.gameKey) : null}
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
