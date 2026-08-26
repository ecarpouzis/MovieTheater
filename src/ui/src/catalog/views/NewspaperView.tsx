import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import CardImage from "../cards/CardImage";
import { NO_GROUP } from "../state/useCatalogView";
import type { CardGroup, CardItem } from "../types";
import { StreamEmpty, StreamFailed, StreamLoading } from "./StreamStates";
import type { ViewProps } from "./ViewProps";
import { GROUPS_PAGE_SIZE, useGroupedStream } from "./groupedStream";

/**
 * Newspaper — the section's groups (franchises, artists, systems, decades…) splashed as a
 * broadsheet: a lead feature, then a pack of columns, streamed as an endless seeded-shuffle scroll.
 * Ported from the standalone's Newspaper layout; a "run" here is a CardGroup, so every section that
 * groups gets a front page. Already-shown bands are FROZEN while the pool grows, so appended groups
 * never reflow what the reader has passed.
 */
export const NP_COLS_PER_BAND = 5;
export const NP_MAX_BANDS = 80;
export const NEWSPAPER_PER_GROUP = 48;

export interface Run {
  key: string;
  name: string;
  items: CardItem[];
  count: number;
  minY: number;
  maxY: number;
  rating: number;
  lead: CardItem;
  synopsis: string;
  byline: string;
  kicker: string;
  tags: string[];
}

export interface Band { feature: Run; cols: Run[]; flip: boolean }

const str = (v: unknown): string => (typeof v === "string" ? v : "");

export function runFrom(group: CardGroup): Run | null {
  const items = group.items;
  if (items.length === 0) return null;
  const years = items.map((i) => i.year ?? 0).filter((y) => y > 0);
  const rated = items.map((i) => i.rating).filter((r): r is number => typeof r === "number");
  const lead = [...items].sort((a, b) => (b.rating ?? -1) - (a.rating ?? -1))[0];
  const tags = Array.isArray(group.detail?.tags) ? (group.detail!.tags as unknown[]).filter((t): t is string => typeof t === "string") : [];
  return {
    key: group.key,
    name: group.label,
    items,
    count: group.totalItems,
    minY: years.length ? Math.min(...years) : 0,
    maxY: years.length ? Math.max(...years) : 0,
    rating: rated.length ? rated.reduce((s, r) => s + r, 0) / rated.length : 0,
    lead,
    synopsis: str(group.detail?.synopsis),
    byline: str(group.detail?.byline),
    kicker: str(group.detail?.kicker),
    tags,
  };
}

