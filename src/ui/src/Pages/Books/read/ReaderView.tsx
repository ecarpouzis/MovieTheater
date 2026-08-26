/**
 * The canvas reader — the standalone's `ReaderView`, every constant and law kept: ref-authoritative
 * zoom/scroll redrawn through one rAF, a gentle sequential prefetch, full-resolution pages under
 * magnification, rotate/mirror, two-page spreads, the printed-page offset, Bubble Zoom (touch,
 * press-and-hold), the webtoon strip (per-book choice, name signal, aspect probe), the Command Deck
 * menu, and the reading-order hand-off. What changed: page bytes come from the media plane
 * (`pageSrc`), the position rides `useReadingPosition`, the library pills are the item's marks, and
 * the debug panel no longer probes the standalone's `/api/…` routes.
 */
import { useQuery } from "@tanstack/react-query";
import { useCallback, useEffect, useRef, useState } from "react";
import { fetchNext, fetchPrev, fetchTextRegions, type ItemDetail, type TextRegion } from "../booksApi";
import { reportMediaFailure } from "../booksMedia";
import type { KidStyle } from "../KidsHome";
import LibraryPills from "../LibraryPills";
import { BubbleDebug, BubbleZoomLoupe, type BubbleAnchor, type PageSlot } from "./BubbleZoom";
import { MenuFooter, MenuHead, MenuShell, RM, RmIcon, Scrubber, useMenuTier } from "./ReaderMenu";
import {
  isRtlSplitReading, isSinglePageSpread, isSplitModeEnabled, loadPageOffset, loadReaderPrefs, loadWebtoonMode,
  savePageOffset, saveReaderPrefs, saveWebtoonMode, snapToSpreadStart, type FitMode, type SplitMode, type WebtoonWidth,
} from "./readerPrefs";
import type useReadingPosition from "./useReadingPosition";
import WebtoonStrip, { type WebtoonStripHandle } from "./WebtoonStrip";

export interface ReaderViewProps {
  itemId: number;
  detail: ItemDetail;
  /** A page's URL from the media plane; no `maxWidth` = the original bytes. */
  pageSrc: (page: number, maxWidth?: number) => string;
  position: ReturnType<typeof useReadingPosition>;
  onClose: () => void;
  /** Open another book in the reader (the "Up next" / "Previous" hand-off). */
  onOpenItem?: (itemId: number) => void;
  isMarked?: boolean;
  isWantToRead?: boolean;
  onToggleMarked?: (id: number) => void;
  onToggleWantToRead?: (id: number) => void;
  kidsStyle?: KidStyle;
}

const MIN_ZOOM = 0.25;
const MAX_ZOOM = 8;
const PREFETCH_AHEAD = 4;
const HIRES_ZOOM_THRESHOLD = 1.5;
/** H/W at/above which an interior page is a genuine long strip (manhwa / webtoon). */
const WEBTOON_STRIP_RATIO = 2.2;
const GESTURE_SMOOTHING_MEDIUM = true;
const LONG_PRESS_MS = 350;
const LONG_PRESS_SLOP = 10;

/** Marvel "Infinity Comics" are only mildly tall but always carry the branding; match that and explicit labels. */
export function looksLikeWebtoonByName(detail: ItemDetail): boolean {
  const s = detail.summary;
  const hay = [s.title, s.series, detail.parsed?.seriesKey, detail.relativePath, detail.folderPath].filter(Boolean).join(" ").toLowerCase();
  return hay.includes("infinity comic") || /\bwebtoon\b/.test(hay) || /\bwebcomic\b/.test(hay);
}

function decodeThen(img: HTMLImageElement, cb: () => void) {
  const d = (img as HTMLImageElement & { decode?: () => Promise<void> }).decode?.();
  if (d && typeof d.then === "function") d.then(cb).catch(cb);
  else cb();
}

function ReadingOrderPill({ dir, title, onPick }: { dir: "prev" | "next"; title: string; onPick: () => void }) {
  const isNext = dir === "next";
  const chevron = (
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className="rdr-pill-chev" aria-hidden="true">
      {isNext ? <polyline points="9 18 15 12 9 6" /> : <polyline points="15 18 9 12 15 6" />}
    </svg>
  );
  return (
    <button type="button" data-reader-control onClick={(e) => { e.stopPropagation(); onPick(); }} title={`${isNext ? "Read next" : "Read previous"}: ${title}`} className="rdr-pill">
      {!isNext && chevron}
      <span className="rdr-pill-k">{isNext ? "Up next" : "Previous"}</span>
      <span className="rdr-pill-t">{title}</span>
      {isNext && chevron}
    </button>
  );
}

