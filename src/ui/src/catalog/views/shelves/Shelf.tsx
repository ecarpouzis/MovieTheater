import { memo, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import type { CardGroup, CardItem } from "../../types";
import { coverSrc } from "../../cards/Card";
import {
  PLANK, SHELF_PAD_LEFT, VIRT_THRESHOLD, relaxedGap, shelfBasis, shelfBookDims, shelfGrowWeight, spineFor, spinePrefix, virtualWindow,
} from "./geometry";

/**
 * One shelf: a group's books packed on a plank, later books painted over earlier ones so a spine
 * slice of each shows; hover (or the centre, on touch) reveals a whole cover. The shelf reserves
 * its FULL run's width up front with a trailing spacer (on `renderTotal`, never `totalItems`), so
 * its scroll width never grows as pages stream in; past VIRT_THRESHOLD loaded books only the
 * slice near the viewport is mounted, exact-width spacers standing in for the rest.
 *
 * Three nodes per book, deliberately: a band mount commits 20 shelves × ~36 books. The per-item
 * hue is the cover's BACKGROUND (shows while loading, unloaded, or failed); covers load via
 * `data-src` under the engine's two-axis windowing — src is set by the load pass and cleared when
 * the book leaves the window, which is what bounds decoded-bitmap memory on old phones.
 */
export function ShelfBook({ item, shelfH, onOpen }: { item: CardItem; shelfH: number; onOpen: (i: CardItem) => void }) {
  const { w: cw, h: ch } = shelfBookDims(shelfH, item.aspect);
  const spine = spineFor(cw);
  const hue = item.hue ?? 220;
  return (
    <div className="bk" data-cw={cw} onClick={() => onOpen(item)} title={item.title}
      role="button" tabIndex={0} aria-label={item.title}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(item); } }}
      style={{ "--cw": `${cw}px`, "--spine": `${spine}px`, "--sh": `${ch}px` } as CSSProperties}>
      <div className="bk-3d" style={{ background: `oklch(0.52 0.18 ${hue})` }}>
        <img data-src={coverSrc(item, cw)} alt={item.title} decoding="async"
          style={{ position: "absolute", inset: 0, width: "100%", height: "100%", objectFit: "cover" }} />
      </div>
    </div>
  );
}

export interface ShelfProps {
  group: CardGroup;
  /** Items loaded so far for this group (the band's page + any "more" pages). */
  items: CardItem[];
  shelfH: number;
  noun: string;
  onOpen: (i: CardItem) => void;
  onOpenGroup: ((g: CardGroup) => void) | null;
}

