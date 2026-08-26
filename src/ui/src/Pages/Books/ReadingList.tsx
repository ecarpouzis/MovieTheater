/**
 * The series modal's smart reading list over the host's run (`/browse/series/{id}/run`): the
 * containment-aware "Reading order" — collected editions sit AT their span position as expandable
 * rows (Omnibus ⊃ Book ⊃ Volume ⊃ Issue), the issues they cover fold inside, uncovered base books stay
 * inline, editions with no known range trail at the end — and the flat "All Reading" safety net
 * (every book of the run in release order). A run with no containment computed is a plain list.
 * Done-ticks come from `/shelf/series/{id}/progress`.
 */
import { useMemo, useState, type CSSProperties } from "react";
import type { CollectionLevel, SeriesRunRow } from "./booksApi";
import { clampAspect, dateLabel } from "./booksFormat";
import { CoverThumb } from "./RelatedStrip";

const LEVEL_ORDER: Record<CollectionLevel, number> = { Issue: 0, Volume: 1, Book: 2, Omnibus: 3 };

const level = (r: SeriesRunRow) => (r.collection ? LEVEL_ORDER[r.collection.level] ?? 0 : 0);
const spanStart = (r: SeriesRunRow) => r.collection?.spanStart ?? null;
const inSpan = (ct: SeriesRunRow, p: number | null) =>
  p != null && ct.collection?.spanStart != null && ct.collection.spanEnd != null && p >= ct.collection.spanStart && p <= ct.collection.spanEnd;

export interface ReadingListProps {
  rows: SeriesRunRow[];
  total: number;
  finishedIds?: Set<number>;
  onOpen?: (row: SeriesRunRow) => void;
}

