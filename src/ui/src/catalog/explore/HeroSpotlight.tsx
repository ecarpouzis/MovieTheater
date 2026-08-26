import { useEffect, useState, type CSSProperties } from "react";
import CardImage from "../cards/CardImage";
import type { CardItem } from "../types";
import ScoreBadge from "./ScoreBadge";

/**
 * The spotlight carousel: one card large on a hue-derived gradient, rotating every `intervalMs`,
 * paused while the pointer is over it, with a thumb strip to pick directly. `detail` lets the section
 * add what a card does not carry (a synopsis, a few tags) without the hero knowing the section.
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
}

export default function HeroSpotlight({ items, onOpen, intervalMs = 8000, detail, eyebrow = "Spotlight", cta = "Open →" }: HeroSpotlightProps) {
  const [idx, setIdx] = useState(0);
  const [paused, setPaused] = useState(false);
  const total = items.length;
  useEffect(() => { setIdx(0); }, [total]);
  useEffect(() => {
    if (paused || total <= 1 || intervalMs <= 0) return;
    const t = setInterval(() => setIdx((i) => (i + 1) % total), intervalMs);
    return () => clearInterval(t);
  }, [paused, total, intervalMs]);

  const cur = items[idx % Math.max(1, total)];
  if (!cur) return null;
  const H = cur.hue ?? 220;
  const d = detail?.(cur) ?? undefined;
  const meta = d?.meta ?? [cur.label, ...(cur.badges?.filter((b) => b.tone !== "rating").map((b) => b.label) ?? [])].filter(Boolean) as string[];
  const bg = {
    background:
      `radial-gradient(130% 115% at 15% 16%, oklch(0.58 0.17 ${H}) 0%, transparent 56%),` +
      `radial-gradient(110% 120% at 94% 4%, oklch(0.5 0.15 ${(H + 32) % 360}) 0%, transparent 52%),` +
      `linear-gradient(135deg, oklch(0.4 0.15 ${H}) 0%, oklch(0.25 0.1 ${H}) 100%)`,
  };
  const synopsis = d?.synopsis ?? "";
  const title = d?.title || cur.title;
  const byline = d?.subtitle === undefined ? cur.subtitle : d.subtitle;
  return (
    <section className="xp-hero" onMouseEnter={() => setPaused(true)} onMouseLeave={() => setPaused(false)} aria-roledescription="carousel">
      <div className="xp-hero-bg" style={bg} />
      <div className="xp-hero-tint" />
      <div className="xp-hero-inner">
        <button type="button" className="xp-hero-cover" style={{ "--aspect": cur.aspect || 0.66 } as CSSProperties} onClick={() => onOpen(cur)} aria-label={cur.title}>
          <CardImage src={cur.imageUrl} hue={cur.hue} eager />
        </button>
        <div className="xp-hero-info">
          <div className="xp-hero-eyebrow">
            <span className="xp-hero-spot">{d?.eyebrow ?? eyebrow}</span>
            {byline && <span className="xp-hero-pub">{byline}</span>}
          </div>
          <h1 className="xp-hero-title">{title}</h1>
          <div className="xp-hero-meta">
            <ScoreBadge score={cur.rating} onDark />
            {meta.map((m, i) => (
              <span key={`${m}-${i}`} className="xp-hero-metaitem">{i > 0 && <span className="xp-hero-sep">·</span>}{m}</span>
            ))}
          </div>
          {d?.tags && d.tags.length > 0 && (
            <div className="xp-hero-tags">{d.tags.slice(0, 4).map((t) => <span key={t} className="xp-hero-tag">{t}</span>)}</div>
          )}
          {synopsis && <p className="xp-hero-synopsis">{synopsis.slice(0, 260)}{synopsis.length > 260 ? "…" : ""}</p>}
          <div className="xp-hero-actions">
            <button type="button" className="xp-hero-cta" onClick={() => onOpen(cur)}>{cta}</button>
          </div>
          {total > 1 && (
            <div className="xp-hero-thumbs" role="tablist" aria-label="Spotlight picks">
              {items.map((it, i) => (
                <button key={it.key} type="button" role="tab" aria-selected={i === idx} className={`xp-hero-thumb${i === idx ? " on" : ""}`} onClick={() => setIdx(i)} aria-label={it.title}>
                  <img src={it.imageThumbUrl ?? it.imageUrl} alt="" loading="lazy" />
                </button>
              ))}
            </div>
          )}
        </div>
      </div>
    </section>
  );
}
