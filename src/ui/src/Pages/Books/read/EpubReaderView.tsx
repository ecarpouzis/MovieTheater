/**
 * The EPUB reader — the standalone's `EpubReaderView`: reflowable text paginated into CSS columns
 * inside a same-origin iframe (one page stride = one viewport width), fixed-layout comic EPUBs
 * scaled from their declared viewport with an optional two-page spread, in-book links followed
 * by hit-testing the iframe under a tap, a TOC drawer, the Command Deck menu with type/theme
 * controls. Resources (images, fonts, stylesheets) the chapter references are rewritten onto the
 * media plane (`epubResourceUrl`); the chapter HTML itself comes through the API.
 */
import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from "react";
import { fetchEpubChapterHtml, fetchEpubSpine, fetchEpubToc, type EpubSpine, type EpubTocEntry, type ItemDetail } from "../booksApi";
import { epubResourceUrl } from "../booksMedia";
import type { KidStyle } from "../KidsHome";
import LibraryPills from "../LibraryPills";
import { EPUB_FONTS, EPUB_LINE_HEIGHTS, EPUB_MEASURE, EPUB_THEMES, FONT_SCALE_MAX, FONT_SCALE_MIN, FONT_SCALE_STEP, loadEpubPrefs, saveEpubPrefs, type EpubFont, type EpubMargin, type EpubPrefs, type EpubTheme } from "./epubPrefs";
import { MenuFooter, MenuHead, MenuShell, RM, RmIcon, Scrubber, useMenuTier } from "./ReaderMenu";
import { isSinglePageSpread, isSplitModeEnabled, loadReaderPrefs, saveReaderPrefs, snapToSpreadStart, type FitMode, type SplitMode } from "./readerPrefs";
import type useReadingPosition from "./useReadingPosition";

export interface EpubReaderViewProps {
  itemId: number;
  detail: ItemDetail;
  position: ReturnType<typeof useReadingPosition>;
  onClose: () => void;
  isMarked?: boolean;
  isWantToRead?: boolean;
  onToggleMarked?: (id: number) => void;
  onToggleWantToRead?: (id: number) => void;
  kidsStyle?: KidStyle;
}

const PAGE_PAD_X = 30;
const PAGE_PAD_Y = 34;
const EPUB_PREFETCH_AHEAD = 2;

function isExternal(href: string): boolean {
  return /^(https?:|data:|mailto:|tel:|javascript:)/i.test(href) || href.startsWith("#");
}

/** Mirrors the host's ResolveRelativeHref/NormalizeHref so resolved hrefs match what the media plane expects. */
export function resolveHref(baseHref: string, href: string): string {
  let raw = (href || "").replace(/\\/g, "/").trim();
  if (!raw) return "";
  const hash = raw.indexOf("#"); if (hash >= 0) raw = raw.slice(0, hash);
  const q = raw.indexOf("?"); if (q >= 0) raw = raw.slice(0, q);
  if (!raw) return "";
  let combined: string;
  if (raw.startsWith("/")) combined = raw.slice(1);
  else {
    const base = (baseHref || "").replace(/\\/g, "/");
    const slash = base.lastIndexOf("/");
    combined = (slash >= 0 ? base.slice(0, slash + 1) : "") + raw;
  }
  const parts: string[] = [];
  for (const seg of combined.split("/")) {
    if (!seg || seg === ".") continue;
    if (seg === "..") { parts.pop(); continue; }
    parts.push(seg);
  }
  return parts.join("/");
}

const resourceUrl = (itemId: number, href: string) => epubResourceUrl(itemId, href) ?? "";

/** The image URLs a chapter references — warmed before the reader navigates to it. */
export function collectChapterImageUrls(rawHtml: string, baseHref: string, itemId: number): string[] {
  const doc = new DOMParser().parseFromString(rawHtml, "text/html");
  const urls = new Set<string>();
  const add = (v: string | null) => {
    if (!v || isExternal(v)) return;
    const r = resolveHref(baseHref, v);
    if (r) { const u = resourceUrl(itemId, r); if (u) urls.add(u); }
  };
  doc.querySelectorAll("img[src]").forEach((el) => add(el.getAttribute("src")));
  doc.querySelectorAll("[poster]").forEach((el) => add(el.getAttribute("poster")));
  doc.querySelectorAll("image").forEach((el) => { add(el.getAttribute("href")); add(el.getAttribute("xlink:href")); });
  doc.querySelectorAll("*").forEach((el) => { if (el.tagName.toLowerCase() !== "image") add(el.getAttribute("xlink:href")); });
  return [...urls];
}

