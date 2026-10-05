/**
 * A horizontal row with paging arrows. A trackpad scrolls sideways on its own, but a plain mouse
 * wheel does not — without these a desktop reader with a mouse could see only the first screen of
 * every strip. Each arrow appears only when there is somewhere to go in its direction (measured on
 * scroll and on resize, one listener + one observer per row), pages by most of the visible width,
 * and is hidden on touch screens, where a swipe already does the job.
 */
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";

export default function ScrollRow({ className, children, label }: { className: string; children: ReactNode; label?: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const [edge, setEdge] = useState({ start: true, end: true });

  const measure = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    const start = el.scrollLeft <= 2;
    const end = el.scrollLeft + el.clientWidth >= el.scrollWidth - 2;
    setEdge((cur) => (cur.start === start && cur.end === end ? cur : { start, end }));
  }, []);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    measure();
    el.addEventListener("scroll", measure, { passive: true });
    const ro = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(measure);
    ro?.observe(el);
    return () => { el.removeEventListener("scroll", measure); ro?.disconnect(); };
  }, [measure]);

  const page = (dir: 1 | -1) => {
    const el = ref.current;
    if (!el) return;
    el.scrollBy({ left: dir * Math.max(200, el.clientWidth * 0.85), behavior: "smooth" });
  };

  return (
    <div className="xp-scroll">
      <div ref={ref} className={className} role={label ? "region" : undefined} aria-label={label}>{children}</div>
      {!edge.start && <button type="button" className="xp-scroll-btn xp-scroll-prev" onClick={() => page(-1)} aria-label="Scroll back"><span aria-hidden="true">‹</span></button>}
      {!edge.end && <button type="button" className="xp-scroll-btn xp-scroll-next" onClick={() => page(1)} aria-label="Scroll forward"><span aria-hidden="true">›</span></button>}
    </div>
  );
}
