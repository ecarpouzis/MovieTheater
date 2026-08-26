import { useCallback, useMemo, useRef, useState, type CSSProperties } from "react";
import CatalogPager from "../../Components/CatalogPager";
import CardImage from "../cards/CardImage";
import InfiniteBands, { type InfiniteBandsHandle } from "../engine/InfiniteBands";
import type { CardItem, ListColumn } from "../types";
import type { ViewProps } from "./ViewProps";
import { StreamEmpty, StreamFailed, StreamLoading } from "./StreamStates";
import { useFlatStream, usePagerLetters } from "./flatStream";

/**
 * List — the dense utility table: a thumbnail and the section's columns, one header, row pages
 * streamed and recycled below it. A column click sorts WITHIN each band (the same semantics the
 * standalone's paginated List had: it only ever sorted the current page); the server order stays
 * the Sort pill's.
 */
export const LIST_ROW_PX = 55;

const DEFAULT_COLUMNS: ListColumn[] = [
  { key: "title", label: "Title", width: "2fr", value: (i) => i.title },
  { key: "label", label: "Year", width: "72px", mono: true, value: (i) => i.label ?? i.year ?? "" },
  { key: "subtitle", label: "", width: "1.2fr", value: (i) => i.subtitle ?? "" },
];

function compare(a: CardItem, b: CardItem, col: ListColumn, dir: number): number {
  const av = col.value(a) ?? "";
  const bv = col.value(b) ?? "";
  const cmp = typeof av === "number" && typeof bv === "number"
    ? av - bv
    : String(av).localeCompare(String(bv), undefined, { numeric: true });
  return cmp * dir;
}

function ListRows({ items, columns, sortKey, dir, onOpen }: {
  items: CardItem[]; columns: ListColumn[]; sortKey: string | null; dir: number; onOpen: (i: CardItem) => void;
}) {
  const sorted = useMemo(() => {
    const col = columns.find((c) => c.key === sortKey);
    return col ? [...items].sort((a, b) => compare(a, b, col, dir)) : items;
  }, [items, columns, sortKey, dir]);
  return (
    <>
      {sorted.map((item) => (
        <button key={item.key} type="button" className="bx-list-row" onClick={() => onOpen(item)}>
          <div className="bx-list-thumb"><CardImage src={item.imageThumbUrl ?? item.imageUrl} hue={item.hue} /></div>
          {columns.map((c) => {
            const v = c.value(item);
            return (
              <div key={c.key} className={`bx-lc${c.mono ? " bx-lc-mono" : ""}${c.align === "right" ? " bx-lc-right" : ""}`} title={v == null ? undefined : String(v)}>
                {v == null || v === "" ? "—" : String(v)}
              </div>
            );
          })}
        </button>
      ))}
    </>
  );
}

export default function ListView({ source, state }: ViewProps) {
  const columns = source.listColumns ?? DEFAULT_COLUMNS;
  const perBand = source.pageSize ?? 48;
  const stream = useFlatStream(source, state, perBand);
  const letters = usePagerLetters(source, state, stream.total);
  const engineRef = useRef<InfiniteBandsHandle>(null);
  const [spyUnit, setSpyUnit] = useState(0);
  const onSpy = useCallback((unit: number) => setSpyUnit(unit), []);
  const [sortKey, setSortKey] = useState<string | null>(null);
  const [dir, setDir] = useState(1);
  const setSort = (k: string) => {
    if (k === sortKey) setDir((d) => -d); else { setSortKey(k); setDir(1); }
  };
  const gridTemplate = `44px ${columns.map((c) => c.width ?? "1fr").join(" ")}`;
  const renderBand = useCallback((items: CardItem[]) => (
    <ListRows items={items} columns={columns} sortKey={sortKey} dir={dir} onOpen={stream.open} />
  ), [columns, sortKey, dir, stream.open]);

  if (stream.loading && !stream.band0) return <StreamLoading />;
  if (stream.error && !stream.band0) return <StreamFailed onRetry={stream.retry} />;
  if (!stream.band0 || stream.band0.length === 0) return <StreamEmpty noun={source.itemNoun ?? "item"} />;

  const pagerMode = letters ? "letters" : "pages";
  return (
    <div className="bx-list" style={{ "--bx-list-cols": gridTemplate } as CSSProperties}>
      <div className="bx-list-head" role="row">
        <div />
        {columns.map((c) => (
          <button key={c.key} type="button" className={`bx-list-sortbtn${sortKey === c.key ? " on" : ""}`} onClick={() => setSort(c.key)}>
            {c.label}{sortKey === c.key && <span className="bx-caret" aria-hidden="true">{dir > 0 ? "▲" : "▼"}</span>}
          </button>
        ))}
      </div>
      <InfiniteBands<CardItem>
        ref={engineRef}
        key="list-flat"
        total={stream.total}
        perBand={perBand}
        band0={stream.band0}
        queryKey={`${stream.queryKey}|list`}
        fetchBand={stream.fetchBand}
        estBandHeight={perBand * LIST_ROW_PX}
        spy={pagerMode === "letters" ? "unit" : "band"}
        onSpy={onSpy}
        renderBand={renderBand}
      />
      {stream.total > perBand && (
        <CatalogPager
          mode={pagerMode}
          letters={letters}
          total={stream.total}
          pageSize={perBand}
          currentIndex={spyUnit}
          disabled={false}
          onJump={(offset: number) => engineRef.current?.jumpToUnit(offset)}
          itemNoun={source.itemNoun ?? "item"}
        />
      )}
    </div>
  );
}
