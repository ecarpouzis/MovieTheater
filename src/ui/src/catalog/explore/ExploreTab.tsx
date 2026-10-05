/**
 * A section's Explore tab: the marquee, then one module per rail. A rail's `kind` picks its SHAPE
 * (a strip, a resume row, a clamped wall or grid, a ranked ten, a focus plate, three columns, the
 * doors) — see `ExploreRailKind` in `../types` for what each is for. Each module carries "See all"
 * when the section can map the rail's browse href onto one of its own URLs, and "Shuffle" when the
 * payload is seeded and the rail is not one whose point is that it is current.
 *
 * The section owns the queries (keys, staleness, invalidation); this only draws what it is handed and
 * reports the clicks — including which doors axis is showing (`onAxis`), so the section can load
 * that one axis and no other.
 */
import { useState, type ReactNode } from "react";
import { useHistory } from "react-router-dom";
import type { CardGroup, CardItem, ExploreRail, ExploreRailKind, ExploreResponse } from "../types";
import CardGrid from "./CardGrid";
import CardRow from "./CardRow";
import ColumnsBlock from "./ColumnsBlock";
import { railHasContent } from "./composeExplore";
import CoverWall from "./CoverWall";
import DoorsBlock from "./DoorsBlock";
import FocusBlock from "./FocusBlock";
import type { HeroDetail } from "./HeroSpotlight";
import HeroSpotlight from "./HeroSpotlight";
import LazyRail from "./LazyRail";
import { GROUP_CARD_KINDS, groupOf, isGroupCard } from "./mapExplore";
import RankedRow from "./RankedRow";
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
  /**
   * Which card kinds stand for a GROUP in this section's vocabulary. Defaults to the host's
   * (`series` + `artist`); Movies passes `FACET_GROUP_KINDS` because its `series` cards are titles.
   * Must be a stable identity — a fresh `new Set()` in the JSX is a new prop every render.
   */
  groupKinds?: ReadonlySet<string>;
  /** The rail's browse href → the section's own URL; null/undefined hides the See all link. */
  moreHref?: (href: string, rail: ExploreRail) => string | null | undefined;
  /** Rails that should not offer Shuffle (the genuinely-newest arrivals). */
  unseededRails?: ReadonlySet<string>;
  heroIntervalMs?: number;
  heroDetail?: (item: CardItem) => HeroDetail | null | undefined;
  heroEyebrow?: string;
  /** Which spotlight pick is up — a section fetches the detail for that ONE title. */
  onHeroActive?: (item: CardItem) => void;
  /** Per-rail subtitle line under the title. */
  railSubtitle?: (rail: ExploreRail) => string | undefined;
  /** A doors tab was picked. Without it the tab keeps the choice itself. */
  onAxis?: (railKey: string, axis: string) => void;
  emptyMessage?: ReactNode;
  className?: string;
  /** How many rails mount eagerly before the rest wait for the viewport (default 2). */
  eagerRails?: number;
}

export function randomSeed(): number {
  return Math.floor(Math.random() * 1_000_000) + 1;
}

/** Reserved height per rail kind while it is still below the fold — the box the mounted rail fills. */
const RAIL_RESERVE: Record<ExploreRailKind, number> = {
  strip: 330, resume: 300, grid: 640, wall: 360, ranked: 520, focus: 330, columns: 560, doors: 470,
};

export default function ExploreTab(p: ExploreTabProps) {
  const history = useHistory();
  const [axisPick, setAxisPick] = useState<Record<string, string>>({});
  const kinds = p.groupKinds ?? GROUP_CARD_KINDS;
  const open = (item: CardItem) => {
    if (p.onOpenGroup && isGroupCard(item, kinds)) p.onOpenGroup(groupOf(item), item.kind);
    else p.onOpen(item);
  };
  const shuffle = p.onSeed ? () => p.onSeed!(randomSeed()) : undefined;
  const cls = `xp${p.className ? ` ${p.className}` : ""}`;

  if (p.error) {
    return <div className={cls}><div className="xp-note" role="alert">Explore could not load. Check the connection, then reload the page.</div></div>;
  }
  if (!p.data) {
    return <div className={cls} aria-busy="true"><div className="xp-hero xp-hero-skel skeleton-block" /><div className="xp-note">Loading…</div></div>;
  }
  const rails = p.data.rails.filter(railHasContent);
  if (p.data.spotlight.length === 0 && rails.length === 0) {
    return <div className={cls}><div className="xp-note">{p.emptyMessage ?? "Nothing to explore yet."}</div></div>;
  }
  const eager = p.eagerRails ?? 2;
  return (
    <div className={cls} data-loading={p.loading || undefined}>
      {p.data.spotlight.length > 0 && (
        <HeroSpotlight
          items={p.data.spotlight}
          onOpen={open}
          intervalMs={p.heroIntervalMs}
          detail={p.heroDetail}
          eyebrow={p.heroEyebrow}
          onActive={p.onHeroActive}
          secondary={shuffle ? { label: "Shuffle", onClick: shuffle } : undefined}
        />
      )}
      {rails.map((rail, i) => {
        const href = rail.more && p.moreHref ? p.moreHref(rail.more.href, rail) : null;
        const seeded = !!shuffle && !(p.unseededRails?.has(rail.key));
        const shuffleBtn = seeded ? <button type="button" className="xp-row-action" onClick={shuffle}>Shuffle</button> : null;
        const moreBtn = href ? <button type="button" className="xp-row-action" onClick={() => history.push(href)}>See all</button> : null;
        const action = (shuffleBtn || moreBtn) ? <>{shuffleBtn}{moreBtn}</> : undefined;
        const subtitle = p.railSubtitle?.(rail);
        let body: ReactNode;
        if (rail.kind === "focus" && rail.focus) {
          // The plate carries the title and its own See all; only Shuffle rides beside it.
          body = <FocusBlock rail={{ ...rail, more: href ? { href } : undefined }} onOpen={open} action={shuffleBtn} />;
        } else {
          // With `onAxis` the section owns the choice (it loads that axis); without it the tab does.
          const active = p.onAxis ? rail.activeAxis : (axisPick[rail.key] ?? rail.activeAxis);
          body = (
            <>
              <RowHead title={rail.title} subtitle={subtitle} action={action} />
              {rail.kind === "wall" && <CoverWall items={rail.items} onOpen={open} />}
              {rail.kind === "grid" && <CardGrid items={rail.items} onOpen={open} />}
              {(rail.kind === "strip" || rail.kind === "resume") && <CardRow items={rail.items} onOpen={open} coverH={rail.kind === "resume" ? 176 : undefined} />}
              {rail.kind === "ranked" && <RankedRow items={rail.items} onOpen={open} />}
              {rail.kind === "columns" && rail.columns && <ColumnsBlock columns={rail.columns} onOpen={open} />}
              {rail.kind === "doors" && rail.axes && (
                <DoorsBlock
                  axes={rail.axes}
                  active={active}
                  onAxis={(axis) => {
                    setAxisPick((m) => ({ ...m, [rail.key]: axis }));
                    p.onAxis?.(rail.key, axis);
                  }}
                />
              )}
            </>
          );
        }
        const section = <section className={`xp-row xp-row-${rail.kind}`} data-rail={rail.key}>{body}</section>;
        // The first rails are the first screen; the rest wait until they are approached, so a landing
        // with a dozen modules paints two modules' worth of covers on arrival, not twelve.
        return i < eager
          ? <div key={rail.key}>{section}</div>
          : <LazyRail key={rail.key} minHeight={RAIL_RESERVE[rail.kind]}>{section}</LazyRail>;
      })}
    </div>
  );
}