function ShelfInner({ group, items, shelfH, noun, onOpen, onOpenGroup }: ShelfProps) {
  const n = items.length;
  const total = Math.max(group.renderTotal ?? group.totalItems, n);
  const unloaded = Math.max(0, total - n);
  const booksRef = useRef<HTMLDivElement>(null);
  const aspects = useMemo(() => items.map((i) => i.aspect), [items]);
  const prefix = useMemo(() => spinePrefix(aspects, shelfH), [aspects, shelfH]);
  const spineSum = prefix[n];
  const avgSpine = n > 0 ? Math.round(spineSum / n) : spineFor(Math.round(shelfH * 0.66));

  // Per-slot spine estimate FROZEN from the first page, so the reserved tail never moves as pages stream in.
  const frozenRef = useRef<{ key: string; h: number; spine: number } | null>(null);
  if (!frozenRef.current || frozenRef.current.key !== group.key || frozenRef.current.h !== shelfH) frozenRef.current = n > 0 ? { key: group.key, h: shelfH, spine: avgSpine } : null;
  const spacerAvgSpine = frozenRef.current?.spine ?? avgSpine;
  const spacerSpineWidth = Math.max(0, total * spacerAvgSpine - spineSum);

  // Adaptive gap: open the spacing to absorb half the row slack on a fully-loaded shelf (capped),
  // and publish the loaded extent the load-more check reads (correct under virtualization).
  const gapRef = useRef<number | null>(null);
  useLayoutEffect(() => {
    const sb = booksRef.current;
    if (!sb) return undefined;
    const apply = () => {
      const lastCw = n > 0 ? shelfBookDims(shelfH, items[n - 1].aspect).w : 0;
      const g = relaxedGap(shelfH, n, unloaded, sb.clientWidth, spineSum, lastCw);
      gapRef.current = g;
      sb.style.setProperty("--book-gap", `${g}px`);
      sb.dataset.loaded = String(n);
      sb.dataset.loadedRight = String(SHELF_PAD_LEFT + spineSum + n * g);
    };
    apply();
    if (typeof ResizeObserver === "undefined") return undefined;
    const ro = new ResizeObserver(apply);
    ro.observe(sb);
    return () => ro.disconnect();
  }, [n, shelfH, spineSum, items, unloaded]);

  // In-shelf DOM virtualization past VIRT_THRESHOLD books.
  const [win, setWin] = useState<{ start: number; end: number } | null>(null);
  useLayoutEffect(() => {
    const sb = booksRef.current;
    if (!sb) return undefined;
    if (n <= VIRT_THRESHOLD) { setWin((p) => (p === null ? p : null)); return undefined; }
    let raf = 0;
    const compute = () => {
      raf = 0;
      const gap = gapRef.current ?? Math.max(1, Math.round(shelfH * 0.11));
      const next = virtualWindow(prefix, n, gap, sb.scrollLeft, sb.clientWidth);
      setWin((prev) => (prev && Math.abs(prev.start - next.start) <= 12 && Math.abs(prev.end - next.end) <= 12 ? prev : next));
    };
    const onScroll = () => { if (!raf) raf = requestAnimationFrame(compute); };
    compute();
    sb.addEventListener("scroll", onScroll, { passive: true });
    const ro = typeof ResizeObserver !== "undefined" ? new ResizeObserver(onScroll) : null;
    ro?.observe(sb);
    return () => { sb.removeEventListener("scroll", onScroll); ro?.disconnect(); if (raf) cancelAnimationFrame(raf); };
  }, [n, prefix, shelfH]);

  const start = win && n > VIRT_THRESHOLD ? Math.min(win.start, n) : 0;
  const end = win && n > VIRT_THRESHOLD ? Math.max(start, Math.min(win.end, n)) : n;
  const tailSpines = spacerSpineWidth + (prefix[n] - prefix[end]);
  const tailCount = unloaded + (n - end);
  const basis = shelfBasis(n);
  const grow = shelfGrowWeight(total, shelfH);

  return (
    <section className="shelf" data-label={group.label} data-key={group.key} style={{ flexBasis: `${basis}px`, flexGrow: grow }}>
      <header className="shelf-head">
        <h3 className={`shelf-name${onOpenGroup ? " bx-clickable" : ""}`} onClick={onOpenGroup ? () => onOpenGroup(group) : undefined}>{group.label}</h3>
        <span className="shelf-count" title={`${total.toLocaleString()} ${noun}s`}>{total.toLocaleString()}</span>
      </header>
      <div className="shelf-books" ref={booksRef} data-group-key={group.key} data-total={total}
        style={{ "--shelf-h": `${shelfH}px`, "--shelf-row": `${shelfH + PLANK}px` } as CSSProperties}>
        {start > 0 && <div className="bk-spacer bk-spacer-lead" aria-hidden="true" style={{ flex: `0 0 calc(${prefix[start]}px + ${start} * var(--book-gap, 0px))` }} />}
        {items.slice(start, end).map((item) => <ShelfBook key={item.key} item={item} shelfH={shelfH} onOpen={onOpen} />)}
        {tailCount > 0 && <div className="bk-spacer" aria-hidden="true" style={{ flex: `0 0 calc(${tailSpines}px + ${tailCount} * var(--book-gap, 0px))` }} />}
      </div>
    </section>
  );
}

const Shelf = memo(ShelfInner);
export default Shelf;
