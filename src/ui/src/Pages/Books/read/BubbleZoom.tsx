/**
 * Bubble Zoom — an in-place magnifier for speech balloons. Press-and-hold a balloon and the loupe
 * paints a high-res crop of just that balloon, enlarged and anchored over the spot it occupies on
 * the page. The card takes pointer events itself: a tap dismisses it, a drag parks it clear of the
 * artwork — every balloon opened afterwards then appears at that spot. Split out of the standalone's
 * ReaderView unchanged in behaviour.
 */
import { useLayoutEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import type { TextRegion } from "../booksApi";

export interface PageSlot {
  pageIndex: number;
  screenX: number; screenY: number;
  screenW: number; screenH: number;
  imgW: number; imgH: number;
}

/** Where the loupe was parked, as a fraction of the viewport so the spot holds up across a rotate/resize. */
export interface BubbleAnchor { cx: number; cy: number }

const BZ_MARGIN = 12;
const BZ_PAD = 6;
const BZ_DRAG_SLOP = 6;

function clampLoupe(left: number, top: number, w: number, h: number) {
  return {
    left: Math.max(BZ_MARGIN, Math.min(left, window.innerWidth - BZ_MARGIN - w)),
    top: Math.max(BZ_MARGIN, Math.min(top, window.innerHeight - BZ_MARGIN - h)),
  };
}

export interface BubbleZoomLoupeProps {
  img: HTMLImageElement | undefined;
  region: TextRegion | undefined;
  slot: PageSlot | undefined;
  anchor: BubbleAnchor | null;
  onAnchorChange: (anchor: BubbleAnchor) => void;
  onDismiss: () => void;
}

export function BubbleZoomLoupe({ img, region, slot, anchor, onAnchorChange, onDismiss }: BubbleZoomLoupeProps) {
  const ref = useRef<HTMLCanvasElement>(null);
  const [nat, setNat] = useState<{ w: number; h: number; cx: number; cy: number } | null>(null);
  const [box, setBox] = useState<{ left: number; top: number } | null>(null);
  const [dragBox, setDragBox] = useState<{ left: number; top: number } | null>(null);
  const dragRef = useRef<{ id: number; startX: number; startY: number; baseLeft: number; baseTop: number; moved: boolean } | null>(null);

  useLayoutEffect(() => {
    const cv = ref.current;
    if (!cv || !img || !region || !slot) { setNat(null); return; }
    const padX = Math.min(0.2, Math.max(0.012, region.width * 0.12));
    const padY = Math.min(0.2, Math.max(0.012, region.height * 0.12));
    const rx = Math.max(0, region.x - padX), ry = Math.max(0, region.y - padY);
    const rw = Math.min(1 - rx, region.width + padX * 2), rh = Math.min(1 - ry, region.height + padY * 2);
    const sx = rx * img.width, sy = ry * img.height, sw = rw * img.width, sh = rh * img.height;
    const aspect = sw / Math.max(1, sh);
    const regionW = rw * slot.screenW;
    const regionCx = slot.screenX + (rx + rw / 2) * slot.screenW;
    const regionCy = slot.screenY + (ry + rh / 2) * slot.screenH;
    const maxW = Math.min(window.innerWidth - BZ_MARGIN * 2, 620) - BZ_PAD * 2;
    const maxH = window.innerHeight - BZ_MARGIN * 2 - BZ_PAD * 2;
    let w = Math.max(regionW * 3.0, 260);
    let h = w / aspect;
    const fit = Math.min(1, maxW / w, maxH / h);
    w *= fit; h *= fit;
    const dpr = window.devicePixelRatio || 1;
    cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
    cv.style.width = `${w}px`; cv.style.height = `${h}px`;
    const ctx = cv.getContext("2d");
    if (!ctx) return;
    ctx.scale(dpr, dpr);
    ctx.imageSmoothingEnabled = true; ctx.imageSmoothingQuality = "high";
    ctx.fillStyle = "#fff"; ctx.fillRect(0, 0, w, h);
    ctx.drawImage(img, sx, sy, sw, sh, 0, 0, w, h);
    setNat({ w, h, cx: regionCx, cy: regionCy });
  }, [img, region, slot]);

  useLayoutEffect(() => {
    if (!nat) { setBox(null); return; }
    const cw = nat.w + BZ_PAD * 2, ch = nat.h + BZ_PAD * 2;
    const cx = anchor ? anchor.cx * window.innerWidth : nat.cx;
    const cy = anchor ? anchor.cy * window.innerHeight : nat.cy;
    setBox(clampLoupe(cx - cw / 2, cy - ch / 2, cw, ch));
  }, [nat, anchor]);

  const beginDrag = (e: ReactPointerEvent<HTMLDivElement>) => {
    const from = dragBox ?? box;
    if (!from) return;
    e.currentTarget.setPointerCapture(e.pointerId);
    dragRef.current = { id: e.pointerId, startX: e.clientX, startY: e.clientY, baseLeft: from.left, baseTop: from.top, moved: false };
  };
  const dragTo = (e: ReactPointerEvent<HTMLDivElement>) => {
    const d = dragRef.current;
    if (!d || d.id !== e.pointerId || !nat) return null;
    const dx = e.clientX - d.startX, dy = e.clientY - d.startY;
    if (!d.moved && Math.hypot(dx, dy) < BZ_DRAG_SLOP) return null;
    d.moved = true;
    return clampLoupe(d.baseLeft + dx, d.baseTop + dy, nat.w + BZ_PAD * 2, nat.h + BZ_PAD * 2);
  };
  const onDragMove = (e: ReactPointerEvent<HTMLDivElement>) => { const next = dragTo(e); if (next) setDragBox(next); };
  const endDrag = (e: ReactPointerEvent<HTMLDivElement>) => {
    const d = dragRef.current;
    if (!d || d.id !== e.pointerId) return;
    const next = dragTo(e);
    dragRef.current = null;
    setDragBox(null);
    if (!d.moved || !next || !nat) { onDismiss(); return; }
    onAnchorChange({ cx: (next.left + (nat.w + BZ_PAD * 2) / 2) / window.innerWidth, cy: (next.top + (nat.h + BZ_PAD * 2) / 2) / window.innerHeight });
  };
  const cancelDrag = () => { dragRef.current = null; setDragBox(null); };
  const pos = dragBox ?? box;

  return (
    <div
      className="rdr-bz-card"
      data-reader-control
      onPointerDown={beginDrag}
      onPointerMove={onDragMove}
      onPointerUp={endDrag}
      onPointerCancel={cancelDrag}
      style={{ left: pos?.left ?? 0, top: pos?.top ?? 0, padding: BZ_PAD, visibility: pos ? "visible" : "hidden", cursor: dragBox ? "grabbing" : "grab" }}
    >
      <canvas ref={ref} className="rdr-bz-canvas" />
    </div>
  );
}

/** Debug overlay around the loupe: the tight text box, the tap target, the tap point, a caption. */
export function BubbleDebug({ region, slot, index, total, tapX, tapY }: { region: TextRegion; slot: PageSlot; index: number; total: number; tapX?: number; tapY?: number }) {
  const tb = { left: slot.screenX + region.x * slot.screenW, top: slot.screenY + region.y * slot.screenH, width: region.width * slot.screenW, height: region.height * slot.screenH };
  const hb = { left: slot.screenX + region.hitX * slot.screenW, top: slot.screenY + region.hitY * slot.screenH, width: region.hitWidth * slot.screenW, height: region.hitHeight * slot.screenH };
  const label = `#${index + 1}/${total} · ${region.pol === 2 ? "light" : "dark"} · ${region.glyphs}g · ${Math.round(region.width * 100)}×${Math.round(region.height * 100)}%`;
  return (
    <div className="rdr-bz-debug">
      <div className="rdr-bz-debug-hit" style={hb} />
      <div className="rdr-bz-debug-text" style={tb} />
      {tapX != null && tapY != null && <div className="rdr-bz-debug-tap" style={{ left: tapX - 4, top: tapY - 4 }} />}
      <div className="rdr-bz-debug-cap" style={{ left: Math.max(4, tb.left), top: Math.max(2, tb.top - 16) }}>{label}</div>
    </div>
  );
}