/** The document the iframe renders: assets onto the media plane, then the reader stylesheet (columns or fixed). */
export function buildSrcDoc(rawHtml: string, itemId: number, baseHref: string, mode: "reflow" | "fixed", prefs: EpubPrefs, width: number, height: number): string {
  const doc = new DOMParser().parseFromString(rawHtml, "text/html");
  const toResource = (h: string) => resourceUrl(itemId, h);
  const rewrite = (el: Element, attr: string) => {
    const v = el.getAttribute(attr);
    if (!v || isExternal(v)) return;
    const r = resolveHref(baseHref, v);
    if (r) el.setAttribute(attr, toResource(r));
  };
  doc.querySelectorAll("[src]").forEach((el) => rewrite(el, "src"));
  doc.querySelectorAll("link[href]").forEach((el) => rewrite(el, "href"));
  doc.querySelectorAll("[poster]").forEach((el) => rewrite(el, "poster"));
  doc.querySelectorAll("image").forEach((el) => {
    if (el.getAttribute("href")) rewrite(el, "href");
    const xl = el.getAttribute("xlink:href");
    if (xl && !isExternal(xl)) el.setAttribute("xlink:href", toResource(resolveHref(baseHref, xl)));
  });
  doc.querySelectorAll("*").forEach((el) => {
    if (el.tagName.toLowerCase() === "image") return;
    const xl = el.getAttribute("xlink:href");
    if (xl && !isExternal(xl)) { const r = resolveHref(baseHref, xl); if (r) el.setAttribute("xlink:href", toResource(r)); }
  });

  const headInner = doc.head ? doc.head.innerHTML : "";
  const bodyInner = doc.body ? doc.body.innerHTML : rawHtml;
  const t = EPUB_THEMES[prefs.theme];
  const bodyHasInlineBg = /background(-color)?\s*:/i.test(doc.body?.getAttribute("style") ?? "");

  let readerCss: string;
  let body: string;
  if (mode === "fixed") {
    readerCss = "html,body{margin:0!important;padding:0!important;background:transparent!important;}";
    body = bodyInner;
  } else {
    const cols = prefs.columns === 2 ? 2 : 1;
    const minGutter = PAGE_PAD_X;
    const maxMeasure = EPUB_MEASURE[prefs.margin] * prefs.fontScale;
    const slot = cols === 2 ? width / 2 : width;
    const colW = Math.round(Math.max(cols === 2 ? 120 : 160, Math.min(maxMeasure, slot - 2 * minGutter)));
    const colGap = slot - colW;
    const padX = Math.max(minGutter, Math.round(colGap / 2));
    const font = EPUB_FONTS[prefs.fontFamily];
    const fontDecl = font ? `font-family:${font};` : "";
    const fontOverride = font ? `#__rdrcol :is(p,li,blockquote,div,span,a,em,i,strong,b,h1,h2,h3,h4,h5,h6,td,th){font-family:${font}!important;}` : "";
    const bgDecl = bodyHasInlineBg ? "" : `background:${t.bg};`;
    readerCss = `
      html{margin:0;padding:0;height:${height}px;overflow:hidden;}
      body{margin:0;padding:0;height:${height}px;overflow:hidden;${bgDecl}
           color:${t.ink};${fontDecl}font-size:${Math.round(prefs.fontScale * 100)}%;line-height:${prefs.lineHeight};
           -webkit-text-size-adjust:none;text-size-adjust:none;text-rendering:optimizeLegibility;}
      #__rdrcol{box-sizing:border-box;height:${height}px;padding:${PAGE_PAD_Y}px ${padX}px;
        column-width:${colW}px;column-gap:${colGap}px;column-fill:auto;
        will-change:transform;transition:transform .18s ease-out;}
      ${fontOverride}
      #__rdrcol :is(img,svg,video,table){max-width:100%!important;max-height:${height - PAGE_PAD_Y * 2}px!important;height:auto!important;}
      #__rdrcol a{color:${t.link};}
      #__rdrcol p{orphans:2;widows:2;}`;
    body = `<div id="__rdrcol">${bodyInner}</div>`;
  }
  return `<!doctype html><html><head><meta charset="utf-8">${headInner}<style id="__rdr">${readerCss}</style></head><body>${body}</body></html>`;
}