export default function ReadingList({ rows, total, finishedIds, onOpen }: ReadingListProps) {
  const hasContainment = rows.some((r) => r.collection != null);
  const primary = useMemo(() => rows.filter((r) => r.collection?.trackRole !== "Container" && r.collection?.trackRole !== "Alternate")
    .sort((a, b) => (spanStart(a) ?? 1e9) - (spanStart(b) ?? 1e9) || a.item.id - b.item.id), [rows]);
  const containers = useMemo(() => rows.filter((r) => r.collection?.trackRole === "Container")
    .sort((a, b) => level(b) - level(a) || (spanStart(a) ?? 0) - (spanStart(b) ?? 0) || a.item.id - b.item.id), [rows]);
  const levelCounts = useMemo(() => {
    const m = new Map<CollectionLevel, number>();
    for (const r of rows) if (r.collection) m.set(r.collection.level, (m.get(r.collection.level) ?? 0) + 1);
    return m;
  }, [rows]);
  const childrenById = useMemo(() => {
    const m = new Map<number, SeriesRunRow[]>();
    for (const r of rows) {
      const p = r.collection?.parentItemId;
      if (p == null) continue;
      (m.get(p) ?? m.set(p, []).get(p)!).push(r);
    }
    for (const list of m.values()) list.sort((a, b) => (spanStart(a) ?? 1e9) - (spanStart(b) ?? 1e9) || a.item.id - b.item.id);
    return m;
  }, [rows]);
  const rootContainers = useMemo(() => {
    const ids = new Set(rows.map((r) => r.item.id));
    return containers.filter((c) => c.collection?.parentItemId == null || !ids.has(c.collection.parentItemId));
  }, [containers, rows]);

  const [mode, setMode] = useState<"read" | "all">("read");
  const [open, setOpen] = useState<Set<number>>(() => new Set());
  const toggleOpen = (id: number) => setOpen((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });

  const merged = useMemo(() => {
    const spanned = rootContainers.filter((ct) => ct.collection?.spanStart != null && ct.collection.spanEnd != null && ct.collection.spanEnd >= ct.collection.spanStart && ct.collection.spanStart > 0);
    const unplaced = rootContainers.filter((ct) => !spanned.includes(ct));
    const covered = (p: number | null) => p != null && spanned.some((ct) => inSpan(ct, p));
    type Row = { kind: "container" | "book"; r: SeriesRunRow; pos: number; width: number };
    const items: Row[] = [];
    for (const ct of spanned) items.push({ kind: "container", r: ct, pos: ct.collection!.spanStart!, width: ct.collection!.spanEnd! - ct.collection!.spanStart! });
    for (const b of primary) if (!covered(spanStart(b))) items.push({ kind: "book", r: b, pos: spanStart(b) ?? Number.MAX_SAFE_INTEGER, width: 0 });
    items.sort((a, b) => a.pos - b.pos || (a.kind === b.kind ? 0 : a.kind === "container" ? -1 : 1) || b.width - a.width || a.r.item.id - b.r.item.id);
    return { items, unplaced };
  }, [rootContainers, primary]);

  const done = finishedIds ?? new Set<number>();
  const truncated = total > rows.length;

  if (!hasContainment) {
    return (
      <section className="cm-relsec">
        <div className="cm-runhead">
          <h3 className="cm-h3">Reading order</h3>
          {truncated && <span className="cm-runhead-meta">First {rows.length} of {total.toLocaleString()}</span>}
        </div>
        <div className="cm-order">
          {rows.map((r, i) => <OrderRow key={r.item.id} row={r} position={i + 1} done={done.has(r.item.id)} onOpen={onOpen ? () => onOpen(r) : undefined} />)}
        </div>
      </section>
    );
  }

  const multiLevel = levelCounts.size > 1;
  return (
    <section className="cm-relsec">
      <div className="cm-runhead">
        <h3 className="cm-h3">{containers.length > 0 ? "Reading list" : "Reading order"}</h3>
        <div className="cm-gran" role="tablist">
          <button type="button" role="tab" aria-selected={mode === "read"} className={`cm-gran-btn${mode === "read" ? " on" : ""}`} onClick={() => setMode("read")}>Reading order</button>
          <button type="button" role="tab" aria-selected={mode === "all"} className={`cm-gran-btn${mode === "all" ? " on" : ""}`} onClick={() => setMode("all")}>All Reading ({total || rows.length})</button>
        </div>
      </div>
      {multiLevel && (
        <div className="cm-dupe" title="This story is owned at more than one collection level — the reading order lists each story once.">
          <span>Owned at {levelCounts.size} levels: {[...levelCounts.entries()].sort((a, b) => LEVEL_ORDER[b[0]] - LEVEL_ORDER[a[0]]).map(([lvl, n]) => `${n} ${lvl}${n > 1 && lvl !== "Issue" ? "s" : ""}`).join(" · ")}</span>
        </div>
      )}
      {mode === "read" ? (
        <div className="cm-colls">
          {merged.items.map((it) => it.kind === "container"
            ? <ContainerRow key={it.r.item.id} container={it.r} childrenById={childrenById} open={open} toggleOpen={toggleOpen} done={done} onOpen={onOpen} />
            : <OrderRow key={it.r.item.id} row={it.r} position={spanStart(it.r) ?? 0} done={done.has(it.r.item.id)} onOpen={onOpen ? () => onOpen(it.r) : undefined} />)}
          {merged.unplaced.length > 0 && (
            <>
              <div className="cm-runhead cm-runhead-sub"><h3 className="cm-h3">Editions without a known range</h3></div>
              {merged.unplaced.map((ct) => <ContainerRow key={ct.item.id} container={ct} childrenById={childrenById} open={open} toggleOpen={toggleOpen} done={done} onOpen={onOpen} />)}
            </>
          )}
        </div>
      ) : (
        <div className="cm-order">
          {rows.map((r, i) => (
            <OrderRow key={r.item.id} row={r} position={i + 1} done={done.has(r.item.id)} covered={multiLevel && containers.some((ct) => inSpan(ct, spanStart(r)))} onOpen={onOpen ? () => onOpen(r) : undefined} />
          ))}
        </div>
      )}
      {truncated && <p className="cm-trunc">First {rows.length} of {total.toLocaleString()} — open Browse for the full run.</p>}
    </section>
  );
}

