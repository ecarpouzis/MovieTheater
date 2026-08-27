import { Fragment, useCallback, useMemo, useRef, useState, type CSSProperties } from "react";
import CatalogPager from "../../Components/CatalogPager";
import Card from "../cards/Card";
import InfiniteBands, { type InfiniteBandsHandle } from "../engine/InfiniteBands";
import type { CardItem, CardRenderProps } from "../types";
import type { ViewProps } from "./ViewProps";
import { StreamEmpty, StreamFailed, StreamLoading } from "./StreamStates";
import { useFlatStream, usePagerLetters } from "./flatStream";

/**
 * The continuous-wrap stream of cards behind the Grid and the Wall: one InfiniteBands in flow
 * mode, band pages wrapping across band boundaries with no seam, plus the site's CatalogPager
 * seeking into it (letters under an alphabetical sort, page numbers otherwise). Module-level on
 * purpose: defined inside a view it would be a new component type every render and React would
 * remount the whole stream (losing scroll + band cache) on any parent re-render.
 *
 * R9 S3: the GRID may lay the SECTION's own card into these bands (`CatalogSource.renderCard`) —
 * Movies' MovieCard with its seen/want row, the Boardgame card, Arcade's GameCard, Music's
 * artist/album tiles. One engine, one letter strip, one skeleton, one tweaks plumbing; the card
 * markup is the section's and does not change. The Wall (and every other view) keeps the package card.
 */
export interface FlatCardStreamProps extends ViewProps {
  variant: "grid" | "wall";
  cellH: number;
  perBand: number;
  /** The Wall reports its wrap element so the capacity can be measured. */
  onWrapEl?: (el: HTMLDivElement | null) => void;
}

/** How many skeleton tiles a not-yet-loaded band paints. Clipped to the band's reserved height. */
const SKELETON_TILES = 24;

export default function FlatCardStream({ source, state, variant, cellH, perBand, coverScale, metadata, hover, hoverClass, onWrapEl }: FlatCardStreamProps) {
  const stream = useFlatStream(source, state, perBand);
  const letters = usePagerLetters(source, state, stream.total);
  const engineRef = useRef<InfiniteBandsHandle>(null);
  const [spyUnit, setSpyUnit] = useState(0);
  const onSpy = useCallback((unit: number) => setSpyUnit(unit), []);
  const isWall = variant === "wall";
  const uniformAspect = isWall ? undefined : (source.defaultAspect ?? 0.66);
  // Only the Grid hands its bands to the section (Eric's S3 ruling); every other view keeps the
  // package card so the Wall/List/Extended/Shelves stay one look across the whole site.
  const renderCard = isWall ? undefined : source.renderCard;
  const wrapClass = isWall ? "bx-wall" : `bx-grid${source.gridClass ? ` ${source.gridClass}` : ""}`;

  // Two stable prop objects per stream (above-the-fold and lazy) rather than one per card: a section
  // card is memoized, and a fresh object per render would defeat that memo for the whole band.
  const viewEager = useMemo<CardRenderProps>(
    () => ({ cellH, coverScale, metadata, hover, hoverClass, eager: true, onOpen: stream.open }),
    [cellH, coverScale, metadata, hover, hoverClass, stream.open],
  );
  const viewLazy = useMemo<CardRenderProps>(() => ({ ...viewEager, eager: false }), [viewEager]);

  const renderBand = useCallback((items: CardItem[], band: number) => (
    <>
      {items.map((item, i) => (renderCard ? (
        <Fragment key={item.key}>{renderCard(item, band === 0 && i < 12 ? viewEager : viewLazy)}</Fragment>
      ) : (
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
      )))}
    </>
  ), [cellH, uniformAspect, metadata, isWall, hoverClass, stream.open, renderCard, viewEager, viewLazy]);

  // A band whose page is still on the wire holds its reserved height AND shows the shape of what is
  // coming — the skeleton cards every section's own grid used to paint for its unfetched slots. The
  // block is exactly the height the spacer would have been, so nothing below it moves when data lands.
  const renderPlaceholder = useCallback((_band: number, height: number) => (
    <div className={`bx-skel-band ${wrapClass}`} style={{ height, minHeight: height }} aria-hidden="true">
      {Array.from({ length: SKELETON_TILES }).map((_, i) => (
        <div className="bx-card bx-skel-card" key={i}>
          <div className="bx-cover skeleton-block" style={{ height: cellH, width: Math.round(cellH * (uniformAspect ?? 0.66)) }} />
        </div>
      ))}
    </div>
  ), [wrapClass, cellH, uniformAspect]);

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
        dataVersion={stream.dataVersion}
        fetchBand={stream.fetchBand}
        flow
        wrapClass={wrapClass}
        wrapStyle={{ "--cell": `${cellH}px` } as CSSProperties}
        estBandHeight={isWall ? cellH * 6 : Math.round(cellH * 1.35) * Math.ceil(perBand / 8)}
        onWrapEl={onWrapEl}
        spy={pagerMode === "letters" ? "unit" : "band"}
        onSpy={onSpy}
        renderBand={renderBand}
        renderPlaceholder={renderPlaceholder}
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
