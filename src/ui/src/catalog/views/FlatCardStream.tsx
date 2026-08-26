import { useCallback, useRef, useState, type CSSProperties } from "react";
import CatalogPager from "../../Components/CatalogPager";
import Card from "../cards/Card";
import InfiniteBands, { type InfiniteBandsHandle } from "../engine/InfiniteBands";
import type { CardItem } from "../types";
import type { ViewProps } from "./ViewProps";
import { StreamEmpty, StreamFailed, StreamLoading } from "./StreamStates";
import { useFlatStream, usePagerLetters } from "./flatStream";

/**
 * The continuous-wrap stream of cards behind the Grid and the Wall: one InfiniteBands in flow
 * mode, band pages wrapping across band boundaries with no seam, plus the site's CatalogPager
 * seeking into it (letters under an alphabetical sort, page numbers otherwise). Module-level on
 * purpose: defined inside a view it would be a new component type every render and React would
 * remount the whole stream (losing scroll + band cache) on any parent re-render.
 */
export interface FlatCardStreamProps extends ViewProps {
  variant: "grid" | "wall";
  cellH: number;
  perBand: number;
  /** The Wall reports its wrap element so the capacity can be measured. */
  onWrapEl?: (el: HTMLDivElement | null) => void;
}

export default function FlatCardStream({ source, state, variant, cellH, perBand, metadata, hoverClass, onWrapEl }: FlatCardStreamProps) {
  const stream = useFlatStream(source, state, perBand);
  const letters = usePagerLetters(source, state, stream.total);
  const engineRef = useRef<InfiniteBandsHandle>(null);
  const [spyUnit, setSpyUnit] = useState(0);
  const onSpy = useCallback((unit: number) => setSpyUnit(unit), []);
  const isWall = variant === "wall";
  const uniformAspect = isWall ? undefined : (source.defaultAspect ?? 0.66);

  const renderBand = useCallback((items: CardItem[], band: number) => (
    <>
      {items.map((item, i) => (
        <Card
          key={item.key}
          item={item}
          cellH={cellH}
          uniformAspect={uniformAspect}
          metadata={metadata}
          hoverMeta={isWall}
          hoverClass={hoverClass}
          eager={band === 0 && i < 12}
          onOpen={stream.open}
        />
      ))}
    </>
  ), [cellH, uniformAspect, metadata, isWall, hoverClass, stream.open]);

  if (stream.loading && !stream.band0) return <StreamLoading />;
  if (stream.error && !stream.band0) return <StreamFailed onRetry={stream.retry} />;
  if (!stream.band0 || stream.band0.length === 0) return <StreamEmpty noun={source.itemNoun ?? "item"} />;

  const pagerMode = letters ? "letters" : "pages";
  return (
    <>
      <InfiniteBands<CardItem>
        ref={engineRef}
        key={`${variant}-flat`}
        total={stream.total}
        perBand={perBand}
        band0={stream.band0}
        queryKey={`${stream.queryKey}|${variant}`}
        fetchBand={stream.fetchBand}
        flow
        wrapClass={isWall ? "bx-wall" : "bx-grid"}
        wrapStyle={{ "--cell": `${cellH}px` } as CSSProperties}
        estBandHeight={isWall ? cellH * 6 : Math.round(cellH * 1.35) * Math.ceil(perBand / 8)}
        onWrapEl={onWrapEl}
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
    </>
  );
}