function ContainerRow({ container, childrenById, open, toggleOpen, done, onOpen }: {
  container: SeriesRunRow; childrenById: Map<number, SeriesRunRow[]>; open: Set<number>; toggleOpen: (id: number) => void; done: Set<number>; onOpen?: (row: SeriesRunRow) => void;
}) {
  const kids = childrenById.get(container.item.id) ?? [];
  const isOpen = open.has(container.item.id);
  const lvl = container.collection?.level ?? "Issue";
  return (
    <div className={`cm-coll${isOpen ? " open" : ""}`}>
      <div className="cm-coll-row" style={{ "--aspect": clampAspect(container.item.coverAspect) } as CSSProperties}>
        <button type="button" className="cm-coll-exp" onClick={() => toggleOpen(container.item.id)} disabled={kids.length === 0} aria-label={isOpen ? "Collapse" : "Expand"} aria-expanded={isOpen}>
          <Chevron open={isOpen} />
        </button>
        <button type="button" className="cm-coll-main" onClick={onOpen ? () => onOpen(container) : undefined} disabled={!onOpen} title={container.item.title ?? undefined}>
          <span className="cm-coll-thumb"><CoverThumb item={container.item} /></span>
          <span className="cm-coll-body">
            <span className="cm-coll-title">{container.item.title}</span>
            <span className="cm-coll-meta">
              <span className={`cm-lvl cm-lvl-${lvl}`}>{lvl}</span>
              {container.collection?.spanLabel && <span className="cm-coll-collects">collects {container.collection.spanLabel}</span>}
              {kids.length > 0 && <span className="cm-coll-n">· {kids.length} {kids.length === 1 ? "item" : "items"}</span>}
            </span>
          </span>
          <span className="cm-coll-year">{dateLabel(container.item.year, container.item.month, container.item.datePrecision)}</span>
        </button>
      </div>
      {isOpen && kids.length > 0 && (
        <div className="cm-coll-kids">
          {kids.map((k) => k.collection?.trackRole === "Container"
            ? <ContainerRow key={k.item.id} container={k} childrenById={childrenById} open={open} toggleOpen={toggleOpen} done={done} onOpen={onOpen} />
            : <OrderRow key={k.item.id} row={k} position={spanStart(k) ?? 0} compact done={done.has(k.item.id)} onOpen={onOpen ? () => onOpen(k) : undefined} />)}
        </div>
      )}
    </div>
  );
}

function OrderRow({ row, position, onOpen, covered = false, compact = false, done = false }: {
  row: SeriesRunRow; position: number; onOpen?: () => void; covered?: boolean; compact?: boolean; done?: boolean;
}) {
  const fmt = row.item.kind === "book" ? null : null;
  return (
    <button type="button" className={`cm-order-row${compact ? " compact" : ""}${done ? " done" : ""}`} style={{ "--aspect": clampAspect(row.item.coverAspect) } as CSSProperties} onClick={onOpen} disabled={!onOpen} title={row.item.title ?? undefined}>
      <span className="cm-order-no">{done ? "✓" : position}</span>
      <span className="cm-order-thumb"><CoverThumb item={row.item} /></span>
      <span className="cm-order-body">
        <span className="cm-order-title">{row.item.title}</span>
        {fmt && <span className="cm-order-fmt">{fmt}</span>}
      </span>
      <span className="cm-order-end">
        {covered && <span className="cm-order-collected" title="Also in a collected edition you own">collected</span>}
        <span className="cm-order-year">{dateLabel(row.item.year, row.item.month, row.item.datePrecision)}</span>
      </span>
    </button>
  );
}

function Chevron({ open }: { open: boolean }) {
  return (
    <svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ transform: open ? "rotate(90deg)" : "none", transition: "transform .15s" }} aria-hidden="true">
      <path d="M6 4l4 4-4 4" />
    </svg>
  );
}
