/**
 * The kids landing, from `/explore/kids`: the hero series as a fan of its first issues, then one row
 * per spotlighted series — the same payload dressed two ways (Comic Pop / Bubble Gum), so the two
 * skins share one component and only the class vocabulary differs. Hearts are the real want-to-read
 * toggle; "Start reading" opens the first issue in the reader.
 */
import type { CSSProperties } from "react";
import { useState } from "react";
import CardImage from "../../catalog/cards/CardImage";
import type { CardItem, ExploreResponse } from "../../catalog/types";
import type { ItemSummary } from "./booksApi";
import { clampAspect } from "./booksFormat";

export type KidStyle = "pop" | "bubble";

const GENRE_LABEL: Record<string, string> = {
  "sci-fi": "Sci-Fi", "funny-animal": "Funny Animals", "slice-of-life": "Slice of Life",
  "coming-of-age": "Coming of Age", "epic-fantasy": "Epic Fantasy", "space-opera": "Space Opera",
};
export function prettyGenre(g?: string | null): string {
  if (!g) return "";
  const key = g.includes(":") ? g.slice(g.indexOf(":") + 1) : g;
  if (GENRE_LABEL[key]) return GENRE_LABEL[key];
  return key.split(/[-\s_]+/).map((w) => (w ? w[0].toUpperCase() + w.slice(1) : w)).join(" ");
}

const rawOf = (c: CardItem) => (c.raw ?? {}) as Partial<ItemSummary>;
const tagsOf = (c: CardItem) => (rawOf(c).tagsCsv ?? "").split(",").map((t) => t.trim()).filter(Boolean);

export function StarsInline({ rating, light }: { rating: number | null | undefined; light?: boolean }) {
  const full = Math.max(0, Math.min(5, Math.round((rating ?? 0) / 20)));
  return (
    <span className={`k-stars${light ? " light" : ""}`} aria-label={rating != null ? `${full} of 5 stars` : undefined}>
      {"★".repeat(full)}<span className="k-stars-off">{"★".repeat(5 - full)}</span>
    </span>
  );
}

export function HeartBtn({ on, onToggle }: { on: boolean; onToggle: () => void }) {
  const [popping, setPopping] = useState(false);
  return (
    <button
      type="button"
      className={`kc-heart${on ? " on" : ""}${popping ? " just-liked" : ""}`}
      onClick={(e) => { e.stopPropagation(); if (!on) { setPopping(true); setTimeout(() => setPopping(false), 440); } onToggle(); }}
      aria-pressed={on}
      aria-label={on ? "Remove from reading list" : "Save to reading list"}
    >
      <span className="glyph" aria-hidden="true" />
    </button>
  );
}

export interface KidCoverProps {
  item: CardItem;
  height: number;
  wanted: ReadonlySet<number>;
  onToggleWant: (id: number) => void;
  onOpen: (item: CardItem) => void;
}

export function KidCover({ item, height, wanted, onToggleWant, onOpen }: KidCoverProps) {
  const aspect = clampAspect(item.aspect);
  return (
    <div className="kc-art" onClick={() => onOpen(item)} role="button" tabIndex={0} onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(item); } }} aria-label={item.title}>
      <div className="kc-cover" style={{ height, width: Math.round(height * aspect) }}>
        <CardImage src={item.imageUrl} hue={item.hue} />
      </div>
      <HeartBtn on={wanted.has(item.id)} onToggle={() => onToggleWant(item.id)} />
    </div>
  );
}

const FAN_ROTATIONS = [-8, -4, 0, 4, 8];

export function SeriesFan({ issues, height, wanted, onToggleWant, onOpen }: { issues: CardItem[]; height: number; wanted: ReadonlySet<number>; onToggleWant: (id: number) => void; onOpen: (item: CardItem) => void }) {
  return (
    <div className="kh-fan">
      {issues.slice(0, 5).map((it, i) => (
        <div key={it.key} className="kh-fan-item" style={{ "--fan-rot": `${FAN_ROTATIONS[i % FAN_ROTATIONS.length]}deg` } as CSSProperties}>
          <KidCover item={it} height={height} wanted={wanted} onToggleWant={onToggleWant} onOpen={onOpen} />
        </div>
      ))}
    </div>
  );
}

export interface KidsHomeProps {
  data: ExploreResponse;
  style: KidStyle;
  wanted: ReadonlySet<number>;
  onToggleWant: (id: number) => void;
  onOpen: (item: CardItem) => void;
  onRead: (itemId: number) => void;
  onOpenShelf: (seriesId: number) => void;
}

