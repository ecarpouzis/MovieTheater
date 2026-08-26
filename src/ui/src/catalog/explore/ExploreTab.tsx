/**
 * A section's Explore tab: the spotlight hero, then one row per rail (a strip, a wall, a grid), each
 * with its "More →" when the section can map the rail's browse href onto one of its own URLs, and a
 * Shuffle when the payload is seeded. The section owns the query (its keys, its staleness, its
 * invalidation); this only draws what it is handed and reports the clicks.
 */
import type { ReactNode } from "react";
import { useHistory } from "react-router-dom";
import type { CardGroup, CardItem, ExploreResponse } from "../types";
import CardGrid from "./CardGrid";
import CardRow from "./CardRow";
import CoverWall from "./CoverWall";
import type { HeroDetail } from "./HeroSpotlight";
import HeroSpotlight from "./HeroSpotlight";
import { groupOf, isGroupCard } from "./mapExplore";
import RowHead from "./RowHead";
import "./explore.css";

export interface ExploreTabProps {
  data?: ExploreResponse | null;
  loading?: boolean;
  error?: unknown;
  /** Ask for a re-roll: the tab hands back a fresh random seed. */
  onSeed?: (seed: number) => void;
  onOpen: (item: CardItem) => void;
  /** A group card (a series, an artist); when absent the card opens like an item. */
  onOpenGroup?: (group: CardGroup, groupBy: string) => void;
  /** The rail's browse href → the section's own URL; null/undefined hides the More link. */
  moreHref?: (href: string, rail: ExploreResponse["rails"][number]) => string | null | undefined;
  /** Rails that should not offer Shuffle (the genuinely-newest arrivals). */
  unseededRails?: ReadonlySet<string>;
  heroIntervalMs?: number;
  heroDetail?: (item: CardItem) => HeroDetail | null | undefined;
  heroEyebrow?: string;
  /** Per-rail subtitle line under the title. */
  railSubtitle?: (rail: ExploreResponse["rails"][number]) => string | undefined;
  emptyMessage?: ReactNode;
  className?: string;
}

export function randomSeed(): number {
  return Math.floor(Math.random() * 1_000_000) + 1;
}

export default function ExploreTab(p: ExploreTabProps) {
  const history = useHistory();
  const open = (item: CardItem) => {
    if (p.onOpenGroup && isGroupCard(item)) p.onOpenGroup(groupOf(item), item.kind);
    else p.onOpen(item);
  };
  const shuffle = p.onSeed ? () => p.onSeed!(randomSeed()) : undefined;

  if (p.error) {
    return <div className={`xp${p.className ? ` ${p.className}` : ""}`}><div className="xp-note" role="alert">Explore could not load right now.</div></div>;
  }
  if (!p.data) {
    return <div className={`xp${p.className ? ` ${p.className}` : ""}`} aria-busy="true"><div className="xp-hero xp-hero-skel" /><div className="xp-note">Loading…</div></div>;
  }
  const rails = p.data.rails.filter((r) => r.items.length > 0);
  if (p.data.spotlight.length === 0 && rails.length === 0) {
    return <div className={`xp${p.className ? ` ${p.className}` : ""}`}><div className="xp-note">{p.emptyMessage ?? "Nothing to explore yet."}</div></div>;
  }
  return (
    <div className={`xp${p.className ? ` ${p.className}` : ""}`} data-loading={p.loading || undefined}>
      {p.data.spotlight.length > 0 && (
        <HeroSpotlight items={p.data.spotlight} onOpen={open} intervalMs={p.heroIntervalMs} detail={p.heroDetail} eyebrow={p.heroEyebrow} />
      )}
      {rails.map((rail) => {
        const href = rail.more && p.moreHref ? p.moreHref(rail.more.href, rail) : null;
        const seeded = shuffle && !(p.unseededRails?.has(rail.key));
        const action = (seeded || href) ? (
          <>
            {seeded && <button type="button" className="xp-row-action" onClick={shuffle}>Shuffle ↻</button>}
            {href && <button type="button" className="xp-row-action" onClick={() => history.push(href)}>More →</button>}
          </>
        ) : undefined;
        return (
          <section key={rail.key} className={`xp-row xp-row-${rail.kind}`} data-rail={rail.key}>
            <RowHead title={rail.title} subtitle={p.railSubtitle?.(rail)} action={action} />
            {rail.kind === "wall" && <CoverWall items={rail.items} onOpen={open} />}
            {rail.kind === "grid" && <CardGrid items={rail.items} onOpen={open} />}
            {rail.kind === "strip" && <CardRow items={rail.items} onOpen={open} />}
          </section>
        );
      })}
    </div>
  );
}
