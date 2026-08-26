/**
 * Webtoon (vertical-scroll) reader surface: every page in one continuously-scrolling column, the
 * way Marvel Infinity Comics / manhwa are meant to be read. Native scrolling does the heavy lifting;
 * a trip-line IntersectionObserver reports which page you are on for the readout + position.
 * The standalone's `WebtoonStrip` — the only change is that page URLs come from the reader
 * (`pageSrc`), since the media plane mints them per book.
 */
import { forwardRef, memo, useCallback, useEffect, useImperativeHandle, useLayoutEffect, useRef, useState } from "react";
import { WEBTOON_WIDTH_PX, type WebtoonWidth } from "./readerPrefs";

export interface WebtoonStripHandle {
  scrollToPage: (p: number, smooth?: boolean) => void;
  pageBy: (delta: number) => void;
  scrollViewport: (dir: 1 | -1) => void;
}

export interface WebtoonStripProps {
  pageSrc: (page: number, maxWidth?: number) => string;
  pageCount: number;
  width: WebtoonWidth;
  gap: boolean;
  scrollSignal: { page: number; t: number } | null;
  onPageChange: (page: number) => void;
  onTap: () => void;
}

/** Reserved aspect (W/H) before an image arrives — Marvel Infinity Comic panels are ~1.6 tall. */
const EST_ASPECT_WH = 1 / 1.6;

const WebtoonPage = memo(function WebtoonPage({ src, idx, gap, register }: { src: string; idx: number; gap: boolean; register: (idx: number, el: HTMLDivElement | null) => void }) {
  const [ar, setAr] = useState<number | undefined>(undefined);
  const [failed, setFailed] = useState(false);
  return (
    <div data-idx={idx} ref={(el) => register(idx, el)} className="rdr-webtoon-page" style={{ aspectRatio: String(ar ?? EST_ASPECT_WH), marginBottom: gap ? 10 : 0 }}>
      {failed ? (
        <div className="rdr-webtoon-missing">Page {idx + 1} unavailable</div>
      ) : (
        <img
          src={src}
          alt={`Page ${idx + 1}`}
          loading="lazy"
          decoding="async"
          draggable={false}
          onLoad={(e) => { const im = e.currentTarget; if (im.naturalWidth > 0 && im.naturalHeight > 0) setAr(im.naturalWidth / im.naturalHeight); }}
          onError={() => setFailed(true)}
        />
      )}
    </div>
  );
});

const WebtoonStrip = forwardRef<WebtoonStripHandle, WebtoonStripProps>(function WebtoonStrip({ pageSrc, pageCount, width, gap, scrollSignal, onPageChange, onTap }, ref) {
  const scrollerRef = useRef<HTMLDivElement>(null);
  const wrappersRef = useRef<(HTMLDivElement | null)[]>([]);
  const currentRef = useRef(0);
  const onPageChangeRef = useRef(onPageChange); onPageChangeRef.current = onPageChange;
  const onTapRef = useRef(onTap); onTapRef.current = onTap;
  const lastScrollTsRef = useRef(0);
  const cap = WEBTOON_WIDTH_PX[width];

  const register = useCallback((idx: number, el: HTMLDivElement | null) => { wrappersRef.current[idx] = el; }, []);

  const scrollToPage = useCallback((p: number, smooth = false) => {
    const sc = scrollerRef.current;
    const w = wrappersRef.current[Math.min(Math.max(0, p), pageCount - 1)];
    if (!sc || !w) return;
    const top = w.getBoundingClientRect().top - sc.getBoundingClientRect().top + sc.scrollTop;
    sc.scrollTo({ top, behavior: smooth ? "smooth" : "auto" });
  }, [pageCount]);

  const scrollViewport = useCallback((dir: 1 | -1) => {
    const sc = scrollerRef.current;
    if (!sc) return;
    sc.scrollBy({ top: dir * sc.clientHeight * 0.9, behavior: "smooth" });
  }, []);

  const pageBy = useCallback((delta: number) => { scrollToPage(currentRef.current + delta, true); }, [scrollToPage]);

  useImperativeHandle(ref, () => ({ scrollToPage, pageBy, scrollViewport }), [scrollToPage, pageBy, scrollViewport]);

  // Trip line at 33% of the viewport: whichever page crosses it is current. Fires only at hand-offs.
  useLayoutEffect(() => {
    const sc = scrollerRef.current;
    if (!sc || typeof IntersectionObserver === "undefined") return;
    const io = new IntersectionObserver((entries) => {
      for (const e of entries) {
        if (!e.isIntersecting) continue;
        const idx = Number((e.target as HTMLElement).dataset.idx);
        if (Number.isNaN(idx) || idx === currentRef.current) continue;
        currentRef.current = idx;
        onPageChangeRef.current(idx);
      }
    }, { root: sc, rootMargin: "-33% 0px -67% 0px", threshold: 0 });
    for (const el of wrappersRef.current) if (el) io.observe(el);
    return () => io.disconnect();
  }, [pageCount]);

  useEffect(() => {
    if (!scrollSignal) return;
    const raf = requestAnimationFrame(() => scrollToPage(scrollSignal.page, false));
    return () => cancelAnimationFrame(raf);
  }, [scrollSignal, scrollToPage]);

  // Request pages at the column's pixel width (DPR-aware), clamped; the server only ever scales down.
  const dpr = (typeof window !== "undefined" ? window.devicePixelRatio : 1) || 1;
  const colCss = cap ? Math.min(typeof window !== "undefined" ? window.innerWidth : cap, cap) : (typeof window !== "undefined" ? window.innerWidth : 1200);
  const reqWidth = Math.max(600, Math.min(2200, Math.ceil(colCss * dpr)));

  return (
    <div
      ref={scrollerRef}
      className="rdr-webtoon"
      onScroll={() => { lastScrollTsRef.current = Date.now(); }}
      onClick={() => { if (Date.now() - lastScrollTsRef.current > 250) onTapRef.current(); }}
    >
      <div className="rdr-webtoon-col" style={{ maxWidth: cap ?? undefined }}>
        {Array.from({ length: pageCount }, (_, i) => (
          <WebtoonPage key={i} src={pageSrc(i, reqWidth)} idx={i} gap={gap} register={register} />
        ))}
      </div>
    </div>
  );
});

export default WebtoonStrip;