export function parseViewport(rawHtml: string): { w: number; h: number } | null {
  const m = rawHtml.match(/<meta[^>]*name=["']viewport["'][^>]*content=["']([^"']+)["']/i);
  if (!m) return null;
  const w = /width\s*=\s*([\d.]+)/i.exec(m[1]);
  const h = /height\s*=\s*([\d.]+)/i.exec(m[1]);
  if (!w || !h) return null;
  const ww = parseFloat(w[1]); const hh = parseFloat(h[1]);
  return ww > 0 && hh > 0 ? { w: ww, h: hh } : null;
}

function anchorPageIn(doc: Document, col: HTMLElement, fragment: string, width: number, total: number): number {
  let frag = fragment;
  try { frag = decodeURIComponent(fragment); } catch { /* keep raw */ }
  if (!frag) return -1;
  let el: Element | null = doc.getElementById(frag);
  if (!el) { const named = doc.getElementsByName(frag); el = named.length ? named[0] : null; }
  if (!el) return -1;
  const x = el.getBoundingClientRect().left - col.getBoundingClientRect().left;
  return Math.max(0, Math.min(total - 1, Math.floor((x + 1) / width)));
}

interface RawChapter { raw: string; baseHref: string; viewport: { w: number; h: number } | null }

const positionKey = (spineIndex: number, scrollPercent: number) => `${spineIndex}:${scrollPercent.toFixed(3)}`;

export default function EpubReaderView({ itemId, detail, position, onClose, isMarked, isWantToRead, onToggleMarked, onToggleWantToRead, kidsStyle }: EpubReaderViewProps) {
  const [spine, setSpine] = useState<EpubSpine | null>(null);
  const [toc, setToc] = useState<EpubTocEntry[]>([]);
  const [chapterIndex, setChapterIndex] = useState(0);
  const [pageInChapter, setPageInChapter] = useState(0);
  const [pageCount, setPageCount] = useState(1);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [showMenu, setShowMenu] = useState(false);
  const [showToc, setShowToc] = useState(false);
  const [linkHover, setLinkHover] = useState(false);
  const [epubPrefs, setEpubPrefs] = useState<EpubPrefs>(() => loadEpubPrefs());
  const [fitMode, setFitMode] = useState<FitMode>("height");
  const [splitMode, setSplitMode] = useState<SplitMode>("none");
  const [coverAsPage, setCoverAsPage] = useState(true);
  const [viewport, setViewport] = useState({ w: window.innerWidth, h: window.innerHeight });
  const tier = useMenuTier();
  const isCompact = tier === "compact";
  const isTablet = tier === "tablet";

  const iframeRef = useRef<HTMLIFrameElement>(null);
  const iframeRightRef = useRef<HTMLIFrameElement>(null);
  const chapterCacheRef = useRef<Map<number, RawChapter>>(new Map());
  const targetPageRef = useRef<number | "last">(0);
  const lastSavedRef = useRef("");
  const posFracRef = useRef(0);
  const prevChapterRef = useRef<number | null>(null);
  const pendingAnchorRef = useRef<string | null>(null);
  const handleKeyRef = useRef<(e: KeyboardEvent) => void>(() => {});
  const prefetchTokenRef = useRef(0);
  const resumedRef = useRef(false);

  const fixed = spine?.fixedLayout ?? false;
  const mode: "reflow" | "fixed" = fixed ? "fixed" : "reflow";
  const title = detail.summary.title ?? detail.summary.fileName;

  // Spine + TOC, once per book.
  useEffect(() => {
    const prefs = loadReaderPrefs();
    setSplitMode(prefs.splitMode);
    setCoverAsPage(prefs.coverAsPage);
    chapterCacheRef.current.clear();
    resumedRef.current = false;
    let cancelled = false;
    (async () => {
      try {
        const s = await fetchEpubSpine(itemId);
        if (cancelled) return;
        setSpine(s);
        if (!s.fixedLayout) setFitMode("width");
        fetchEpubToc(itemId).then((t) => { if (!cancelled) setToc(t?.entries ?? []); }).catch(() => {});
      } catch {
        if (!cancelled) setError("Failed to load this book.");
      }
    })();
    return () => { cancelled = true; };
  }, [itemId]);

  // Resume once: the saved spine item + fraction, applied when both the spine and the position are known.
  useEffect(() => {
    if (resumedRef.current || !spine || !position.resume) return;
    resumedRef.current = true;
    const r = position.resume;
    if (r.spineIndex != null) {
      const idx = Math.max(0, Math.min(spine.count - 1, r.spineIndex));
      const frac = Math.max(0, Math.min(1, r.scrollPercent ?? 0));
      lastSavedRef.current = positionKey(idx, frac);
      setChapterIndex(idx);
      targetPageRef.current = frac > 0 ? frac : 0;
    }
  }, [spine, position.resume]);

  useEffect(() => {
    const onResize = () => setViewport({ w: window.innerWidth, h: window.innerHeight });
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, []);

  const ensureChapter = useCallback(async (idx: number): Promise<RawChapter | null> => {
    if (!spine || idx < 0 || idx >= spine.count) return null;
    const cached = chapterCacheRef.current.get(idx);
    if (cached) return cached;
    try {
      const html = await fetchEpubChapterHtml(itemId, idx);
      const baseHref = spine.items[idx]?.href || "";
      const rc: RawChapter = { raw: html, baseHref, viewport: parseViewport(html) };
      chapterCacheRef.current.set(idx, rc);
      return rc;
    } catch {
      return null;
    }
  }, [itemId, spine]);

  const prefetchNeighbours = useCallback((fromIdx: number) => {
    if (!spine) return;
    const last = Math.min(fromIdx + EPUB_PREFETCH_AHEAD, spine.count - 1);
    const token = ++prefetchTokenRef.current;
    const step = async (idx: number) => {
      if (token !== prefetchTokenRef.current || idx > last) return;
      const rc = await ensureChapter(idx);
      if (token !== prefetchTokenRef.current) return;
      if (rc) {
        for (const url of collectChapterImageUrls(rc.raw, rc.baseHref, itemId)) {
          const img = new Image();
          (img as { fetchPriority?: string }).fetchPriority = "low";
          img.src = url;
        }
      }
      void step(idx + 1);
    };
    void step(fromIdx + 1);
  }, [spine, ensureChapter, itemId]);

  const schedulePrefetch = useCallback((fromIdx: number) => {
    const ric = (window as Window & { requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number }).requestIdleCallback;
    if (ric) ric(() => prefetchNeighbours(fromIdx), { timeout: 1500 });
    else setTimeout(() => prefetchNeighbours(fromIdx), 250);
  }, [prefetchNeighbours]);

  const [srcDoc, setSrcDoc] = useState("");
  const [srcDocRight, setSrcDocRight] = useState("");
  const [leftViewport, setLeftViewport] = useState<{ w: number; h: number } | null>(null);
  const [rightViewport, setRightViewport] = useState<{ w: number; h: number } | null>(null);
  const spread = fixed && isSplitModeEnabled(splitMode) && !isSinglePageSpread(chapterIndex, splitMode, coverAsPage);

  useEffect(() => {
    if (!spine) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    const sameChapter = prevChapterRef.current === chapterIndex;
    prevChapterRef.current = chapterIndex;
    if (mode === "reflow" && sameChapter) {
      const f = posFracRef.current;
      targetPageRef.current = f <= 0 ? 0 : f >= 1 ? 0.999 : f;
    }
    (async () => {
      const left = await ensureChapter(chapterIndex);
      if (cancelled) return;
      if (!left) { setError("Failed to load this page."); setLoading(false); return; }
      setLeftViewport(left.viewport);
      setSrcDoc(buildSrcDoc(left.raw, itemId, left.baseHref, mode, epubPrefs, viewport.w, viewport.h));
      if (spread && chapterIndex + 1 < spine.count) {
        const right = await ensureChapter(chapterIndex + 1);
        if (cancelled) return;
        setRightViewport(right?.viewport ?? null);
        setSrcDocRight(right ? buildSrcDoc(right.raw, itemId, right.baseHref, mode, epubPrefs, viewport.w, viewport.h) : "");
      } else setSrcDocRight("");
      schedulePrefetch(chapterIndex);
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [spine, chapterIndex, mode, epubPrefs, viewport.w, viewport.h, spread]);

  const applyReflowTransform = useCallback((page: number) => {
    const doc = iframeRef.current?.contentDocument;
    const col = doc?.getElementById("__rdrcol") as HTMLElement | null;
    if (col) col.style.transform = `translateX(${-page * viewport.w}px)`;
  }, [viewport.w]);

  const measureAndPlace = useCallback(() => {
    const doc = iframeRef.current?.contentDocument;
    const col = doc?.getElementById("__rdrcol") as HTMLElement | null;
    if (!doc || !col) return;
    const total = Math.max(1, Math.round(col.scrollWidth / Math.max(1, viewport.w)));
    setPageCount(total);
    let target = 0;
    const anchor = pendingAnchorRef.current;
    pendingAnchorRef.current = null;
    const anchorPage = anchor ? anchorPageIn(doc, col, anchor, viewport.w, total) : -1;
    if (anchorPage >= 0) target = anchorPage;
    else {
      const want = targetPageRef.current;
      if (want === "last") target = total - 1;
      else if (want > 0 && want < 1) target = Math.round(want * (total - 1));
      else target = Math.max(0, Math.min(total - 1, Math.round(want)));
    }
    targetPageRef.current = 0;
    setPageInChapter(target);
    applyReflowTransform(target);
  }, [viewport.w, applyReflowTransform]);

  const fixedScale = useMemo(() => {
    if (!fixed) return 1;
    const vpW = leftViewport?.w || 717;
    const vpH = leftViewport?.h || 1109;
    if (spread) return Math.min(viewport.w / (vpW * 2), viewport.h / vpH);
    if (fitMode === "width") return viewport.w / vpW;
    if (fitMode === "original") return 1;
    if (fitMode === "auto") return Math.min(viewport.w / vpW, viewport.h / vpH);
    return viewport.h / vpH;
  }, [fixed, spread, leftViewport, fitMode, viewport]);

  const wireFrameKeys = useCallback((iframe: HTMLIFrameElement | null) => {
    const win = iframe?.contentWindow;
    if (!win) return;
    win.addEventListener("keydown", (e) => handleKeyRef.current(e as KeyboardEvent));
  }, []);

  const handleLeftLoad = useCallback(() => {
    if (mode === "reflow") measureAndPlace();
    wireFrameKeys(iframeRef.current);
    setLoading(false);
  }, [measureAndPlace, mode, wireFrameKeys]);

  useEffect(() => {
    if (mode === "reflow") {
      applyReflowTransform(pageInChapter);
      posFracRef.current = pageCount > 1 ? pageInChapter / (pageCount - 1) : 0;
    }
  }, [pageInChapter, pageCount, mode, applyReflowTransform]);

  const navigateToChapter = useCallback((idx: number, toEnd = false) => {
    if (!spine) return;
    const clamped = Math.max(0, Math.min(spine.count - 1, idx));
    targetPageRef.current = toEnd ? "last" : 0;
    setShowToc(false);
    setChapterIndex(clamped);
    setPageInChapter(0);
  }, [spine]);

  const goNext = useCallback(() => {
    if (!spine) return;
    if (mode === "reflow") {
      if (pageInChapter < pageCount - 1) { setPageInChapter((p) => p + 1); return; }
      if (chapterIndex < spine.count - 1) navigateToChapter(chapterIndex + 1);
      return;
    }
    const step = isSplitModeEnabled(splitMode) && !isSinglePageSpread(chapterIndex, splitMode, coverAsPage) ? 2 : 1;
    if (chapterIndex < spine.count - 1) navigateToChapter(snapToSpreadStart(Math.min(spine.count - 1, chapterIndex + step), splitMode, coverAsPage));
  }, [spine, mode, pageInChapter, pageCount, chapterIndex, splitMode, coverAsPage, navigateToChapter]);

  const goPrev = useCallback(() => {
    if (!spine) return;
    if (mode === "reflow") {
      if (pageInChapter > 0) { setPageInChapter((p) => p - 1); return; }
      if (chapterIndex > 0) navigateToChapter(chapterIndex - 1, true);
      return;
    }
    if (chapterIndex > 0) navigateToChapter(snapToSpreadStart(Math.max(0, chapterIndex - 2), splitMode, coverAsPage));
  }, [spine, mode, pageInChapter, chapterIndex, splitMode, coverAsPage, navigateToChapter]);

  const findSpineIndexForHref = (resolved: string): number => {
    const items = spine?.items;
    if (!items || !resolved) return -1;
    const dec = (s: string) => { try { return decodeURIComponent(s.toLowerCase()); } catch { return s.toLowerCase(); } };
    const want = dec(resolved);
    let i = items.findIndex((it) => dec(it.href || "") === want);
    if (i >= 0) return i;
    i = items.findIndex((it) => { const h = dec(it.href || ""); return h.endsWith("/" + want) || want.endsWith("/" + h); });
    if (i >= 0) return i;
    const leaf = want.split("/").pop() || "";
    const leafIdxs: number[] = [];
    if (leaf) items.forEach((it, j) => { if ((dec(it.href || "").split("/").pop() || "") === leaf) leafIdxs.push(j); });
    return leafIdxs.length === 1 ? leafIdxs[0] : -1;
  };

  const scrollToAnchor = (fragment: string) => {
    if (mode !== "reflow") return;
    const doc = iframeRef.current?.contentDocument;
    const col = doc?.getElementById("__rdrcol") as HTMLElement | null;
    if (!doc || !col) return;
    const p = anchorPageIn(doc, col, fragment, viewport.w, pageCount);
    if (p >= 0) setPageInChapter(p);
  };

  const followLink = (rawHref: string, baseHref: string) => {
    const href = rawHref.trim();
    if (!href) return;
    if (/^(https?:|mailto:|tel:)/i.test(href)) {
      const a = document.createElement("a");
      a.href = href; a.target = "_blank"; a.rel = "noopener noreferrer";
      a.click();
      return;
    }
    if (/^(data:|javascript:|blob:|about:)/i.test(href)) return;
    const hashIdx = href.indexOf("#");
    const pathPart = hashIdx >= 0 ? href.slice(0, hashIdx) : href;
    const fragment = hashIdx >= 0 ? href.slice(hashIdx + 1) : "";
    if (!pathPart) { scrollToAnchor(fragment); return; }
    const idx = findSpineIndexForHref(resolveHref(baseHref, pathPart));
    if (idx < 0) return;
    if (idx === chapterIndex) { if (fragment) scrollToAnchor(fragment); return; }
    if (mode === "reflow" && fragment) pendingAnchorRef.current = fragment;
    navigateToChapter(idx);
  };

  const linkAt = (clientX: number, clientY: number): { href: string; baseHref: string } | null => {
    const scale = fixed ? fixedScale : 1;
    const frames: Array<{ el: HTMLIFrameElement | null; baseHref: string }> = [{ el: iframeRef.current, baseHref: spine?.items[chapterIndex]?.href || "" }];
    if (spread) frames.push({ el: iframeRightRef.current, baseHref: spine?.items[chapterIndex + 1]?.href || "" });
    for (const { el, baseHref } of frames) {
      const rect = el?.getBoundingClientRect();
      const doc = el?.contentDocument;
      if (!rect || !doc) continue;
      if (clientX < rect.left || clientX > rect.right || clientY < rect.top || clientY > rect.bottom) continue;
      const hit = doc.elementFromPoint((clientX - rect.left) / scale, (clientY - rect.top) / scale);
      const href = hit?.closest("a")?.getAttribute("href");
      if (href) return { href, baseHref };
    }
    return null;
  };

  const onZone = (action: () => void) => (e: React.MouseEvent) => {
    const hit = linkAt(e.clientX, e.clientY);
    if (hit) { followLink(hit.href, hit.baseHref); return; }
    action();
  };
  const onZoneMove = (e: React.MouseEvent) => {
    const over = !!linkAt(e.clientX, e.clientY);
    setLinkHover((prev) => (prev === over ? prev : over));
  };

  const changeFont = (delta: number) => setEpubPrefs((p) => ({ ...p, fontScale: Math.min(FONT_SCALE_MAX, Math.max(FONT_SCALE_MIN, +(p.fontScale + delta).toFixed(2))) }));
  const setTheme = (theme: EpubTheme) => setEpubPrefs((p) => ({ ...p, theme }));
  const setColumns = (columns: 1 | 2) => setEpubPrefs((p) => ({ ...p, columns }));
  const setFont = (fontFamily: EpubFont) => setEpubPrefs((p) => ({ ...p, fontFamily }));
  const setMargin = (margin: EpubMargin) => setEpubPrefs((p) => ({ ...p, margin }));
  const setLineHeight = (lineHeight: number) => setEpubPrefs((p) => ({ ...p, lineHeight }));
  const toggleFullscreen = () => {
    if (!document.fullscreenElement) document.documentElement.requestFullscreen?.();
    else document.exitFullscreen?.();
  };

  const handleKey = useCallback((e: KeyboardEvent) => {
    const tag = (e.target as HTMLElement)?.tagName?.toLowerCase();
    if (tag === "input" || tag === "textarea" || tag === "select") return;
    switch (e.key) {
      case "Escape": e.preventDefault(); if (showToc) setShowToc(false); else if (showMenu) setShowMenu(false); else onClose(); break;
      case "ArrowRight": case "PageDown": case " ": e.preventDefault(); goNext(); break;
      case "ArrowLeft": case "PageUp": e.preventDefault(); goPrev(); break;
      case "m": case "M": e.preventDefault(); setShowMenu((v) => !v); break;
      case "t": case "T": e.preventDefault(); setShowToc((v) => !v); break;
      case "f": case "F": e.preventDefault(); toggleFullscreen(); break;
      case "+": case "=": if (mode === "reflow") { e.preventDefault(); changeFont(FONT_SCALE_STEP); } break;
      case "-": case "_": if (mode === "reflow") { e.preventDefault(); changeFont(-FONT_SCALE_STEP); } break;
      default: break;
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [showMenu, showToc, onClose, goNext, goPrev, mode]);
  handleKeyRef.current = handleKey;

  useEffect(() => {
    const fn = (e: KeyboardEvent) => handleKey(e);
    window.addEventListener("keydown", fn);
    return () => window.removeEventListener("keydown", fn);
  }, [handleKey]);

  useEffect(() => { saveEpubPrefs(epubPrefs); }, [epubPrefs]);
  useEffect(() => { saveReaderPrefs({ ...loadReaderPrefs(), splitMode, coverAsPage }); }, [splitMode, coverAsPage]);

  // The reading position: spine item + fraction, debounced; the standalone's law — reaching the end never marks Read.
  useEffect(() => {
    if (!spine || loading) return;
    const frac = mode === "reflow" && pageCount > 1 ? pageInChapter / (pageCount - 1) : 0;
    const key = positionKey(chapterIndex, frac);
    if (key === lastSavedRef.current) return;
    lastSavedRef.current = key;
    position.saveEpub(chapterIndex, frac);
  }, [spine, chapterIndex, pageInChapter, pageCount, mode, loading, position]);

  const totalPages = spine?.count ?? 1;
  const pageLabel = mode === "reflow"
    ? `Section ${chapterIndex + 1}/${totalPages} · p.${pageInChapter + 1}/${pageCount}`
    : spread ? `Pages ${chapterIndex + 1}–${Math.min(totalPages, chapterIndex + 2)} of ${totalPages}` : `Page ${chapterIndex + 1} of ${totalPages}`;
  const progressPct = Math.round(((chapterIndex + (mode === "reflow" ? pageInChapter / Math.max(1, pageCount) : 0)) / Math.max(1, totalPages)) * 100);

  const epubScrubRef = useRef<HTMLDivElement>(null);
  const seekEpubScrub = (clientX: number) => {
    const r = epubScrubRef.current?.getBoundingClientRect();
    if (!r || r.width <= 0 || totalPages <= 1) return;
    const f = Math.min(1, Math.max(0, (clientX - r.left) / r.width));
    navigateToChapter(Math.round(f * (totalPages - 1)));
  };
  const onEpubScrubDown = (e: React.PointerEvent) => {
    e.preventDefault();
    seekEpubScrub(e.clientX);
    const mv = (ev: PointerEvent) => seekEpubScrub(ev.clientX);
    const up = () => { window.removeEventListener("pointermove", mv); window.removeEventListener("pointerup", up); };
    window.addEventListener("pointermove", mv);
    window.addEventListener("pointerup", up);
  };

  const chapStrip = useMemo(() => toc.filter((e) => e.depth === 0), [toc]);

  const renderFixedPage = (ref: RefObject<HTMLIFrameElement>, doc: string, vp: { w: number; h: number } | null, testid: string, onLoad?: () => void) => {
    const vpW = vp?.w || 717, vpH = vp?.h || 1109;
    const w = Math.max(1, Math.round(vpW * fixedScale)), h = Math.max(1, Math.round(vpH * fixedScale));
    return (
      <div className="rdr-epub-fixed" style={{ width: w, height: h }}>
        <iframe ref={ref} title={testid} data-testid={testid} sandbox="allow-same-origin" srcDoc={doc} onLoad={onLoad} style={{ width: vpW, height: vpH, transform: `scale(${fixedScale})` }} />
      </div>
    );
  };

  const zoneCursor = (c: string): React.CSSProperties => ({ cursor: showMenu ? "default" : linkHover ? "pointer" : c });
  const theme = EPUB_THEMES[epubPrefs.theme];

  return (
    <div className="rdr-root rdr-epub-root" style={{ background: fixed ? "#000" : theme.bg }} data-testid="reader-epub-root">
      <div className="rdr-epub-stage">
        {fixed ? (
          <div className="rdr-epub-row">
            {renderFixedPage(iframeRef, srcDoc, leftViewport, "epub-frame", handleLeftLoad)}
            {spread && srcDocRight && renderFixedPage(iframeRightRef, srcDocRight, rightViewport, "epub-frame-right", () => wireFrameKeys(iframeRightRef.current))}
          </div>
        ) : (
          <iframe ref={iframeRef} title="epub-page" data-testid="epub-frame" sandbox="allow-same-origin" srcDoc={srcDoc} onLoad={handleLeftLoad} className="rdr-epub-frame" style={{ background: theme.bg }} />
        )}
      </div>

      {!showMenu && !showToc && (
        <div className="rdr-tapzones" data-testid="epub-tapzones" onMouseMove={onZoneMove} onMouseLeave={() => setLinkHover(false)}
          onWheel={(e) => { if (mode === "reflow" && Math.abs(e.deltaY) >= 8) { if (e.deltaY > 0) goNext(); else goPrev(); } }}>
          <div className="rdr-zone rdr-zone-prev" style={zoneCursor("w-resize")} onClick={onZone(goPrev)} data-testid="epub-zone-prev" />
          <div className="rdr-zone rdr-zone-menu" style={zoneCursor("pointer")} onClick={onZone(() => setShowMenu(true))} data-testid="epub-zone-menu" />
          <div className="rdr-zone rdr-zone-next" style={zoneCursor("e-resize")} onClick={onZone(goNext)} data-testid="epub-zone-next" />
        </div>
      )}

      {loading && !error && <div className="rdr-center rdr-passthrough"><div className="rdr-spinner" /></div>}
      {error && <div className="rdr-center"><div className="rdr-error rdr-error-box">{error}</div></div>}

      {!showMenu && !showToc && <div className="rdr-badge" data-testid="epub-pill">{pageLabel}</div>}

      {showToc && (
        <div className="rdr-toc" onClick={() => setShowToc(false)}>
          <div className="rdr-toc-panel" onClick={(e) => e.stopPropagation()} data-testid="epub-toc" role="dialog" aria-label="Contents">
            <div className="rdr-toc-head">
              <div className="rdr-toc-title">Contents</div>
              <button type="button" className="rdr-toc-close" onClick={() => setShowToc(false)} aria-label="Close contents">✕</button>
            </div>
            {toc.length === 0 && <div className="rdr-toc-empty">No table of contents.</div>}
            {toc.map((e, i) => (
              <button type="button" key={i} disabled={e.spineIndex < 0} onClick={() => e.spineIndex >= 0 && navigateToChapter(e.spineIndex)} className={`rdr-toc-entry${e.spineIndex === chapterIndex ? " on" : ""}`} style={{ paddingLeft: 8 + e.depth * 16 }} data-testid="epub-toc-entry">
                {e.label}
              </button>
            ))}
          </div>
        </div>
      )}

      {showMenu && (
        <MenuShell tier={tier} kidsStyle={kidsStyle} onClose={() => setShowMenu(false)} maxWidth={880} zIndex={11}>
          <MenuHead eyebrow=".epub · Books" title={title} now={chapterIndex + 1} total={totalPages} pct={progressPct} compact={isCompact} onClose={() => setShowMenu(false)} />
          <div style={{ height: isCompact ? 12 : 24 }} />
          <Scrubber label={pageLabel} totalLabel={`${totalPages} sections`} progress={progressPct} trackRef={epubScrubRef} onPointerDown={onEpubScrubDown} onPrev={goPrev} onNext={goNext} />

          {!isCompact && !isTablet && chapStrip.length > 0 && (
            <>
              <div style={{ height: 14 }} />
              <div className="rmx-label" style={{ marginBottom: 9 }}>Contents <span className="hint">{chapStrip.length} chapters</span></div>
              <div className="rmx-strip">
                {chapStrip.map((entry, i) => (
                  <button type="button" key={i} className={`rmx-chap${entry.spineIndex === chapterIndex ? " on" : ""}`} onClick={() => entry.spineIndex >= 0 && navigateToChapter(entry.spineIndex)} data-reader-control data-testid="epub-toc-chip">
                    <span className="cn">{entry.label}</span>
                    <span className="ct">§{entry.spineIndex + 1}</span>
                  </button>
                ))}
              </div>
            </>
          )}

          <hr className="rmx-div" style={{ margin: isCompact ? "12px 0" : "18px 0" }} />

          <div className="rmx-cols" style={{ gridTemplateColumns: isCompact ? "1fr" : "1.12fr 1fr" }}>
            <div className="rmx-card">
              {mode === "reflow" ? (
                <>
                  <div className="rmx-label">Type &amp; layout</div>
                  <div className="rmx-stack" style={{ gap: 14 }}>
                    <div>
                      <div className="rmx-fieldl">Text size</div>
                      <div className="rmx-stepper">
                        <button type="button" style={{ fontSize: 13 }} onClick={() => changeFont(-FONT_SCALE_STEP)} aria-label="Smaller" data-testid="epub-font-smaller" data-reader-control>A−</button>
                        <div className="val">{Math.round(epubPrefs.fontScale * 100)}%<small>text</small></div>
                        <button type="button" style={{ fontSize: 19 }} onClick={() => changeFont(FONT_SCALE_STEP)} aria-label="Larger" data-testid="epub-font-larger" data-reader-control>A+</button>
                      </div>
                    </div>
                    <div>
                      <div className="rmx-fieldl">Font</div>
                      <div className="rmx-seg fill">
                        {(["original", "serif", "sans"] as EpubFont[]).map((f) => (
                          <button type="button" key={f} className={epubPrefs.fontFamily === f ? "on" : ""} onClick={() => setFont(f)} data-testid={`epub-font-${f}`} data-reader-control><span>{f === "original" ? "Original" : f === "serif" ? "Serif" : "Sans"}</span></button>
                        ))}
                      </div>
                    </div>
                    <div>
                      <div className="rmx-fieldl">Line spacing</div>
                      <div className="rmx-seg fill">
                        {EPUB_LINE_HEIGHTS.map(([label, lh]) => (
                          <button type="button" key={label} className={Math.abs(epubPrefs.lineHeight - lh) < 0.05 ? "on" : ""} onClick={() => setLineHeight(lh)} data-testid={`epub-line-${label.toLowerCase()}`} data-reader-control><span>{label}</span></button>
                        ))}
                      </div>
                    </div>
                    <div>
                      <div className="rmx-fieldl">Margins</div>
                      <div className="rmx-seg fill">
                        {(["narrow", "normal", "wide"] as EpubMargin[]).map((mg) => (
                          <button type="button" key={mg} className={epubPrefs.margin === mg ? "on" : ""} onClick={() => setMargin(mg)} data-testid={`epub-margin-${mg}`} data-reader-control><span>{mg[0].toUpperCase() + mg.slice(1)}</span></button>
                        ))}
                      </div>
                    </div>
                    <div>
                      <div className="rmx-fieldl">Columns</div>
                      <div className="rmx-seg fill">
                        {([1, 2] as (1 | 2)[]).map((c) => (
                          <button type="button" key={c} className={epubPrefs.columns === c ? "on" : ""} onClick={() => setColumns(c)} data-reader-control><span>{c === 1 ? "Single" : "Two-up"}</span></button>
                        ))}
                      </div>
                    </div>
                  </div>
                </>
              ) : (
                <>
                  <div className="rmx-label">Reading</div>
                  <div className="rmx-stack" style={{ gap: 10 }}>
                    <div className="rmx-seg fill">
                      {([{ v: "width", label: "Fit width" }, { v: "height", label: "Fit height" }, { v: "original", label: "Zoom" }] as { v: FitMode; label: string }[]).map(({ v, label }) => (
                        <button type="button" key={v} className={fitMode === v ? "on" : ""} onClick={() => setFitMode(v)} data-reader-control><span>{label}</span></button>
                      ))}
                    </div>
                    <div className="rmx-seg fill">
                      {([{ v: "none", label: "Single" }, { v: "l2r", label: "L → R" }, { v: "r2l", label: "R → L" }] as { v: SplitMode; label: string }[]).map(({ v, label }) => (
                        <button type="button" key={v} className={splitMode === v ? "on" : ""} onClick={() => setSplitMode(v)} data-reader-control><span>{label}</span></button>
                      ))}
                    </div>
                    {isSplitModeEnabled(splitMode) && (
                      <button type="button" className={`rmx-toggle${coverAsPage ? " on" : ""}`} onClick={() => setCoverAsPage((v) => !v)}>
                        <span className="grow"><span className="tt">Cover as single page</span></span>
                        <span className="rmx-switch" />
                      </button>
                    )}
                  </div>
                </>
              )}
            </div>

            <div className="rmx-stack" style={{ gap: isCompact ? 12 : 18 }}>
              {mode === "reflow" && (
                <div>
                  <div className="rmx-label">Theme</div>
                  <div className="rmx-theme">
                    {(["light", "sepia", "dark"] as EpubTheme[]).map((th) => (
                      <button type="button" key={th} className={`rmx-swatch${epubPrefs.theme === th ? " on" : ""}`} onClick={() => setTheme(th)} data-reader-control>
                        <span className="chip" style={{ background: EPUB_THEMES[th].bg, color: EPUB_THEMES[th].ink }}>Aa</span>
                        <span className="lbl">{th[0].toUpperCase() + th.slice(1)}</span>
                      </button>
                    ))}
                  </div>
                </div>
              )}
              <LibraryPills itemId={itemId} isMarked={isMarked} isWantToRead={isWantToRead} onToggleMarked={onToggleMarked} onToggleWantToRead={onToggleWantToRead} testIdPrefix="epub" />
              <div>
                <div className="rmx-label">This session</div>
                <div className="rmx-row">
                  <button type="button" className="rmx-btn" style={{ flex: 1 }} onClick={() => { setShowMenu(false); setShowToc(true); }} data-testid="epub-toc-btn" data-reader-control><RmIcon d={RM.list} /> Contents</button>
                  <button type="button" className="rmx-btn" style={{ flex: 1 }} onClick={toggleFullscreen} data-testid="epub-fullscreen" data-reader-control><RmIcon d={RM.expand} /> Fullscreen</button>
                </div>
                <button type="button" className="rmx-btn ghost-danger wide" style={{ marginTop: 8 }} onClick={onClose} data-reader-control><RmIcon d={RM.door} /> Close book</button>
              </div>
            </div>
          </div>

          <MenuFooter compact={isCompact} keys={[["← →", "Turn page"], ["space", "Next page"], ["t", "Contents"], ["m", "Toggle menu"], ["f", "Fullscreen"], ["esc", "Close book"]]} />
        </MenuShell>
      )}
    </div>
  );
}
