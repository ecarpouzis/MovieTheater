import { useEffect, useRef, useState, type CSSProperties } from "react";
import CardImage from "../cards/CardImage";
import type { CardItem } from "../types";
import ScoreBadge from "./ScoreBadge";

/**
 * The marquee: the feature large, lit by its OWN artwork (the cover, enlarged and blurred behind a
 * scrim — so the colour of the panel is the colour of the thing, never a hue guessed from its title),
 * with the rest of the rotation listed beside it as "Up next". The queue is the carousel's control:
 * a click promotes a pick, and a hairline under the active row shows the auto-advance running.
 * Hover pauses it; `prefers-reduced-motion` stops it (CSS hides the timer, the interval still pauses
 * on hover — a reader can always pick by hand).
 *
 * `detail` lets the section add what a card does not carry (a synopsis, a few tags) without the hero
 * knowing the section; `onActive` tells the section which pick is up, so it can fetch that detail
 * for ONE title instead of all of them.
 */
export interface HeroDetail {
  synopsis?: string | null;
  tags?: string[];
  /** Replaces the card's own meta line ("1994 · 12 issues") when the section has a better one. */
  meta?: string[];
  eyebrow?: string | null;
  /** Overrides for the headline and its byline (Books shows the SERIES as the headline, the publisher beside the eyebrow). */
  title?: string | null;
  subtitle?: string | null;
}

export interface HeroSpotlightProps {
  items: CardItem[];
  onOpen: (item: CardItem) => void;
  intervalMs?: number;
  detail?: (item: CardItem) => HeroDetail | null | undefined;
  eyebrow?: string;
  cta?: string;
  onActive?: (item: CardItem) => void;
  /** A second action beside the CTA (the page's Shuffle). */
  secondary?: { label: string; onClick: () => void };
}

function metaOf(cur: CardItem, d: HeroDetail | undefined): string[] {
  if (d?.meta) return d.meta;
  return [cur.label, ...(cur.badges?.filter((b) => b.tone !== "rating").map((b) => b.label) ?? [])].filter(Boolean) as string[];
}

export default function HeroSpotlight({ items, onOpen, intervalMs = 9000, detail, eyebrow = "Spotlight", cta = "Open", onActive, secondary }: HeroSpotlightProps) {
  const [idx, setIdx] = useState(0);
  const [paused, setPaused] = useState(false);
  const total = items.length;
  const firstKey = items[0]?.key;
  // A new roll (a different first pick) starts the marquee over.
  useEffect(() => { setIdx(0); }, [total, firstKey]);
  // The advance IS the timer bar's CSS animation ending: hover pauses the animation, so the pause and
  // the bar can never disagree, and under reduced motion there is no animation and so no autoplay.
  const advance = () => setIdx((i) => (i + 1) % Math.max(1, total));

  const queueRef = useRef<HTMLDivElement>(null);
  // When the queue is a horizontal strip (tablet/phone), keep the active pick in view — by moving the
  // STRIP's own scroll, never scrollIntoView (that would scroll the page under the reader too).
  useEffect(() => {
    const q = queueRef.current;
    const on = q?.querySelector<HTMLElement>(".xp-hero-pick.on");
    if (!q || !on || q.scrollWidth <= q.clientWidth) return;
    const left = on.offsetLeft - q.offsetLeft - 8;
    if (left < q.scrollLeft || left + on.offsetWidth > q.scrollLeft + q.clientWidth) q.scrollTo({ left: Math.max(0, left), behavior: "smooth" });
  }, [idx]);
  const cur = items[idx % Math.max(1, total)];
  useEffect(() => { if (cur && onActive) onActive(cur); }, [cur, onActive]);
  if (!cur) return null;
  const d = detail?.(cur) ?? undefined;
  const meta = metaOf(cur, d);
  const synopsis = d?.synopsis ?? "";
  const title = d?.title || cur.title;
  const byline = d?.subtitle === undefined ? cur.subtitle : d.subtitle;
  const art = cur.imageUrl || cur.imageThumbUrl || "";
  const timer = { "--xp-interval": `${intervalMs}ms` } as CSSProperties;

  return (
    <section
      className="xp-hero"
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      onFocus={() => setPaused(true)}
      onBlur={() => setPaused(false)}
      aria-roledescription="carousel"
      aria-label={eyebrow}
      data-paused={paused || undefined}
    >
      {/* The backdrop is the feature's own cover, keyed so a change cross-fades rather than snaps. */}
      <div className="xp-hero-bg" aria-hidden="true">
        {art && <img key={cur.key} src={art} alt="" decoding="async" />}
      </div>
      <div className="xp-hero-scrim" aria-hidden="true" />
      {/* Warm the NEXT pick's art, so an advance swaps to a loaded cover instead of a black panel. */}
      {total > 1 && items[(idx + 1) % total]?.imageUrl && (
        <img className="xp-hero-preload" src={items[(idx + 1) % total].imageUrl} alt="" aria-hidden="true" decoding="async" />
      )}
      <div className="xp-hero-inner">
        <button type="button" className="xp-hero-cover" style={{ "--aspect": cur.aspect || 0.66 } as CSSProperties} onClick={() => onOpen(cur)} aria-label={cur.title}>
          <CardImage key={cur.key} src={cur.imageUrl} hue={cur.hue} eager />
        </button>
        <div className="xp-hero-info" key={cur.key}>
          <div className="xp-hero-eyebrow">
            <span>{d?.eyebrow ?? eyebrow}</span>
            {byline && <span className="xp-hero-pub">{byline}</span>}
          </div>
          <h1 className="xp-hero-title">{title}</h1>
          <div className="xp-hero-meta">
            <ScoreBadge score={cur.rating} onDark />
            {meta.map((m, i) => <span key={`${m}-${i}`} className="xp-hero-metaitem">{m}</span>)}
          </div>
          {d?.tags && d.tags.length > 0 && (
            <div className="xp-hero-tags">{d.tags.slice(0, 4).map((t) => <span key={t} className="xp-hero-tag">{t}</span>)}</div>
          )}
          {synopsis && <p className="xp-hero-synopsis">{synopsis}</p>}
          <div className="xp-hero-actions">
            <button type="button" className="xp-hero-cta" onClick={() => onOpen(cur)}>{cta}</button>
            {secondary && <button type="button" className="xp-hero-ghost" onClick={secondary.onClick}>{secondary.label}</button>}
          </div>
        </div>
        {total > 1 && (
          <div ref={queueRef} className="xp-hero-queue" role="tablist" aria-label="Up next">
            <div className="xp-hero-queue-head">Up next</div>
            {items.map((it, i) => (
              <button
                key={it.key}
                type="button"
                role="tab"
                aria-selected={i === idx}
                className={`xp-hero-pick${i === idx ? " on" : ""}`}
                onClick={() => setIdx(i)}
                aria-label={it.title}
                style={i === idx ? timer : undefined}
              >
                <span className="xp-hero-pick-art"><CardImage src={it.imageThumbUrl ?? it.imageUrl} hue={it.hue} /></span>
                <span className="xp-hero-pick-text">
                  <span className="xp-hero-pick-title">{it.title}</span>
                  {(() => { const sub = it.label ?? it.subtitle; return sub && sub !== it.title ? <span className="xp-hero-pick-sub">{sub}</span> : null; })()}
                </span>
                {i === idx && intervalMs > 0 && <span className="xp-hero-pick-timer" aria-hidden="true" onAnimationEnd={advance} />}
              </button>
            ))}
          </div>
        )}
      </div>
    </section>
  );
}