/** Seeded shuffle (mulberry32) — deterministic for a seed, so revealed bands stay put. */
export function npShuffle<T>(arr: T[], seed: number): T[] {
  const a = [...arr];
  let s = seed >>> 0;
  const rnd = () => {
    s = (s + 0x6D2B79F5) | 0;
    let t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  for (let i = a.length - 1; i > 0; i -= 1) {
    const j = Math.floor(rnd() * (i + 1));
    [a[i], a[j]] = [a[j], a[i]];
  }
  return a;
}

/** Bands from repeated reshuffles of the pool; a larger `count` yields a stable prefix. */
export function npBuildBands(pool: Run[], count: number, seed: number, perBand = NP_COLS_PER_BAND): Band[] {
  const bands: Band[] = [];
  if (!pool.length) return bands;
  let epoch = 0;
  let flip = 0;
  while (bands.length < count && epoch < 100000) {
    const deck = npShuffle(pool, (seed + epoch * 7919) | 0);
    let i = 0;
    while (i < deck.length && bands.length < count) {
      const feature = deck[i]; i += 1;
      const cols = deck.slice(i, i + perBand); i += perBand;
      bands.push({ feature, cols, flip: flip % 2 === 1 });
      flip += 1;
    }
    epoch += 1;
  }
  return bands;
}

const firstSentence = (t: string): string => { if (!t) return ""; const m = t.match(/^[^.!?]*[.!?]/); return m ? m[0] : t; };

function Thumb({ item, height, onOpen, cap }: { item: CardItem; height: number; onOpen: (i: CardItem) => void; cap?: boolean }) {
  const w = Math.round(height * (item.aspect || 0.66));
  return (
    <button type="button" className="np-thumb" style={{ width: w }} title={item.label ? `${item.title} · ${item.label}` : item.title} onClick={() => onOpen(item)}>
      <div className="np-thumb-cover" style={{ height }}><CardImage src={item.imageThumbUrl ?? item.imageUrl} hue={item.hue} /></div>
      {cap && item.label && <span className="np-thumb-cap">{item.label}</span>}
    </button>
  );
}

function Feature({ run, flip, h, noun, onOpen, onOpenGroup }: { run: Run; flip: boolean; h: (n: number) => number; noun: string; onOpen: (i: CardItem) => void; onOpenGroup: ((r: Run) => void) | null }) {
  const span = run.minY > 0 ? `${run.minY}–${run.maxY}` : "";
  return (
    <article className={`np-lead${flip ? " np-lead-flip" : ""}`}>
      <div className="np-lead-text">
        <div className="np-kick">{run.kicker || "In full"}</div>
        <h2 className={`np-hl${onOpenGroup ? " bx-clickable" : ""}`} onClick={onOpenGroup ? () => onOpenGroup(run) : undefined}>{run.name}</h2>
        {run.byline && <div className="np-byline">{run.byline}</div>}
        <div className="np-stat-row">
          {span && <><span>{span}</span><i /></>}
          <span>{run.count.toLocaleString()} {run.count === 1 ? noun : `${noun}s`}</span>
          {run.rating > 0 && <><i /><span>★ {(run.rating / 10).toFixed(1)}</span></>}
        </div>
        {run.synopsis && <p className="np-lede"><span className="np-drop">{run.synopsis.charAt(0)}</span>{run.synopsis.slice(1)}</p>}
        {run.tags.length > 0 && <div className="np-tags">{run.tags.slice(0, 4).map((t) => <span key={t}>{t}</span>)}</div>}
        <div className="np-lead-strip">
          <span className="np-strip-label">The run, in order{span ? ` — ${span}` : ""}</span>
          <div className="np-strip-row">{run.items.map((b) => <Thumb key={b.key} item={b} height={h(104)} onOpen={onOpen} cap />)}</div>
        </div>
      </div>
      <figure className="np-lead-fig">
        <button type="button" className="np-fig-cover" style={{ "--aspect": run.lead.aspect || 0.66 } as CSSProperties} onClick={() => onOpen(run.lead)}>
          <CardImage src={run.lead.imageUrl} hue={run.lead.hue} eager />
        </button>
        <figcaption>Pictured · <b>{run.lead.title}</b>{run.lead.label ? ` (${run.lead.label})` : ""}.</figcaption>
        <dl className="np-fig-facts">
          {run.lead.subtitle && <div><dt>From</dt><dd>{run.lead.subtitle}</dd></div>}
          {run.minY > 0 && <div><dt>Spans</dt><dd>{run.maxY - run.minY === 0 ? "one year" : `${run.maxY - run.minY} years`}</dd></div>}
        </dl>
      </figure>
    </article>
  );
}

function Columns({ runs, h, noun, onOpen, onOpenGroup }: { runs: Run[]; h: (n: number) => number; noun: string; onOpen: (i: CardItem) => void; onOpenGroup: ((r: Run) => void) | null }) {
  return (
    <div className="np-cols">
      {runs.map((run) => {
        const lede = firstSentence(run.synopsis);
        return (
          <article key={run.key} className="np-col">
            <div className="np-kick">{run.kicker || "Also"}</div>
            <h3 className={`np-col-hl${onOpenGroup ? " bx-clickable" : ""}`} onClick={onOpenGroup ? () => onOpenGroup(run) : undefined}>{run.name}</h3>
            {run.byline && <div className="np-col-by">{run.byline}</div>}
            <div className="np-col-stat">
              {run.minY > 0 ? `${run.minY}–${run.maxY} · ` : ""}{run.count.toLocaleString()} {run.count === 1 ? noun : `${noun}s`}{run.rating > 0 ? ` · ★${(run.rating / 10).toFixed(1)}` : ""}
            </div>
            <div className="np-col-covers">{run.items.slice(0, 6).map((b) => <Thumb key={b.key} item={b} height={h(74)} onOpen={onOpen} />)}</div>
            {lede && <p className="np-col-lede">{lede}</p>}
          </article>
        );
      })}
    </div>
  );
}

export default function NewspaperView({ source, state, coverScale }: ViewProps) {
  const stream = useGroupedStream(source, state, NEWSPAPER_PER_GROUP);
  // Pool growth: later bands of groups, one at a time, as the sentinel consumes what is built.
  const [extraBands, setExtraBands] = useState<CardGroup[][]>([]);
  const nextRef = useRef(1);
  const busyRef = useRef(false);
  useEffect(() => { setExtraBands([]); nextRef.current = 1; busyRef.current = false; }, [stream.queryKey]);
  const totalBands = Math.max(1, Math.ceil(stream.totalGroups / GROUPS_PAGE_SIZE));
  const hasMore = nextRef.current < totalBands;
  const needMore = useCallback(() => {
    if (busyRef.current) return;
    const i = nextRef.current;
    if (i >= totalBands) return;
    busyRef.current = true;
    const key = stream.queryKey;
    stream.fetchBand(i, new AbortController().signal)
      .then((groups) => { if (key !== stream.queryKey) return; nextRef.current = i + 1; setExtraBands((p) => [...p, groups]); })
      .catch(() => {})
      .finally(() => { busyRef.current = false; });
  }, [stream, totalBands]);

  const runs = useMemo(() => {
    const groups = [...(stream.band0 ?? []), ...extraBands.flat()];
    return groups.map(runFrom).filter((r): r is Run => r != null)
      .sort((a, b) => (b.count - a.count) || (b.rating - a.rating) || a.name.localeCompare(b.name));
  }, [stream.band0, extraBands]);

  const [seed, setSeed] = useState(() => (Math.random() * 1e9) | 0);
  const [shown, setShown] = useState(3);
  const frozenRef = useRef<Band[]>([]);
  useEffect(() => { setSeed((Math.random() * 1e9) | 0); setShown(3); frozenRef.current = []; }, [stream.queryKey]);
  const bands = useMemo(() => {
    const frozen = frozenRef.current;
    if (frozen.length < shown) {
      const built = npBuildBands(runs, shown, seed);
      for (let i = frozen.length; i < built.length; i += 1) frozen.push(built[i]);
    }
    return frozen.slice(0, shown);
  }, [runs, shown, seed]);

  const sentinelRef = useRef<HTMLDivElement>(null);
  const atEnd = shown >= NP_MAX_BANDS;
  const needMoreRef = useRef(needMore); needMoreRef.current = needMore;
  useEffect(() => {
    if (atEnd || typeof IntersectionObserver === "undefined") return undefined;
    const sentinel = sentinelRef.current;
    if (!sentinel) return undefined;
    const io = new IntersectionObserver((entries) => {
      if (!entries.some((e) => e.isIntersecting)) return;
      setShown((v) => Math.min(v + 2, NP_MAX_BANDS));
      needMoreRef.current();
    }, { rootMargin: "900px 0px" });
    io.observe(sentinel);
    return () => io.disconnect();
  }, [shown, atEnd]);

  const noun = source.itemNoun ?? "item";
  const onOpenGroup = stream.openGroup ? (r: Run) => stream.openGroup!({ key: r.key, label: r.name, totalItems: r.count, renderTotal: r.items.length, items: r.items }) : null;

  if (state.group === NO_GROUP && source.groups.length === 0) return <StreamEmpty noun={noun} />;
  if (stream.loading && !stream.band0) return <StreamLoading />;
  if (stream.error && !stream.band0) return <StreamFailed onRetry={stream.retry} />;
  if (!runs.length) return <StreamEmpty noun={noun} />;

  const years = runs.flatMap((r) => (r.minY > 0 ? [r.minY, r.maxY] : []));
  const yLo = years.length ? Math.min(...years) : 0;
  const yHi = years.length ? Math.max(...years) : 0;
  const total = runs.reduce((s, r) => s + r.count, 0);
  const h = (n: number) => Math.round(n * coverScale);
  const groupNoun = source.groupNoun ?? "groups";
  return (
    <div className="np">
      <header className="np-mast">
        <div className="np-mast-side np-mast-l">{stream.totalGroups.toLocaleString()} {groupNoun}</div>
        <h1 className="np-flag">The {source.title ?? "Catalog"} Ledger</h1>
        <div className="np-mast-side np-mast-r">{yLo > 0 ? `${yLo}–${yHi} · ` : ""}{total.toLocaleString()} {noun}s in view</div>
      </header>
      <div className="np-dek">An endless, shuffled survey of the {groupNoun} that match your filters</div>
      <div className="np-rule-2" />
      {bands.map((band, bi) => (
        <Fragment key={bi}>
          {bi > 0 && <div className="np-band-rule" />}
          <Feature run={band.feature} flip={band.flip} h={h} noun={noun} onOpen={stream.open} onOpenGroup={onOpenGroup} />
          {band.cols.length > 0 && <><div className="np-rule-1" /><Columns runs={band.cols} h={h} noun={noun} onOpen={stream.open} onOpenGroup={onOpenGroup} /></>}
        </Fragment>
      ))}
      {!atEnd && <div className="np-sentinel" ref={sentinelRef}>{hasMore ? "Continued — more below" : "The end of the run"}</div>}
    </div>
  );
}