export default function ReaderView({ itemId, detail, pageSrc, position, onClose, onOpenItem, isMarked, isWantToRead, onToggleMarked, onToggleWantToRead, kidsStyle }: ReaderViewProps) {
  const pageCount = detail.summary.pageCount ?? 0;
  const title = detail.summary.title ?? detail.summary.fileName;

  const [pageIndex, setPageIndex] = useState(0);
  const [scrubPreview, setScrubPreview] = useState<number | null>(null);
  const [fitMode, setFitMode] = useState<FitMode>(() => loadReaderPrefs().fitMode);
  const [splitMode, setSplitMode] = useState<SplitMode>(() => loadReaderPrefs().splitMode);
  const [coverAsPage, setCoverAsPage] = useState(() => loadReaderPrefs().coverAsPage);
  const [webtoon, setWebtoon] = useState(false);
  const [webtoonWidth, setWebtoonWidth] = useState<WebtoonWidth>(() => loadReaderPrefs().webtoonWidth);
  const [webtoonGap, setWebtoonGap] = useState(() => loadReaderPrefs().webtoonGap);
  const [webtoonAutoNotice, setWebtoonAutoNotice] = useState(false);
  const [webtoonScrollSignal, setWebtoonScrollSignal] = useState<{ page: number; t: number } | null>(null);
  const webtoonDecidedRef = useRef(false);
  const webtoonRef = useRef<WebtoonStripHandle>(null);
  const [rotation, setRotation] = useState<0 | 90 | 180 | 270>(0);
  const [mirror, setMirror] = useState(false);
  const [pageOffset, setPageOffset] = useState(() => loadPageOffset(itemId));
  const [showMenu, setShowMenu] = useState(false);
  const [debugInfo, setDebugInfo] = useState<string | null>(null);
  const tier = useMenuTier();
  const isCompact = tier === "compact";

  const zoomScaleRef = useRef(1);
  const scrollXRef = useRef(0);
  const scrollYRef = useRef(0);
  const maxScrollXRef = useRef(0);
  const maxScrollYRef = useRef(0);
  const [scrollable, setScrollable] = useState(false);
  const [badgeCovered, setBadgeCovered] = useState(false);
  const [badgePeek, setBadgePeek] = useState(false);
  const viewTransformRef = useRef({ dx: 0, dy: 0, drawW: 0, drawH: 0 });
  const lastCanvasRef = useRef({ w: 0, h: 0 });
  const [grabbing, setGrabbing] = useState(false);
  const grabbingRef = useRef(false);

  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const isTouchDevice = useRef(typeof window !== "undefined" && typeof window.matchMedia === "function" && window.matchMedia("(pointer: coarse)").matches).current;
  const [textZoomEnabled, setTextZoomEnabled] = useState(() => loadReaderPrefs().textZoom);
  const [bubbleZoom, setBubbleZoom] = useState<{ pageIndex: number; regionIdx: number; tapX?: number; tapY?: number } | null>(null);
  const [bubbleAnchor, setBubbleAnchor] = useState<BubbleAnchor | null>(null);
  const [showBubbleDebug, setShowBubbleDebug] = useState(false);
  const textRegionCacheRef = useRef<Map<number, TextRegion[]>>(new Map());
  const drawTransformRef = useRef<PageSlot[] | null>(null);

  const canvasRef = useRef<HTMLCanvasElement>(null);
  const readerRef = useRef<HTMLDivElement>(null);
  const scrubRef = useRef<HTMLDivElement>(null);
  const imageCacheRef = useRef<Map<number, HTMLImageElement>>(new Map());
  const prefetchTokenRef = useRef(0);
  const currentImageRef = useRef<HTMLImageElement | null>(null);
  const spreadImageRef = useRef<HTMLImageElement | null>(null);
  const hiResRef = useRef<Map<number, HTMLImageElement>>(new Map());
  const hiResPendingRef = useRef<Set<number>>(new Set());
  const drawRafRef = useRef(0);
  const drawCanvasRef = useRef<() => void>(() => {});
  const gestureActiveRef = useRef(false);
  const wheelSettleRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const hiResTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const longPressTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const tryTextZoomRef = useRef<(x: number, y: number) => boolean>(() => false);
  const resumedRef = useRef(false);

  // Reading-order neighbours, asked for when the reader is near either end.
  const nextQ = useQuery({ queryKey: ["books", "next", itemId], queryFn: () => fetchNext(itemId), enabled: pageCount > 0 && pageIndex >= pageCount - 2, retry: false, staleTime: 5 * 60 * 1000 });
  const prevQ = useQuery({ queryKey: ["books", "prev", itemId], queryFn: () => fetchPrev(itemId), enabled: pageIndex <= 1, retry: false, staleTime: 5 * 60 * 1000 });
  const nextItem = nextQ.data?.item?.summary ?? null;
  const prevItem = prevQ.data?.item?.summary ?? null;

  // Resume once from the saved position.
  useEffect(() => {
    if (resumedRef.current || !position.resume) return;
    resumedRef.current = true;
    const r = position.resume;
    const resolved = r.status === "finished" ? Math.max(0, pageCount - 1) : r.page;
    if (resolved != null && resolved > 0) {
      setPageIndex(Math.min(resolved, Math.max(0, pageCount - 1)));
      setWebtoonScrollSignal({ page: resolved, t: Date.now() });
    }
  }, [position.resume, pageCount]);

  // Decide whether THIS book opens in webtoon mode: a remembered choice, else the name, else an aspect probe.
  useEffect(() => {
    if (webtoonDecidedRef.current || pageCount <= 0) return;
    const stored = loadWebtoonMode(itemId);
    if (stored !== null) { webtoonDecidedRef.current = true; setWebtoon(stored); return; }
    if (looksLikeWebtoonByName(detail)) { webtoonDecidedRef.current = true; setWebtoon(true); setWebtoonAutoNotice(true); return; }
    let cancelled = false;
    const probeIdx = pageCount > 2 ? Math.floor(pageCount / 2) : Math.max(0, pageCount - 1);
    const im = new Image();
    (im as { fetchPriority?: string }).fetchPriority = "low";
    im.onload = () => {
      if (cancelled || webtoonDecidedRef.current) return;
      if (im.naturalWidth > 0 && im.naturalHeight / im.naturalWidth >= WEBTOON_STRIP_RATIO) { webtoonDecidedRef.current = true; setWebtoon(true); setWebtoonAutoNotice(true); }
    };
    im.src = pageSrc(probeIdx, 480);
    return () => { cancelled = true; };
  }, [detail, itemId, pageCount, pageSrc]);

  useEffect(() => {
    if (!webtoonAutoNotice) return;
    const t = setTimeout(() => setWebtoonAutoNotice(false), 2800);
    return () => clearTimeout(t);
  }, [webtoonAutoNotice]);

  useEffect(() => {
    saveReaderPrefs({ fitMode, splitMode, coverAsPage, textZoom: textZoomEnabled, webtoonWidth, webtoonGap });
  }, [fitMode, splitMode, coverAsPage, textZoomEnabled, webtoonWidth, webtoonGap]);

  const pageUrlWithDpr = useCallback((pageIdx: number) => {
    const dpr = window.devicePixelRatio || 1;
    return pageSrc(pageIdx, Math.ceil(window.innerWidth * dpr));
  }, [pageSrc]);

  const syncScrollable = useCallback((mx: number, my: number) => {
    const s = mx > 0 || my > 0;
    setScrollable((prev) => (prev === s ? prev : s));
  }, []);
  const syncBadgeCovered = useCallback((dx: number, dy: number, drawW: number, drawH: number, vw: number, vh: number) => {
    const c = dx + drawW > vw - 184 && dy + drawH > vh - 56 && dx < vw - 16 && dy < vh - 16;
    setBadgeCovered((prev) => (prev === c ? prev : c));
  }, []);

  const drawCanvas = useCallback(() => {
    const canvas = canvasRef.current;
    const ctx = canvas?.getContext("2d");
    const baseImg = currentImageRef.current;
    if (!canvas || !ctx || !baseImg) return;
    const dpr = window.devicePixelRatio || 1;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const cw = Math.round(vw * dpr), ch = Math.round(vh * dpr);
    if (lastCanvasRef.current.w !== cw || lastCanvasRef.current.h !== ch) {
      canvas.width = cw; canvas.height = ch;
      canvas.style.width = `${vw}px`; canvas.style.height = `${vh}px`;
      lastCanvasRef.current = { w: cw, h: ch };
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = "#000";
    ctx.fillRect(0, 0, vw, vh);
    const zoomScale = zoomScaleRef.current;

    if (!isSplitModeEnabled(splitMode)) {
      const img = hiResRef.current.get(pageIndex) ?? baseImg;
      const paneW = img.width, paneH = img.height;
      const rotated = rotation === 90 || rotation === 270;
      const effW = rotated ? paneH : paneW;
      const effH = rotated ? paneW : paneH;
      let base = 1;
      if (fitMode === "width") base = vw / effW;
      else if (fitMode === "height") base = vh / effH;
      else if (fitMode === "auto") base = Math.min(vw / effW, vh / effH);
      const scale = base * zoomScale;
      const drawW = Math.max(1, Math.floor(effW * scale));
      const drawH = Math.max(1, Math.floor(effH * scale));
      const newMaxScrollY = Math.max(0, drawH - vh);
      const newMaxScrollX = Math.max(0, drawW - vw);
      maxScrollXRef.current = newMaxScrollX; maxScrollYRef.current = newMaxScrollY;
      const sx = Math.min(Math.max(0, scrollXRef.current), newMaxScrollX);
      const sy = Math.min(Math.max(0, scrollYRef.current), newMaxScrollY);
      scrollXRef.current = sx; scrollYRef.current = sy;
      syncScrollable(newMaxScrollX, newMaxScrollY);
      const dx = newMaxScrollX > 0 ? -sx : Math.floor((vw - drawW) / 2);
      const dy = newMaxScrollY > 0 ? -sy : Math.floor((vh - drawH) / 2);
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = GESTURE_SMOOTHING_MEDIUM && gestureActiveRef.current ? "medium" : "high";
      const iw = paneW * scale, ih = paneH * scale;
      const cx = dx + drawW / 2, cy = dy + drawH / 2;
      ctx.save();
      ctx.translate(cx, cy);
      if (mirror) ctx.scale(-1, 1);
      if (rotation) ctx.rotate((rotation * Math.PI) / 180);
      ctx.drawImage(img, 0, 0, img.width, img.height, -iw / 2, -ih / 2, iw, ih);
      ctx.restore();
      viewTransformRef.current = { dx, dy, drawW, drawH };
      syncBadgeCovered(dx, dy, drawW, drawH, vw, vh);
      drawTransformRef.current = [{ pageIndex, screenX: dx, screenY: dy, screenW: drawW, screenH: drawH, imgW: img.width, imgH: img.height }];
      return;
    }

    const pageA = hiResRef.current.get(pageIndex) ?? baseImg;
    const pageB = hiResRef.current.get(pageIndex + 1) ?? spreadImageRef.current;
    const w1 = pageA.width, h1 = pageA.height;
    const w2 = pageB ? pageB.width : 0;
    const h2 = pageB ? pageB.height : h1;
    const totalSourceW = w1 + (pageB ? w2 : 0);
    const maxSourceH = Math.max(h1, h2);
    const rotated = rotation === 90 || rotation === 270;
    const effSpreadW = rotated ? maxSourceH : totalSourceW;
    const effSpreadH = rotated ? totalSourceW : maxSourceH;
    let base = 1;
    if (fitMode === "width") base = vw / Math.max(1, effSpreadW);
    else if (fitMode === "height") base = vh / Math.max(1, effSpreadH);
    else if (fitMode === "auto") base = Math.min(vw / Math.max(1, effSpreadW), vh / Math.max(1, effSpreadH));
    const scale = base * zoomScale;
    const drawW1 = Math.max(1, Math.floor(w1 * scale));
    const drawH1 = Math.max(1, Math.floor(h1 * scale));
    const drawW2 = pageB ? Math.max(1, Math.floor(w2 * scale)) : 0;
    const drawH2 = pageB ? Math.max(1, Math.floor(h2 * scale)) : drawH1;
    const combinedW = drawW1 + drawW2;
    const combinedH = Math.max(drawH1, drawH2);
    const fpW = rotated ? combinedH : combinedW;
    const fpH = rotated ? combinedW : combinedH;
    const newMaxScrollY = Math.max(0, fpH - vh);
    const newMaxScrollX = Math.max(0, fpW - vw);
    maxScrollXRef.current = newMaxScrollX; maxScrollYRef.current = newMaxScrollY;
    const fsx = Math.min(Math.max(0, scrollXRef.current), newMaxScrollX);
    const fsy = Math.min(Math.max(0, scrollYRef.current), newMaxScrollY);
    scrollXRef.current = fsx; scrollYRef.current = fsy;
    syncScrollable(newMaxScrollX, newMaxScrollY);
    const fx = newMaxScrollX > 0 ? -fsx : Math.floor((vw - fpW) / 2);
    const fy = newMaxScrollY > 0 ? -fsy : Math.floor((vh - fpH) / 2);
    const leftFirst = splitMode === "l2r";
    const leftImage = leftFirst ? pageA : pageB;
    const rightImage = leftFirst ? pageB : pageA;
    const leftW = leftFirst ? drawW1 : drawW2;
    const rightW = leftFirst ? drawW2 : drawW1;
    const leftH = leftFirst ? drawH1 : drawH2;
    const rightH = leftFirst ? drawH2 : drawH1;
    const cx = fx + fpW / 2, cy = fy + fpH / 2;
    const ox = -combinedW / 2, oy = -combinedH / 2;
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = GESTURE_SMOOTHING_MEDIUM && gestureActiveRef.current ? "medium" : "high";
    ctx.save();
    ctx.translate(cx, cy);
    if (mirror) ctx.scale(-1, 1);
    if (rotation) ctx.rotate((rotation * Math.PI) / 180);
    if (leftImage) ctx.drawImage(leftImage, 0, 0, leftImage.width, leftImage.height, ox, oy, leftW, leftH);
    if (rightImage) ctx.drawImage(rightImage, 0, 0, rightImage.width, rightImage.height, ox + leftW, oy, rightW, rightH);
    ctx.restore();
    const leftPageIdx = leftFirst ? pageIndex : pageIndex + 1;
    const rightPageIdx = leftFirst ? pageIndex + 1 : pageIndex;
    const slots: PageSlot[] = [];
    if (leftImage) slots.push({ pageIndex: leftPageIdx, screenX: fx, screenY: fy, screenW: leftW, screenH: leftH, imgW: leftImage.width, imgH: leftImage.height });
    if (rightImage) slots.push({ pageIndex: rightPageIdx, screenX: fx + leftW, screenY: fy, screenW: rightW, screenH: rightH, imgW: rightImage.width, imgH: rightImage.height });
    viewTransformRef.current = { dx: fx, dy: fy, drawW: fpW, drawH: fpH };
    syncBadgeCovered(fx, fy, fpW, fpH, vw, vh);
    drawTransformRef.current = slots;
  }, [fitMode, splitMode, pageIndex, rotation, mirror, syncScrollable, syncBadgeCovered]);
  drawCanvasRef.current = drawCanvas;

  const scheduleDraw = useCallback(() => {
    if (drawRafRef.current) return;
    drawRafRef.current = requestAnimationFrame(() => { drawRafRef.current = 0; drawCanvasRef.current(); });
  }, []);
  const settleGesture = useCallback(() => {
    if (!gestureActiveRef.current) return;
    gestureActiveRef.current = false;
    scheduleDraw();
  }, [scheduleDraw]);

  const ensureSpreadImage = useCallback(() => {
    spreadImageRef.current = null;
    if (!isSplitModeEnabled(splitMode)) return;
    if (isSinglePageSpread(pageIndex, splitMode, coverAsPage)) return;
    const nextIndex = pageIndex + 1;
    if (nextIndex < 0 || nextIndex >= pageCount) return;
    const cached = imageCacheRef.current.get(nextIndex);
    if (cached) { spreadImageRef.current = cached; return; }
    const img = new Image();
    img.onload = () => {
      imageCacheRef.current.set(nextIndex, img);
      decodeThen(img, () => { if (pageIndex + 1 === nextIndex) { spreadImageRef.current = img; scheduleDraw(); } });
    };
    img.src = pageUrlWithDpr(nextIndex);
  }, [splitMode, pageIndex, coverAsPage, pageCount, pageUrlWithDpr, scheduleDraw]);

  const prefetchAhead = useCallback((fromIdx: number) => {
    const lastWanted = Math.min(fromIdx + PREFETCH_AHEAD, pageCount - 1);
    const token = ++prefetchTokenRef.current;
    const loadOne = (idx: number) => {
      if (token !== prefetchTokenRef.current) return;
      if (idx > lastWanted) return;
      if (idx < 0) { loadOne(idx + 1); return; }
      if (imageCacheRef.current.has(idx)) { loadOne(idx + 1); return; }
      const img = new Image();
      (img as { fetchPriority?: string }).fetchPriority = "low";
      img.onload = () => {
        decodeThen(img, () => {
          if (token !== prefetchTokenRef.current) return;
          imageCacheRef.current.set(idx, img);
          const lo = fromIdx - 1, hi = fromIdx + PREFETCH_AHEAD;
          for (const key of imageCacheRef.current.keys()) if (key < lo || key > hi) imageCacheRef.current.delete(key);
          loadOne(idx + 1);
        });
      };
      img.onerror = () => { /* stop the chain quietly */ };
      img.src = pageUrlWithDpr(idx);
    };
    loadOne(fromIdx + 1);
  }, [pageCount, pageUrlWithDpr]);

  const schedulePrefetch = useCallback((fromIdx: number) => {
    const ric = (window as Window & { requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number }).requestIdleCallback;
    if (ric) ric(() => prefetchAhead(fromIdx), { timeout: 1000 });
    else setTimeout(() => prefetchAhead(fromIdx), 200);
  }, [prefetchAhead]);

  const loadCurrentPage = useCallback(async () => {
    if (webtoon || pageCount <= 0) { setIsLoading(false); return; }
    const pageIdx = pageIndex;
    setError(null);
    setIsLoading(true);
    const cached = imageCacheRef.current.get(pageIdx);
    if (cached) {
      currentImageRef.current = cached;
      ensureSpreadImage();
      setIsLoading(false);
      scheduleDraw();
      schedulePrefetch(pageIdx);
      return;
    }
    const img = new Image();
    const url = pageUrlWithDpr(pageIdx);
    img.onload = () => {
      decodeThen(img, () => {
        currentImageRef.current = img;
        imageCacheRef.current.set(pageIdx, img);
        scrollXRef.current = 0; scrollYRef.current = 0; zoomScaleRef.current = 1;
        ensureSpreadImage();
        setIsLoading(false);
        scheduleDraw();
        schedulePrefetch(pageIdx);
      });
    };
    img.onerror = async () => {
      setIsLoading(false);
      reportMediaFailure(url);
      try {
        const r = await fetch(url);
        setError(r.status === 404 ? "Page not found." : r.status === 401 || r.status === 403 ? "Session expired." : `Error loading page (HTTP ${r.status}).`);
      } catch {
        setError("Error loading page.");
      }
    };
    img.src = url;
  }, [webtoon, pageCount, pageIndex, ensureSpreadImage, scheduleDraw, schedulePrefetch, pageUrlWithDpr]);

  useEffect(() => { void loadCurrentPage(); }, [loadCurrentPage]);

  useEffect(() => {
    setBadgePeek(true);
    const t = setTimeout(() => setBadgePeek(false), 1400);
    return () => clearTimeout(t);
  }, [pageIndex, itemId]);

  useEffect(() => { setBubbleZoom(null); hiResRef.current.clear(); hiResPendingRef.current.clear(); }, [pageIndex]);
  useEffect(() => { setBubbleAnchor(null); }, [textZoomEnabled]);

  // Text regions for the current page and the next two (touch only — Bubble Zoom is touch only).
  useEffect(() => {
    if (!textZoomEnabled || !isTouchDevice || webtoon || pageCount <= 0) return;
    [pageIndex, pageIndex + 1, pageIndex + 2].forEach((pi) => {
      if (pi >= 0 && pi < pageCount && !textRegionCacheRef.current.has(pi)) {
        fetchTextRegions(itemId, pi).then((r) => { textRegionCacheRef.current.set(pi, r?.regions ?? []); }).catch(() => { textRegionCacheRef.current.set(pi, []); });
      }
    });
  }, [pageIndex, itemId, pageCount, textZoomEnabled, webtoon, isTouchDevice]);

  // The "Up next" hand-off swaps the book in place: per-book state reloads here.
  useEffect(() => {
    textRegionCacheRef.current.clear(); hiResRef.current.clear(); hiResPendingRef.current.clear();
    imageCacheRef.current.clear(); currentImageRef.current = null; spreadImageRef.current = null;
    setRotation(0); setMirror(false); setBubbleAnchor(null); setPageOffset(loadPageOffset(itemId));
    setPageIndex(0); setWebtoon(false); webtoonDecidedRef.current = false; resumedRef.current = false;
    setError(null);
  }, [itemId]);

  useEffect(() => {
    const handleResize = () => drawCanvas();
    window.addEventListener("resize", handleResize);
    return () => window.removeEventListener("resize", handleResize);
  }, [drawCanvas]);
  useEffect(() => { drawCanvas(); }, [drawCanvas]);
  useEffect(() => () => {
    if (drawRafRef.current) cancelAnimationFrame(drawRafRef.current);
    if (hiResTimerRef.current) clearTimeout(hiResTimerRef.current);
    if (wheelSettleRef.current) clearTimeout(wheelSettleRef.current);
    if (longPressTimerRef.current) clearTimeout(longPressTimerRef.current);
  }, []);

  const loadHiRes = useCallback(() => {
    const slots = drawTransformRef.current;
    if (!slots) return;
    for (const slot of slots) {
      const p = slot.pageIndex;
      if (p < 0 || p >= pageCount) continue;
      if (hiResRef.current.has(p) || hiResPendingRef.current.has(p)) continue;
      hiResPendingRef.current.add(p);
      const img = new Image();
      (img as { fetchPriority?: string }).fetchPriority = "low";
      img.onload = () => decodeThen(img, () => {
        hiResPendingRef.current.delete(p);
        const visible = new Set((drawTransformRef.current ?? []).map((s) => s.pageIndex));
        if (!visible.has(p)) return;
        hiResRef.current.set(p, img);
        scheduleDraw();
      });
      img.onerror = () => { hiResPendingRef.current.delete(p); };
      img.src = pageSrc(p);
    }
  }, [pageCount, pageSrc, scheduleDraw]);

  const requestHiResDebounced = useCallback(() => {
    if (hiResTimerRef.current) clearTimeout(hiResTimerRef.current);
    hiResTimerRef.current = setTimeout(() => { if (zoomScaleRef.current >= HIRES_ZOOM_THRESHOLD) loadHiRes(); }, 250);
  }, [loadHiRes]);

  const applyZoom = useCallback((nextRaw: number, focalX: number, focalY: number, panDx = 0, panDy = 0) => {
    const next = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, nextRaw));
    const cur = zoomScaleRef.current;
    const vt = viewTransformRef.current;
    if (vt.drawW <= 0 || vt.drawH <= 0 || cur <= 0) { zoomScaleRef.current = next; scheduleDraw(); requestHiResDebounced(); return; }
    const ratio = next / cur;
    const newDrawW = vt.drawW * ratio;
    const newDrawH = vt.drawH * ratio;
    const fracX = (focalX - vt.dx) / vt.drawW;
    const fracY = (focalY - vt.dy) / vt.drawH;
    const newDx = focalX - fracX * newDrawW + panDx;
    const newDy = focalY - fracY * newDrawH + panDy;
    const maxX = Math.max(0, newDrawW - window.innerWidth);
    const maxY = Math.max(0, newDrawH - window.innerHeight);
    zoomScaleRef.current = next;
    scrollXRef.current = Math.min(maxX, Math.max(0, -newDx));
    scrollYRef.current = Math.min(maxY, Math.max(0, -newDy));
    scheduleDraw();
    requestHiResDebounced();
  }, [scheduleDraw, requestHiResDebounced]);

  // Wheel: pans whichever axis overflows, Shift forces horizontal, Ctrl/Cmd zooms about the cursor.
  useEffect(() => {
    const reader = readerRef.current;
    if (!reader || webtoon) return;
    const markWheelGesture = () => {
      gestureActiveRef.current = true;
      if (wheelSettleRef.current) clearTimeout(wheelSettleRef.current);
      wheelSettleRef.current = setTimeout(settleGesture, 160);
    };
    const handleWheel = (e: WheelEvent) => {
      if (showMenu) return;
      if (e.ctrlKey || e.metaKey) {
        e.preventDefault();
        markWheelGesture();
        applyZoom(zoomScaleRef.current * (e.deltaY < 0 ? 1.1 : 0.9), e.clientX, e.clientY);
        return;
      }
      const canX = maxScrollXRef.current > 0, canY = maxScrollYRef.current > 0;
      if (!canX && !canY) return;
      e.preventDefault();
      markWheelGesture();
      let dx = e.deltaX, dy = e.deltaY;
      if (e.shiftKey && dx === 0) { dx = dy; dy = 0; }
      if (canY && dy) scrollYRef.current = Math.min(maxScrollYRef.current, Math.max(0, scrollYRef.current + dy));
      if (canX && dx) scrollXRef.current = Math.min(maxScrollXRef.current, Math.max(0, scrollXRef.current + dx));
      scheduleDraw();
    };
    reader.addEventListener("wheel", handleWheel, { passive: false });
    return () => reader.removeEventListener("wheel", handleWheel);
  }, [showMenu, applyZoom, scheduleDraw, settleGesture, webtoon]);

  const activeTouchesRef = useRef<Map<number, { clientX: number; clientY: number }>>(new Map());
  const touchStateRef = useRef({
    active: false, mouse: false, vertical: false, horizontal: false, startX: 0, startY: 0, lastX: 0, lastY: 0,
    pinchActive: false, pinchStartDist: 0, pinchStartScale: 1, lastMidX: 0, lastMidY: 0, fromPinch: false, suppressTapUntil: 0, longPressed: false,
  });
  const getTouchDist = (a: { clientX: number; clientY: number }, b: { clientX: number; clientY: number }) => Math.hypot(b.clientX - a.clientX, b.clientY - a.clientY);

  const scrollDownStep = useCallback(() => {
    const max = maxScrollYRef.current;
    if (max <= 0 || scrollYRef.current >= max) return false;
    scrollYRef.current = Math.min(max, scrollYRef.current + Math.max(180, Math.floor(window.innerHeight * 0.8)));
    scheduleDraw();
    return true;
  }, [scheduleDraw]);
  const scrollUpStep = useCallback(() => {
    if (maxScrollYRef.current <= 0 || scrollYRef.current <= 0) return false;
    scrollYRef.current = Math.max(0, scrollYRef.current - Math.max(180, Math.floor(window.innerHeight * 0.8)));
    scheduleDraw();
    return true;
  }, [scheduleDraw]);

  const queueBookmarkSave = useCallback((targetPage?: number) => {
    position.savePage(targetPage ?? pageIndex);
  }, [position, pageIndex]);

  const goNext = useCallback(() => {
    if (pageIndex >= pageCount - 1) return;
    let newPage: number;
    if (isSplitModeEnabled(splitMode)) {
      const step = isSinglePageSpread(pageIndex, splitMode, coverAsPage) ? 1 : 2;
      newPage = snapToSpreadStart(Math.min(pageCount - 1, pageIndex + step), splitMode, coverAsPage);
    } else newPage = Math.min(pageCount - 1, pageIndex + 1);
    setPageIndex(newPage);
    scrollYRef.current = 0; scrollXRef.current = 0; zoomScaleRef.current = 1;
    queueBookmarkSave(newPage);
  }, [pageCount, pageIndex, splitMode, coverAsPage, queueBookmarkSave]);

  const goPrev = useCallback(() => {
    if (pageIndex <= 0) return;
    const newPage = isSplitModeEnabled(splitMode) ? snapToSpreadStart(Math.max(0, pageIndex - 2), splitMode, coverAsPage) : Math.max(0, pageIndex - 1);
    setPageIndex(newPage);
    scrollYRef.current = 0; scrollXRef.current = 0; zoomScaleRef.current = 1;
    queueBookmarkSave(newPage);
  }, [pageIndex, splitMode, coverAsPage, queueBookmarkSave]);

  const goDirectionalNext = useCallback(() => { if (isRtlSplitReading(splitMode)) goPrev(); else goNext(); }, [splitMode, goNext, goPrev]);
  const goDirectionalPrev = useCallback(() => { if (isRtlSplitReading(splitMode)) goNext(); else goPrev(); }, [splitMode, goNext, goPrev]);

  const goToPagePrompt = useCallback(() => {
    if (pageCount <= 0) return;
    const pageNo = prompt(`Go to page (${1 + pageOffset}-${pageCount + pageOffset})`);
    if (!pageNo) return;
    let target = Math.min(Math.max(0, parseInt(pageNo, 10) - 1 - pageOffset), Math.max(0, pageCount - 1));
    if (Number.isNaN(target)) return;
    if (webtoon) { webtoonRef.current?.scrollToPage(target); return; }
    if (isSplitModeEnabled(splitMode)) target = snapToSpreadStart(target, splitMode, coverAsPage);
    setPageIndex(target);
    scrollYRef.current = 0; scrollXRef.current = 0; zoomScaleRef.current = 1;
    queueBookmarkSave(target);
  }, [pageCount, splitMode, coverAsPage, pageOffset, webtoon, queueBookmarkSave]);

  const jumpToPage = useCallback((target: number) => {
    const clamped = Math.min(Math.max(0, target), Math.max(0, pageCount - 1));
    if (webtoon) { webtoonRef.current?.scrollToPage(clamped); return; }
    const snapped = isSplitModeEnabled(splitMode) ? snapToSpreadStart(clamped, splitMode, coverAsPage) : clamped;
    setPageIndex(snapped);
    scrollYRef.current = 0; scrollXRef.current = 0; zoomScaleRef.current = 1;
    queueBookmarkSave(snapped);
  }, [pageCount, webtoon, splitMode, coverAsPage, queueBookmarkSave]);

  const setReadingMode = useCallback((on: boolean) => {
    if (on === webtoon) return;
    webtoonDecidedRef.current = true;
    saveWebtoonMode(itemId, on);
    setWebtoon(on);
    if (on) setWebtoonScrollSignal({ page: pageIndex, t: Date.now() });
    else { scrollYRef.current = 0; scrollXRef.current = 0; zoomScaleRef.current = 1; }
  }, [webtoon, itemId, pageIndex]);

  const toggleFullscreen = () => {
    if (!document.fullscreenElement) document.documentElement.requestFullscreen?.();
    else document.exitFullscreen?.();
  };

  // Keyboard.
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      const tag = (document.activeElement?.tagName || "").toLowerCase();
      if (tag === "input" || tag === "textarea" || tag === "select") return;
      if (bubbleZoom) { if (e.key === "Escape") { e.preventDefault(); setBubbleZoom(null); } return; }
      if (webtoon) {
        if (e.key === "Escape") { e.preventDefault(); if (showMenu) setShowMenu(false); else onClose(); }
        else if (e.key === "ArrowDown" || e.key === " " || e.key === "PageDown") { e.preventDefault(); webtoonRef.current?.scrollViewport(1); }
        else if (e.key === "ArrowUp" || e.key === "PageUp") { e.preventDefault(); webtoonRef.current?.scrollViewport(-1); }
        else if (e.key === "ArrowRight") { e.preventDefault(); webtoonRef.current?.pageBy(1); }
        else if (e.key === "ArrowLeft") { e.preventDefault(); webtoonRef.current?.pageBy(-1); }
        else if (e.key === "g" || e.key === "G") { e.preventDefault(); goToPagePrompt(); }
        else if (e.key === "m" || e.key === "M") { e.preventDefault(); setShowMenu((p) => !p); }
        else if (e.key === "f" || e.key === "F") { e.preventDefault(); toggleFullscreen(); }
        return;
      }
      if (e.key === "Escape") { e.preventDefault(); if (showMenu) setShowMenu(false); else onClose(); }
      else if (e.key === "ArrowRight") { e.preventDefault(); goDirectionalNext(); }
      else if (e.key === "ArrowLeft") { e.preventDefault(); goDirectionalPrev(); }
      else if (e.key === "ArrowDown") { e.preventDefault(); if (!scrollDownStep()) goDirectionalNext(); }
      else if (e.key === "ArrowUp") { e.preventDefault(); if (!scrollUpStep()) goDirectionalPrev(); }
      else if (e.key === " ") { e.preventDefault(); if (!scrollDownStep()) goDirectionalNext(); }
      else if (e.key === "g" || e.key === "G") { e.preventDefault(); goToPagePrompt(); }
      else if (e.key === "m" || e.key === "M") { e.preventDefault(); setShowMenu((p) => !p); }
      else if (e.key === "f" || e.key === "F") { e.preventDefault(); toggleFullscreen(); }
      else if (e.key === "+" || e.key === "=") { e.preventDefault(); applyZoom(zoomScaleRef.current * 1.25, window.innerWidth / 2, window.innerHeight / 2); }
      else if (e.key === "-" || e.key === "_") { e.preventDefault(); applyZoom(zoomScaleRef.current * 0.8, window.innerWidth / 2, window.innerHeight / 2); }
      else if (e.key === "0") { e.preventDefault(); zoomScaleRef.current = 1; scrollXRef.current = 0; scrollYRef.current = 0; scheduleDraw(); }
      else if (e.key === "r" || e.key === "R") { e.preventDefault(); const delta = e.shiftKey ? 270 : 90; setRotation((prev) => ((prev + delta) % 360) as 0 | 90 | 180 | 270); scrollXRef.current = 0; scrollYRef.current = 0; }
      else if (e.key === "h" || e.key === "H") { e.preventDefault(); setMirror((prev) => !prev); scrollXRef.current = 0; scrollYRef.current = 0; }
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [showMenu, onClose, goDirectionalNext, goDirectionalPrev, scrollDownStep, scrollUpStep, goToPagePrompt, bubbleZoom, applyZoom, scheduleDraw, webtoon]);

  // Pointer gestures: one finger / mouse pans, two fingers pinch, a horizontal flick turns the page.
  useEffect(() => {
    const reader = readerRef.current;
    if (!reader || webtoon) return;
    const ts = touchStateRef.current;
    const beginPan = (x: number, y: number) => { ts.active = true; ts.vertical = false; ts.horizontal = false; ts.fromPinch = false; ts.startX = x; ts.startY = y; ts.lastX = x; ts.lastY = y; };
    const clearLongPress = () => { if (longPressTimerRef.current) { clearTimeout(longPressTimerRef.current); longPressTimerRef.current = undefined; } };
    const armLongPress = (x: number, y: number) => {
      clearLongPress();
      if (!isTouchDevice) return;
      longPressTimerRef.current = setTimeout(() => {
        longPressTimerRef.current = undefined;
        if (!ts.active || ts.vertical || ts.horizontal || ts.pinchActive) return;
        if (tryTextZoomRef.current(x, y)) { ts.longPressed = true; ts.suppressTapUntil = Date.now() + 600; if (navigator.vibrate) navigator.vibrate(8); }
      }, LONG_PRESS_MS);
    };
    const handlePointerDown = (e: PointerEvent) => {
      if (showMenu) return;
      if ((e.target as HTMLElement)?.closest?.("[data-reader-control]")) return;
      if (e.pointerType === "mouse") {
        if (e.button !== 0 || (maxScrollXRef.current <= 0 && maxScrollYRef.current <= 0)) return;
        ts.mouse = true; beginPan(e.clientX, e.clientY); return;
      }
      if (e.pointerType !== "touch") return;
      activeTouchesRef.current.set(e.pointerId, { clientX: e.clientX, clientY: e.clientY });
      if (activeTouchesRef.current.size === 2) {
        clearLongPress();
        const pts = Array.from(activeTouchesRef.current.values());
        ts.pinchActive = true; ts.pinchStartDist = getTouchDist(pts[0], pts[1]); ts.pinchStartScale = zoomScaleRef.current;
        ts.lastMidX = (pts[0].clientX + pts[1].clientX) / 2; ts.lastMidY = (pts[0].clientY + pts[1].clientY) / 2;
        ts.active = false;
        return;
      }
      beginPan(e.clientX, e.clientY);
      ts.longPressed = false;
      armLongPress(e.clientX, e.clientY);
    };
    const handlePointerMove = (e: PointerEvent) => {
      if (e.pointerType === "mouse") {
        if (!ts.mouse || !ts.active) return;
        const dx = e.clientX - ts.lastX, dy = e.clientY - ts.lastY;
        ts.lastX = e.clientX; ts.lastY = e.clientY;
        if (Math.abs(e.clientX - ts.startX) > 3 || Math.abs(e.clientY - ts.startY) > 3) {
          ts.suppressTapUntil = Date.now() + 260;
          if (!grabbingRef.current) { grabbingRef.current = true; setGrabbing(true); }
          gestureActiveRef.current = true;
          if (maxScrollXRef.current > 0) scrollXRef.current = Math.min(maxScrollXRef.current, Math.max(0, scrollXRef.current - dx));
          if (maxScrollYRef.current > 0) scrollYRef.current = Math.min(maxScrollYRef.current, Math.max(0, scrollYRef.current - dy));
          scheduleDraw();
        }
        return;
      }
      if (e.pointerType !== "touch") return;
      activeTouchesRef.current.set(e.pointerId, { clientX: e.clientX, clientY: e.clientY });
      if (longPressTimerRef.current && (Math.abs(e.clientX - ts.startX) > LONG_PRESS_SLOP || Math.abs(e.clientY - ts.startY) > LONG_PRESS_SLOP)) clearLongPress();
      if (ts.pinchActive && activeTouchesRef.current.size >= 2) {
        e.preventDefault();
        const pts = Array.from(activeTouchesRef.current.values());
        const dist = getTouchDist(pts[0], pts[1]);
        const midX = (pts[0].clientX + pts[1].clientX) / 2;
        const midY = (pts[0].clientY + pts[1].clientY) / 2;
        gestureActiveRef.current = true;
        if (ts.pinchStartDist > 0) applyZoom(ts.pinchStartScale * (dist / ts.pinchStartDist), ts.lastMidX, ts.lastMidY, midX - ts.lastMidX, midY - ts.lastMidY);
        ts.lastMidX = midX; ts.lastMidY = midY;
        return;
      }
      if (!ts.active) return;
      const dx = e.clientX - ts.lastX;
      const dy = e.clientY - ts.lastY;
      ts.lastX = e.clientX; ts.lastY = e.clientY;
      if (!ts.vertical && !ts.horizontal) {
        const totalDx = Math.abs(e.clientX - ts.startX);
        const totalDy = Math.abs(e.clientY - ts.startY);
        if (totalDy > 12 && totalDy > totalDx + 6) ts.vertical = true;
        else if (totalDx > 12 && totalDx > totalDy + 6) ts.horizontal = true;
      }
      if (maxScrollXRef.current > 0 || maxScrollYRef.current > 0) {
        const totalDx = Math.abs(e.clientX - ts.startX);
        const totalDy = Math.abs(e.clientY - ts.startY);
        if (totalDx > 4 || totalDy > 4) {
          e.preventDefault();
          gestureActiveRef.current = true;
          if (maxScrollXRef.current > 0) scrollXRef.current = Math.min(maxScrollXRef.current, Math.max(0, scrollXRef.current - dx));
          if (maxScrollYRef.current > 0) scrollYRef.current = Math.min(maxScrollYRef.current, Math.max(0, scrollYRef.current - dy));
          scheduleDraw();
        }
      }
    };
    const endGesture = (e?: PointerEvent) => {
      clearLongPress();
      if (e?.pointerType === "mouse") {
        ts.mouse = false; ts.active = false;
        if (grabbingRef.current) { grabbingRef.current = false; setGrabbing(false); }
        settleGesture();
        return;
      }
      if (e?.pointerType === "touch") activeTouchesRef.current.delete(e.pointerId);
      const wasPinching = ts.pinchActive;
      if (activeTouchesRef.current.size < 2) ts.pinchActive = false;
      if (wasPinching && activeTouchesRef.current.size === 1) {
        const pt = activeTouchesRef.current.values().next().value;
        if (pt) { beginPan(pt.clientX, pt.clientY); ts.fromPinch = true; ts.suppressTapUntil = Date.now() + 260; }
        return;
      }
      if (!ts.active) return;
      if (ts.vertical || ts.horizontal || ts.fromPinch) ts.suppressTapUntil = Date.now() + 260;
      if (ts.horizontal && maxScrollXRef.current === 0) {
        const totalDx = ts.lastX - ts.startX;
        if (Math.abs(totalDx) > 40) { if (totalDx < 0) goDirectionalNext(); else goDirectionalPrev(); }
      }
      ts.active = false; ts.vertical = false; ts.horizontal = false; ts.fromPinch = false;
      settleGesture();
    };
    reader.addEventListener("pointerdown", handlePointerDown);
    reader.addEventListener("pointermove", handlePointerMove, { passive: false });
    reader.addEventListener("pointerup", endGesture);
    reader.addEventListener("pointercancel", endGesture);
    return () => {
      reader.removeEventListener("pointerdown", handlePointerDown);
      reader.removeEventListener("pointermove", handlePointerMove);
      reader.removeEventListener("pointerup", endGesture);
      reader.removeEventListener("pointercancel", endGesture);
    };
  }, [showMenu, applyZoom, goDirectionalNext, goDirectionalPrev, scheduleDraw, settleGesture, webtoon, isTouchDevice]);

  const shouldIgnoreTap = () => Date.now() < touchStateRef.current.suppressTapUntil;

  const tryTextZoom = (clientX: number, clientY: number): boolean => {
    if (!textZoomEnabled || !isTouchDevice || !drawTransformRef.current) return false;
    const slot = drawTransformRef.current.find((s) => clientX >= s.screenX && clientX <= s.screenX + s.screenW && clientY >= s.screenY && clientY <= s.screenY + s.screenH);
    if (!slot) return false;
    const regions = textRegionCacheRef.current.get(slot.pageIndex);
    if (!regions || regions.length === 0) return false;
    const nx = (clientX - slot.screenX) / slot.screenW;
    const ny = (clientY - slot.screenY) / slot.screenH;
    const TOL = 0.01;
    const hit = regions
      .map((r, i) => ({ r, i }))
      .filter(({ r }) => nx >= r.hitX - TOL && nx <= r.hitX + r.hitWidth + TOL && ny >= r.hitY - TOL && ny <= r.hitY + r.hitHeight + TOL)
      .sort((a, b) => a.r.hitWidth * a.r.hitHeight - b.r.hitWidth * b.r.hitHeight)[0];
    if (!hit) return false;
    setBubbleZoom({ pageIndex: slot.pageIndex, regionIdx: hit.i, tapX: clientX, tapY: clientY });
    return true;
  };
  tryTextZoomRef.current = tryTextZoom;

  /** Diagnostics: local facts only (the standalone's `/api/…` probes do not exist here). */
  const runDebug = () => {
    const c = canvasRef.current;
    const cur = currentImageRef.current;
    const lines = [
      `item: ${itemId} · ${title}`, `extension: ${detail.summary.extension ?? "?"} · pages: ${pageCount} · current: ${pageIndex}`,
      `canvas: ${c ? `${c.width}x${c.height} (css ${c.style.width} × ${c.style.height})` : "NULL"}`,
      `image: ${cur ? `${cur.width}x${cur.height} complete=${cur.complete} src=${cur.src.slice(0, 96)}` : "NULL"}`,
      `isLoading: ${isLoading} · error: ${error ?? "(none)"}`,
      `fit: ${fitMode} · split: ${splitMode} · zoom: ${zoomScaleRef.current.toFixed(2)} · rotation: ${rotation} · mirror: ${mirror}`,
      `scroll: ${scrollXRef.current},${scrollYRef.current} / max ${maxScrollXRef.current},${maxScrollYRef.current}`,
      `window: ${window.innerWidth}x${window.innerHeight} · dpr: ${window.devicePixelRatio}`,
      `cache: ${[...imageCacheRef.current.keys()].join(",") || "(empty)"} · hi-res: ${[...hiResRef.current.keys()].join(",") || "(none)"}`,
      `text regions: ${[...textRegionCacheRef.current.entries()].map(([k, v]) => `${k}:${v.length}`).join(" ") || "(none)"}`,
      `page 0 url: ${pageSrc(0, 100)}`,
    ];
    setDebugInfo(lines.join("\n"));
  };

  const totalPages = pageCount;
  const chapterNo = pageIndex + 1;
  const single = webtoon ? true : isSinglePageSpread(pageIndex, splitMode, coverAsPage);
  const spreadEnd = Math.min(totalPages, chapterNo + 1);
  const lastVisibleIndex = isSplitModeEnabled(splitMode) && !single ? Math.min(totalPages - 1, pageIndex + 1) : pageIndex;
  const onLastPage = lastVisibleIndex >= totalPages - 1;
  const onFirstPage = pageIndex <= 0;
  const chapterLabel = splitMode === "none" || single ? `Page ${chapterNo + pageOffset} of ${totalPages}` : `Pages ${chapterNo + pageOffset}-${spreadEnd + pageOffset} of ${totalPages}`;
  const progress = totalPages > 0 ? ((pageIndex + 1) / totalPages) * 100 : 0;
  const scrubProgress = scrubPreview != null && totalPages > 0 ? ((scrubPreview + 1) / totalPages) * 100 : progress;

  const filmStart = Math.max(0, pageIndex - 7);
  const filmEnd = Math.min(totalPages - 1, pageIndex + 10);
  const filmPages: number[] = [];
  for (let n = filmStart; n <= filmEnd; n++) filmPages.push(n);

  const scrubTarget = (clientX: number): number | null => {
    const r = scrubRef.current?.getBoundingClientRect();
    if (!r || r.width <= 0) return null;
    const f = Math.min(1, Math.max(0, (clientX - r.left) / r.width));
    let target = Math.round(f * (totalPages - 1));
    if (!webtoon && isSplitModeEnabled(splitMode)) target = snapToSpreadStart(target, splitMode, coverAsPage);
    return target;
  };
  const onScrubDown = (e: React.PointerEvent) => {
    e.preventDefault();
    const start = scrubTarget(e.clientX);
    if (start != null) setScrubPreview(start);
    const mv = (ev: PointerEvent) => { const t = scrubTarget(ev.clientX); if (t != null) setScrubPreview(t); };
    const up = (ev: PointerEvent) => {
      window.removeEventListener("pointermove", mv);
      window.removeEventListener("pointerup", up);
      const t = scrubTarget(ev.clientX);
      setScrubPreview(null);
      if (t != null) jumpToPage(t);
    };
    window.addEventListener("pointermove", mv);
    window.addEventListener("pointerup", up);
  };

  const setupState = [pageOffset !== 0 ? `Offset ${pageOffset > 0 ? "+" : ""}${pageOffset}` : null, rotation ? `${rotation}°` : null, mirror ? "Mirrored" : null].filter(Boolean).join(" · ");

  return (
    <div
      ref={readerRef}
      className="rdr-root rdr-canvas-root"
      style={{ touchAction: webtoon ? "pan-y" : "none" }}
      onContextMenu={(e) => e.preventDefault()}
      data-testid="reader-canvas-root"
    >
      {!webtoon && <canvas ref={canvasRef} className="rdr-canvas" draggable={false} style={{ cursor: showMenu ? "default" : "none" }} />}

      {webtoon && (
        <WebtoonStrip
          ref={webtoonRef}
          key={itemId}
          pageSrc={pageSrc}
          pageCount={totalPages}
          width={webtoonWidth}
          gap={webtoonGap}
          scrollSignal={webtoonScrollSignal}
          onPageChange={(p) => { setPageIndex(p); queueBookmarkSave(p); }}
          onTap={() => setShowMenu(true)}
        />
      )}

      {isLoading && !webtoon && pageCount > 0 && <div className="rdr-center rdr-passthrough"><div className="rdr-spinner" /></div>}

      {pageCount <= 0 && <div className="rdr-center rdr-passthrough"><div className="rdr-error"><div className="rdr-error-t">This file has no readable pages.</div></div></div>}

      {error && !webtoon && (
        <div className="rdr-center rdr-passthrough">
          <div className="rdr-error">
            <div className="rdr-error-t">{error}</div>
            <div className="rdr-error-s">Page {pageIndex + 1} of {totalPages}</div>
          </div>
        </div>
      )}

      {!showMenu && !webtoon && (
        <div
          className="rdr-tapzone"
          style={{ cursor: scrollable ? (grabbing ? "grabbing" : "grab") : "default" }}
          onClick={(e) => {
            if (shouldIgnoreTap()) return;
            e.preventDefault();
            if (bubbleZoom) { setBubbleZoom(null); return; }
            const x = e.clientX, vw = window.innerWidth;
            if (x < vw / 3) goDirectionalPrev();
            else if (x > (2 * vw) / 3) goDirectionalNext();
            else setShowMenu(true);
          }}
        />
      )}

      {showMenu && (
        <MenuShell tier={tier} kidsStyle={kidsStyle} onClose={() => setShowMenu(false)} maxWidth={860}>
          <MenuHead eyebrow={`${(detail.summary.extension ?? ".cbz").replace(".", "")} · Books`} title={title} now={chapterNo} total={totalPages} pct={Math.round(progress)} compact={isCompact} onClose={() => setShowMenu(false)} />
          <div style={{ height: isCompact ? 12 : 24 }} />
          <Scrubber
            label={chapterLabel}
            totalLabel={`${totalPages} pages`}
            progress={scrubProgress}
            trackRef={scrubRef}
            onPointerDown={onScrubDown}
            onPrev={() => { if (webtoon) webtoonRef.current?.pageBy(-1); else goDirectionalPrev(); }}
            onNext={() => { if (webtoon) webtoonRef.current?.pageBy(1); else goDirectionalNext(); }}
            preview={scrubPreview != null ? (
              <div className="rmx-scrub-preview">
                <img src={pageSrc(scrubPreview, 120)} alt="" loading="eager" />
                <span>{scrubPreview + 1 + pageOffset}</span>
              </div>
            ) : undefined}
          />

          {!isCompact && (
            <>
              <div style={{ height: 14 }} />
              <div className="rmx-strip">
                {filmPages.map((n) => (
                  <button type="button" key={n} className={`rmx-thumb${n === pageIndex ? " on" : ""}`} data-reader-control onClick={() => jumpToPage(n)}>
                    <span className="frame"><img src={pageSrc(n, 120)} alt={`Page ${n + 1}`} loading="lazy" /></span>
                    <span className="n">{n + 1}</span>
                  </button>
                ))}
              </div>
            </>
          )}

          <hr className="rmx-div" style={{ margin: isCompact ? "12px 0" : "18px 0" }} />

          <div className="rmx-cols" style={{ gridTemplateColumns: isCompact ? "1fr" : "1.15fr 1fr" }}>
            <div className="rmx-card">
              <div className="rmx-label">Reading</div>
              <div className="rmx-stack" style={{ gap: 10 }}>
                <div className="rmx-seg fill">
                  {([
                    { v: "auto", label: "Fit page", icon: RM.fit },
                    { v: "width", label: "Fit width", icon: RM.width },
                    { v: "height", label: "Fit height", icon: RM.height },
                    { v: "original", label: "Zoom", icon: RM.zoom },
                  ] as { v: FitMode; label: string; icon: string }[]).map(({ v, label, icon }) => (
                    <button type="button" key={v} className={!webtoon && fitMode === v ? "on" : ""} onClick={() => { if (webtoon) setReadingMode(false); setFitMode(v); zoomScaleRef.current = 1; scrollXRef.current = 0; scrollYRef.current = 0; }}>
                      <RmIcon d={icon} className="seg-ic" /><span>{label}</span>
                    </button>
                  ))}
                  <span className="rmx-seg-div" aria-hidden="true" />
                  <button type="button" className={webtoon ? "on" : ""} onClick={() => setReadingMode(true)} title="Continuous vertical scroll (webtoon / long-strip)">
                    <RmIcon d={RM.webtoon} className="seg-ic" /><span>Webtoon</span>
                  </button>
                </div>

                {webtoon && (
                  <div className="rmx-seg fill">
                    {([{ v: "narrow", label: "Narrow" }, { v: "normal", label: "Normal" }, { v: "wide", label: "Wide" }, { v: "full", label: "Full" }] as { v: WebtoonWidth; label: string }[]).map(({ v, label }) => (
                      <button type="button" key={v} className={webtoonWidth === v ? "on" : ""} onClick={() => { setWebtoonWidth(v); setWebtoonScrollSignal({ page: pageIndex, t: Date.now() }); }}><span>{label}</span></button>
                    ))}
                  </div>
                )}
                {!webtoon && (
                  <div className="rmx-seg fill">
                    {([{ v: "none", label: "Single", icon: RM.single }, { v: "l2r", label: "L → R", icon: RM.spread }, { v: "r2l", label: "R → L", icon: RM.spread }] as { v: SplitMode; label: string; icon: string }[]).map(({ v, label, icon }) => (
                      <button type="button" key={v} className={splitMode === v ? "on" : ""} onClick={() => {
                        if (splitMode === v) return;
                        setSplitMode(v);
                        if (v !== "none") setPageIndex((prev) => snapToSpreadStart(prev, v, coverAsPage));
                        scrollYRef.current = 0; scrollXRef.current = 0; zoomScaleRef.current = 1;
                        queueBookmarkSave();
                      }}>
                        <RmIcon d={icon} className="seg-ic" /><span>{label}</span>
                      </button>
                    ))}
                  </div>
                )}
              </div>

              <hr className="rmx-div" style={{ margin: "12px 0 2px" }} />

              {webtoon && (
                <button type="button" className={`rmx-toggle${webtoonGap ? " on" : ""}`} onClick={() => { setWebtoonGap((v) => !v); setWebtoonScrollSignal({ page: pageIndex, t: Date.now() }); }}>
                  <span className="grow"><span className="tt">Panel gaps</span><span className="ts" style={{ display: "block" }}>{webtoonGap ? "Small space between panels" : "Seamless continuous strip"}</span></span>
                  <span className="rmx-switch" />
                </button>
              )}
              {!webtoon && (
                <>
                  <button type="button" className={`rmx-toggle${coverAsPage ? " on" : ""}`} disabled={!isSplitModeEnabled(splitMode)} onClick={() => {
                    if (!isSplitModeEnabled(splitMode)) return;
                    const next = !coverAsPage;
                    setCoverAsPage(next);
                    setPageIndex((prev) => snapToSpreadStart(prev, splitMode, next));
                    scrollYRef.current = 0; scrollXRef.current = 0; zoomScaleRef.current = 1;
                  }}>
                    <span className="grow"><span className="tt">Cover as single page</span><span className="ts" style={{ display: "block" }}>{!isSplitModeEnabled(splitMode) ? "Available in two-page spread" : "First page stands alone"}</span></span>
                    <span className="rmx-switch" />
                  </button>
                  <button type="button" className={`rmx-toggle${textZoomEnabled ? " on" : ""}`} disabled={!isTouchDevice} onClick={() => { if (isTouchDevice) setTextZoomEnabled((v) => !v); }}>
                    <span className="grow"><span className="tt">Bubble Zoom</span><span className="ts" style={{ display: "block" }}>{isTouchDevice ? "Press and hold a balloon to enlarge · touch only" : "Mobile only — not available on desktop"}</span></span>
                    <span className="rmx-switch" />
                  </button>
                </>
              )}

              <details className="rmx-diag rmx-setup">
                <summary>Page setup<span className="rmx-setup-state">{setupState}</span></summary>
                <div className="rmx-diag-body">
                  {!webtoon && (
                    <div className="rmx-seg fill">
                      <button type="button" onClick={() => { setRotation((prev) => ((prev + 270) % 360) as 0 | 90 | 180 | 270); scrollXRef.current = 0; scrollYRef.current = 0; }} title="Rotate left (Shift+R)"><RmIcon d={RM.rotccw} className="seg-ic" /><span>Rotate ⟲</span></button>
                      <button type="button" onClick={() => { setRotation((prev) => ((prev + 90) % 360) as 0 | 90 | 180 | 270); scrollXRef.current = 0; scrollYRef.current = 0; }} title="Rotate right (R)"><RmIcon d={RM.rotcw} className="seg-ic" /><span>{rotation ? `${rotation}°` : "Rotate ⟳"}</span></button>
                      <button type="button" className={mirror ? "on" : ""} onClick={() => { setMirror((prev) => !prev); scrollXRef.current = 0; scrollYRef.current = 0; }} title="Mirror horizontally (H)"><RmIcon d={RM.mirror} className="seg-ic" /><span>Mirror</span></button>
                    </div>
                  )}
                  <div className="rmx-toggle" style={{ cursor: "default" }}>
                    <span className="grow">
                      <span className="tt">Page number offset</span>
                      <span className="ts" style={{ display: "block" }}>{pageOffset === 0 ? "Match the book's printed page numbers" : `Readout shifted by ${pageOffset > 0 ? "+" : ""}${pageOffset} · this book only`}</span>
                    </span>
                    <span className="rmx-seg" style={{ flex: "none" }}>
                      <button type="button" aria-label="Decrease page offset" onClick={() => { const next = pageOffset - 1; setPageOffset(next); savePageOffset(itemId, next); }}><span>−</span></button>
                      <button type="button" aria-label="Reset page offset" title="Reset to 0" disabled={pageOffset === 0} onClick={() => { setPageOffset(0); savePageOffset(itemId, 0); }}><span style={{ minWidth: 28, textAlign: "center" }}>{pageOffset > 0 ? `+${pageOffset}` : pageOffset}</span></button>
                      <button type="button" aria-label="Increase page offset" onClick={() => { const next = pageOffset + 1; setPageOffset(next); savePageOffset(itemId, next); }}><span>+</span></button>
                    </span>
                  </div>
                </div>
              </details>
            </div>

            <div className="rmx-stack" style={{ gap: isCompact ? 12 : 18 }}>
              <LibraryPills itemId={itemId} isMarked={isMarked} isWantToRead={isWantToRead} onToggleMarked={onToggleMarked} onToggleWantToRead={onToggleWantToRead} testIdPrefix="reader" />
              <div>
                <div className="rmx-label">This session</div>
                <div className="rmx-row">
                  <button type="button" className="rmx-btn" style={{ flex: 1 }} onClick={goToPagePrompt} data-reader-control><RmIcon d={RM.target} /> Go to page</button>
                  <button type="button" className="rmx-btn" style={{ flex: 1 }} data-reader-control onClick={toggleFullscreen}><RmIcon d={RM.expand} /> Fullscreen</button>
                </div>
                {onOpenItem && (prevItem || nextItem) && (
                  <div className="rmx-row" style={{ marginTop: 8 }}>
                    {prevItem && <button type="button" className="rmx-btn" style={{ flex: 1 }} data-reader-control title={`Previous: ${prevItem.title ?? ""}`} onClick={() => onOpenItem(prevItem.id)}><RmIcon d={RM.left} /> Prev book</button>}
                    {nextItem && <button type="button" className="rmx-btn" style={{ flex: 1 }} data-reader-control title={`Next: ${nextItem.title ?? ""}`} onClick={() => onOpenItem(nextItem.id)}>Next book <RmIcon d={RM.right} /></button>}
                  </div>
                )}
                <button type="button" className="rmx-btn ghost-danger wide" style={{ marginTop: 8 }} data-reader-control onClick={onClose}><RmIcon d={RM.door} /> Close book</button>
              </div>

              <details className="rmx-diag">
                <summary>Diagnostics</summary>
                <div className="rmx-diag-body">
                  <div className="rmx-row" style={{ marginBottom: 10 }}>
                    <button type="button" className="rmx-btn rmx-btn-sm" onClick={runDebug} data-reader-control>Debug info</button>
                    <button type="button" className="rmx-btn rmx-btn-sm" onClick={() => drawCanvas()} data-reader-control>Force redraw</button>
                  </div>
                  <button type="button" className={`rmx-toggle${showBubbleDebug ? " on" : ""}`} style={{ padding: "8px 4px" }} onClick={() => setShowBubbleDebug((v) => !v)}>
                    <span className="grow"><span className="tt" style={{ fontSize: 13 }}>Bubble Zoom debug</span></span>
                    <span className="rmx-switch" />
                  </button>
                  {debugInfo && (
                    <div style={{ marginTop: 10 }}>
                      <div className="rmx-debug-head">
                        <span className="rmx-debug-label">Debug output</span>
                        <span className="rmx-row">
                          <button type="button" className="rmx-linkbtn" onClick={() => navigator.clipboard?.writeText(debugInfo)}>Copy</button>
                          <button type="button" className="rmx-linkbtn" onClick={() => setDebugInfo(null)}>Clear</button>
                        </span>
                      </div>
                      <pre className="rmx-debug-pre">{debugInfo}</pre>
                    </div>
                  )}
                </div>
              </details>
            </div>
          </div>

          <MenuFooter compact={isCompact} keys={webtoon ? [
            ["↑ ↓", "Scroll up / down"], ["space", "Scroll down"], ["← →", "Previous / next page"], ["g", "Go to page"], ["m", "Toggle menu"], ["f", "Fullscreen"], ["esc", "Close reader"],
          ] : [
            ["← →", "Previous / next page"], ["space", "Scroll or next page"], ["g", "Go to page"], ["m", "Toggle menu"], ["f", "Fullscreen"], ["+  −", "Zoom in / out"], ["0", "Reset zoom"], ["r", "Rotate (⇧ left)"], ["h", "Mirror"], ["esc", "Close reader"],
          ]} />
        </MenuShell>
      )}

      {bubbleZoom && (() => {
        const regions = textRegionCacheRef.current.get(bubbleZoom.pageIndex);
        const region = regions?.[bubbleZoom.regionIdx];
        if (!region) return null;
        const slot = drawTransformRef.current?.find((s) => s.pageIndex === bubbleZoom.pageIndex);
        return (
          <>
            <BubbleZoomLoupe key={`${bubbleZoom.pageIndex}:${bubbleZoom.regionIdx}`} img={imageCacheRef.current.get(bubbleZoom.pageIndex)} region={region} slot={slot} anchor={bubbleAnchor} onAnchorChange={setBubbleAnchor} onDismiss={() => setBubbleZoom(null)} />
            {showBubbleDebug && slot && <BubbleDebug region={region} slot={slot} index={bubbleZoom.regionIdx} total={regions!.length} tapX={bubbleZoom.tapX} tapY={bubbleZoom.tapY} />}
          </>
        );
      })()}

      {!showMenu && onOpenItem && ((onFirstPage && prevItem) || (onLastPage && nextItem)) && (
        <div className="rdr-pills">
          {onFirstPage && prevItem && <ReadingOrderPill dir="prev" title={prevItem.title ?? prevItem.fileName} onPick={() => onOpenItem(prevItem.id)} />}
          {onLastPage && nextItem && <ReadingOrderPill dir="next" title={nextItem.title ?? nextItem.fileName} onPick={() => onOpenItem(nextItem.id)} />}
        </div>
      )}

      {!showMenu && pageCount > 0 && (
        <div className="rdr-badge" style={{ opacity: badgeCovered && !badgePeek ? 0 : 1 }} data-testid="reader-badge">{chapterLabel}</div>
      )}

      {!showMenu && webtoon && webtoonAutoNotice && (
        <div className="rdr-note"><RmIcon d={RM.webtoon} />Webtoon mode · vertical scroll</div>
      )}
    </div>
  );
}
