import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { getScrollParent, onAnyScroll, viewportBand } from "../../utils/scroll";
import { buildBlocks, visibleRange, DEFAULT_GAP, DEFAULT_TARGET_ROW_HEIGHT } from "./justifiedLayout";
import { formatDuration } from "./PhotoVideo";

// Virtualized justified grid (docs/photos-plan.md §4).
//
// Windowing is exact rather than measured: every tile's size is arithmetic over dimensions the
// ingest already stored (EXIF-oriented), so the full scroll height is known before anything renders.
// That is why this does NOT reuse useGridWindow — that hook measures uniform CSS-grid rows and
// corrects the scroll position when an estimate turns out wrong; here there are no estimates to
// correct, and its anchor nudging would fight a layout that never moves.

const OVERSCAN = 1000;

export default function PhotoGrid({ items, groupBySection = true, onOpen, emptyText, selection }) {
  const hostRef = useRef(null);
  const rootRef = useRef(null);
  const rafRef = useRef(0);
  const [width, setWidth] = useState(0);
  const [range, setRange] = useState([0, 0]);

  const { blocks, totalHeight } = width > 0
    ? buildBlocks(items, {
        containerWidth: width,
        targetRowHeight: DEFAULT_TARGET_ROW_HEIGHT,
        gap: DEFAULT_GAP,
        groupBySection,
      })
    : { blocks: [], totalHeight: 0 };

  const blocksRef = useRef(blocks);
  blocksRef.current = blocks;

  const maintain = useCallback(() => {
    rafRef.current = 0;
    const host = hostRef.current;
    if (!host) return;
    const band = viewportBand(rootRef.current);
    // Blocks are positioned relative to the host, so shift the band into the host's coordinates.
    const hostTop = host.getBoundingClientRect().top;
    const next = visibleRange(blocksRef.current, { top: band.top - hostTop, bottom: band.bottom - hostTop }, OVERSCAN);
    setRange((prev) => (prev[0] === next[0] && prev[1] === next[1] ? prev : next));
  }, []);

  const schedule = useCallback(() => {
    if (rafRef.current) return;
    rafRef.current = requestAnimationFrame(maintain);
  }, [maintain]);

  // Width drives the whole layout, so it is the one thing that must be observed.
  useLayoutEffect(() => {
    const host = hostRef.current;
    if (!host) return undefined;
    const read = () => setWidth((prev) => (Math.abs(prev - host.clientWidth) > 1 ? host.clientWidth : prev));
    read();
    if (typeof ResizeObserver === "undefined") return undefined;
    const ro = new ResizeObserver(read);
    ro.observe(host);
    return () => ro.disconnect();
  }, []);

  useEffect(() => {
    rootRef.current = getScrollParent(hostRef.current);
    const off = onAnyScroll(schedule);
    schedule();
    return () => {
      off();
      if (rafRef.current) cancelAnimationFrame(rafRef.current);
      rafRef.current = 0;
    };
  }, [schedule]);

  // A new page appended (or a new list entirely) changes the block list under the window.
  useEffect(() => {
    schedule();
  }, [items, width, schedule]);

  if (!items?.length && emptyText) {
    return <p className="photos-note">{emptyText}</p>;
  }

  const [start, end] = range;
  return (
    <div className="photo-grid" ref={hostRef} style={{ height: totalHeight }}>
      {blocks.slice(start, end).map((block) =>
        block.type === "header" ? (
          <div className="photo-grid-header" key={`h-${block.key}-${block.top}`} style={{ top: block.top }}>
            {block.label}
          </div>
        ) : (
          <div className="photo-grid-row" key={`r-${block.top}`} style={{ top: block.top, height: block.height }}>
            {block.tiles.map((tile) => (
              <PhotoTile key={tile.item.id} tile={tile} onOpen={onOpen} selection={selection} />
            ))}
          </div>
        )
      )}
    </div>
  );
}

/**
 * The video mark on a tile (docs/photos-plan.md §2.3), or null for a photo.
 *
 * Once the video pass has run, ffprobe's duration is on the row, so a tile can say how long a clip is
 * before anything has loaded. A video with no Jellyfin item id says THAT instead: an honest "not
 * synced" beats a play triangle that leads nowhere, and the file is safe on disk either way.
 *
 * Exported as a pure function because it is a decision worth asserting on its own — the grid it lives
 * in is virtualized, and a test that had to defeat the windowing to reach one tile would be measuring
 * the scroll math rather than this rule.
 */
export function videoBadge(item) {
  if (item?.kind !== "Video") return null;
  if (item.videoSynced === false) {
    return {
      text: "▶ !",
      className: "photo-tile-badge photo-tile-badge-unsynced",
      title: "Not yet synced for playback — the file is safe on disk",
    };
  }
  const duration = formatDuration(item.durationSec);
  return {
    text: duration ? `▶ ${duration}` : "▶",
    className: "photo-tile-badge",
    title: undefined,
  };
}

/**
 * One card. Three deterministic states rather than one <img> that might 404: a ready derivative, a
 * video (Phase 5 has the poster grabs, §2.3), and a still whose format this build has no decoder
 * for. The server sends `thumbState` precisely so the UI never has to guess which it is holding.
 *
 * In selection mode the click SELECTS instead of opening — one mode, no modifier keys to discover —
 * and the hidden badge is a subtle corner mark rather than a dimmed tile: a hidden photo in the
 * folder view is still a photo that is still on disk (§2.9), and it must not read as broken.
 */
function PhotoTile({ tile, onOpen, selection }) {
  const { item, width, height } = tile;
  const style = { width, height };
  const label = item.path?.split("/").pop() ?? "";
  const selecting = !!selection?.active;
  const selected = selecting && selection.has(item.id);
  const activate = () => (selecting ? selection.toggle(item.id) : onOpen?.(item));

  const badge = videoBadge(item);

  const marks = (
    <>
      {badge && (
        <span className={badge.className} title={badge.title}>
          {badge.text}
        </span>
      )}
      {item.hidden && (
        <span className="photo-tile-hidden" title="Hidden from the timeline and albums">
          hidden
        </span>
      )}
      {/* The §2.6 group badge. A small mark, never a dimmed tile: a collapsed copy in the folder view
          is still a photo that is still on disk, and it must not read as broken or deleted. */}
      {item.group && (
        <span
          className={item.group.collapsed ? "photo-tile-group collapsed" : "photo-tile-group"}
          title={
            item.group.collapsed
              ? `One of ${item.group.size} copies — another one represents it in the timeline`
              : `${item.group.size} copies of this photo; this is the one the timeline shows`
          }
        >
          ×{item.group.size}
        </span>
      )}
      {selecting && <span className="photo-tile-check">{selected ? "✓" : ""}</span>}
    </>
  );

  const className = [
    "photo-tile",
    item.gridUrl ? "" : `photo-tile-placeholder photo-tile-${(item.thumbState || "Pending").toLowerCase()}`,
    selected ? "photo-tile-selected" : "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <button
      type="button"
      className={className}
      style={style}
      onClick={activate}
      title={label}
      aria-pressed={selecting ? selected : undefined}
    >
      {item.gridUrl ? (
        <img src={item.gridUrl} alt="" loading="lazy" decoding="async" width={width} height={height} />
      ) : (
        <>
          <span className="photo-tile-placeholder-mark">{item.kind === "Video" ? "▶" : "◇"}</span>
          <span className="photo-tile-placeholder-name">{label}</span>
        </>
      )}
      {marks}
    </button>
  );
}