const BUB_SECTION_BG = ["var(--mint)", "var(--lav)", "var(--peach)", "var(--sky)", "var(--lemon)"];
const BUB_TAG_BG = ["var(--sky)", "var(--bubblegum)", "var(--lemon)", "var(--mint)"];

export default function KidsHome({ data, style, wanted, onToggleWant, onOpen, onRead, onOpenShelf }: KidsHomeProps) {
  const p = style === "pop" ? "pop" : "bub";
  const hero = data.spotlight;
  const first = hero[0];
  const heroName = first ? (rawOf(first).series ?? first.title) : "";
  const heroRating = first ? rawOf(first).seriesRatingResolved ?? first.rating ?? null : null;
  const heroTags = first ? tagsOf(first).slice(0, 4) : [];
  const heroCount = first ? rawOf(first).seriesIssueCount ?? hero.length : 0;
  const rowH = style === "pop" ? 210 : 214;

  return (
    <>
      {first && (
        <section className={`${p}-hero`}>
          {style === "pop" && (
            <div className="pop-hero-fan"><SeriesFan issues={hero} height={260} wanted={wanted} onToggleWant={onToggleWant} onOpen={onOpen} /></div>
          )}
          <div className={`${p}-hero-info`}>
            <span className={style === "pop" ? "pop-eyebrow" : "bub-pickpill"}>{style === "pop" ? "★ SERIES SPOTLIGHT ★" : "⭐ Series Spotlight"}</span>
            <h1 className={`${p}-hero-title`}>{heroName}</h1>
            <div className={`${p}-hero-meta`}>
              <StarsInline rating={heroRating} light={style === "pop"} />
              {heroCount > 0 && <><span className="dot">{style === "pop" ? "●" : "•"}</span><span>{heroCount} {style === "pop" ? "ISSUES" : "issues"}</span></>}
            </div>
            {heroTags.length > 0 && (
              <div className={`${p}-tags`}>
                {heroTags.map((g, i) => <span key={g} className={`${p}-tag`} style={style === "bubble" ? { background: BUB_TAG_BG[i % BUB_TAG_BG.length] } : undefined}>{prettyGenre(g)}</span>)}
              </div>
            )}
            <button type="button" className={`${p}-cta`} onClick={() => onRead(first.id)}>{style === "pop" ? "START READING →" : "Read it now ♥"}</button>
          </div>
          {style === "bubble" && (
            <div className="bub-hero-fan"><SeriesFan issues={hero} height={260} wanted={wanted} onToggleWant={onToggleWant} onOpen={onOpen} /></div>
          )}
        </section>
      )}

      {data.rails.map((rail, si) => {
        const seriesId = /^series:(\d+)$/.exec(rail.key)?.[1];
        const genre = rail.items[0] ? tagsOf(rail.items[0])[0] : undefined;
        return (
          <section key={rail.key} className={`${p}-sec`}>
            <header className={`${p}-sec-head`}>
              {style === "pop" ? (
                <>
                  <button type="button" className="pop-sec-title" onClick={seriesId ? () => onOpenShelf(Number(seriesId)) : undefined}>{rail.title.toUpperCase()}</button>
                  <span className="pop-sec-tab">{rail.items.length} ISSUES</span>
                  <span className="pop-sec-rule" />
                </>
              ) : (
                <>
                  <button type="button" className="bub-sec-pill" style={{ background: BUB_SECTION_BG[si % BUB_SECTION_BG.length] }} onClick={seriesId ? () => onOpenShelf(Number(seriesId)) : undefined}>{rail.title}</button>
                  <span className="bub-sec-count">{rail.items.length} issues</span>
                </>
              )}
            </header>
            <div className="k-row-cards">
              {rail.items.map((it) => (
                <div className={`${p}-card`} key={it.key}>
                  <KidCover item={it} height={rowH} wanted={wanted} onToggleWant={onToggleWant} onOpen={onOpen} />
                  <div className={`${p}-card-label`}>
                    <div className={`${p}-card-title`}>{rawOf(it).series ?? it.title}</div>
                    <div className={`${p}-card-sub`}>{prettyGenre(genre) || it.label}</div>
                  </div>
                </div>
              ))}
            </div>
          </section>
        );
      })}
    </>
  );
}
