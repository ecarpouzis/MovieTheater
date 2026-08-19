import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import useLongPress from "../../hooks/useLongPress";
import { getScrollParent, onAnyScroll, viewportBand } from "../../utils/scroll";
import {
  blockAtOffset,
  buildBlocks,
  sectionKeyOf,
  visibleRange,
  DEFAULT_GAP,
  DEFAULT_TARGET_ROW_HEIGHT,
} from "./justifiedLayout";
import { formatDuration } from "./PhotoVideo";

// Virtualized justified grid (docs/photos-plan.md §4).
//
// Windowing is exact rather than measured: every tile's size is arithmetic over dimensions the
// ingest already stored (EXIF-oriented), so the full scroll height is known before anything renders.
// That is why this does NOT reuse useGridWindow — that hook measures uniform CSS-grid rows and
// corrects the scroll position when an estimate turns out wrong; here there are no estimates to
// correct, and its anchor nudging would fight a layout that never moves.

const OVERSCAN = 1000;

// §2.12's museum treatment, as two numbers. Artwork is looked AT, one piece at a time, so a gallery
// row is taller and the gutters are wide enough that two paintings never read as one diptych. The
// family timeline keeps its dense contact-sheet packing, which is right for a hundred snapshots of
// one afternoon and wrong for a wall.
export const GALLERY_TARGET_ROW_HEIGHT = 300;
export const GALLERY_GAP = 26;

export default function PhotoGrid({
  items,
  groupBySection = true,
  onOpen,
  emptyText,
  selection,
  gallery = false,
  plaqueArtist = null,
  onSection = null,
}) {
  const hostRef = useRef(null);
  const rootRef = useRef(null);
  const rafRef = useRef(0);
  const [width, setWidth] = useState(0);
  const [range, setRange] = useState([0, 0]);
  // The section under the top of the viewport, reported through a ref-read callback so the year
  // rail can say "you are here" without this component re-rendering for it.
  const onSectionRef = useRef(onSection);
  onSectionRef.current = onSection;
  const lastSectionRef = useRef(null);

  const gap = gallery ? GALLERY_GAP : DEFAULT_GAP;
  const { blocks, totalHeight } = width > 0
    ? buildBlocks(items, {
        containerWidth: width,
        targetRowHeight: gallery ? GALLERY_TARGET_ROW_HEIGHT : DEFAULT_TARGET_ROW_HEIGHT,
        gap,
        groupBySection,
      })
    : { blocks: [], totalHeight: 0 };

  const blocksRef = useRef(blocks);
  blocksRef.current = blocks;

  // ── Selecting in bulk (docs/photos-plan.md §2.9) ───────────────────────────────────────────────
  // The batch job this section exists for is "put these forty photographs in an album", and doing it
  // forty taps at a time is the thing that made batch work not worth starting. So the grid owns three
  // bulk gestures, all of which need the ORDER items are displayed in — which only the grid has.
  const order = useMemo(() => {
    const index = new Map();
    (items || []).forEach((item, i) => index.set(item.id, i));
    return index;
  }, [items]);

  // The last tile touched, which is what a range press measures FROM. Held as an ID and resolved to a
  // position only when a range is actually taken: a batch write removes cards from the list under the
  // reader (photoPatch.js), and a remembered INDEX would quietly start pointing at a different
  // photograph the moment anything above it left.
  const anchorRef = useRef(null);

  // The bar's "All" needs to know what is on screen to select, and the grid is the only thing that
  // knows. Published rather than duplicated: the page holding the bar does not re-derive the list.
  const selectionRef = useRef(selection);
  selectionRef.current = selection;
  useEffect(() => {
    selectionRef.current?.register?.((items || []).map((item) => item.id));
  }, [items]);

  /** A plain tap on a tile: it toggles, and it becomes the anchor a later range press measures from. */
  const tapped = useCallback((id) => {
    anchorRef.current = id;
    selectionRef.current?.toggle(id);
  }, []);

  /**
   * Press and hold. Out of selection mode it STARTS selecting — the gesture that answers "tapping a
   * photo to put it in an album opens it instead". Already selecting, it selects everything from the
   * last tile touched through this one, which is how forty photographs get picked in two gestures.
   */
  const held = useCallback((id) => {
    const current = selectionRef.current;
    if (!current) return;
    const index = order.get(id);
    // An anchor whose photograph has since left the list is no anchor at all — take this one instead
    // of a span measured from a card that is not there.
    const anchor = current.active ? order.get(anchorRef.current) : undefined;
    anchorRef.current = id;

    if (!current.active || anchor == null || index == null) {
      if (!current.active) current.enable?.();
      current.toggle(id);
      return;
    }
    const [from, to] = anchor <= index ? [anchor, index] : [index, anchor];
    current.selectMany?.((items || []).slice(from, to + 1).map((item) => item.id), true);
  }, [items, order]);

  /** A month header, in selection mode: takes the whole month, or gives it all back. */
  const toggleSection = useCallback((key) => {
    const current = selectionRef.current;
    if (!current) return;
    const ids = (items || []).filter((item) => sectionKeyOf(item) === key).map((item) => item.id);
    if (!ids.length) return;
    current.selectMany?.(ids, !ids.every((id) => current.has(id)));
  }, [items]);

  const maintain = useCallback(() => {
    rafRef.current = 0;
    const host = hostRef.current;
    if (!host) return;
    const band = viewportBand(rootRef.current);
    // Blocks are positioned relative to the host, so shift the band into the host's coordinates.
    const hostTop = host.getBoundingClientRect().top;
    const next = visibleRange(blocksRef.current, { top: band.top - hostTop, bottom: band.bottom - hostTop }, OVERSCAN);
    setRange((prev) => (prev[0] === next[0] && prev[1] === next[1] ? prev : next));

    // Report which SECTION sits under the top of the viewport. Walk back from the topmost visible
    // block to its nearest header — rows carry no key, headers do — and only speak on change.
    if (onSectionRef.current) {
      const blocks = blocksRef.current;
      let i = blockAtOffset(blocks, band.top - hostTop + 1);
      while (i > 0 && blocks[i]?.type !== "header") i -= 1;
      const key = blocks[i]?.type === "header" ? blocks[i].key : null;
      if (key !== lastSectionRef.current) {
        lastSectionRef.current = key;
        onSectionRef.current(key);
      }
    }
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
    <div className={`photo-grid${gallery ? " photo-grid--gallery" : ""}`} ref={hostRef} style={{ height: totalHeight }}>
      {blocks.slice(start, end).map((block) =>
        block.type === "header" ? (
          <div className="photo-grid-header" key={`h-${block.key}-${block.top}`} style={{ top: block.top }}>
            {/* A dated section's stamp inks the month and lets the year sit lighter beside it — the
                way the year on a lab print was the small half of the burn. The undated shelf's label
                has no such halves and prints as it is. */}
            {/^\d{4}-\d{2}$/.test(block.key) ? (
              <>
                <span className="photo-grid-header-month">{block.label.slice(0, block.label.lastIndexOf(" "))}</span>
                <span className="photo-grid-header-year">{block.label.slice(block.label.lastIndexOf(" ") + 1)}</span>
              </>
            ) : (
              block.label
            )}
            {/* Selecting a month is one tap, not sixty. Only offered once selecting has begun: on a
                page that is being READ, a "select all" beside every month is an invitation to a
                mis-tap that quietly picks up four hundred photographs.

                Named for its SCOPE rather than "Select all", which is the dock's button and means the
                whole list — two controls a thumb's width apart must not share a label. */}
            {selection?.active && (
              <button
                type="button"
                className="photo-grid-header-select"
                onClick={() => toggleSection(block.key)}
              >
                {block.key === "undated" ? "Select these" : "Select month"}
              </button>
            )}
          </div>
        ) : (
          <div className="photo-grid-row" key={`r-${block.top}`} style={{ top: block.top, height: block.height }}>
            {block.tiles.map((tile) => (
              <PhotoTile
                key={tile.item.id}
                tile={tile}
                onOpen={onOpen}
                selection={selection}
                onTap={tapped}
                onHold={held}
                plaqueArtist={gallery ? plaqueArtist : null}
              />
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
 * In selection mode the tap SELECTS instead of opening — one mode, no modifier keys to discover —
 * and the hidden badge is a subtle corner mark rather than a dimmed tile: a hidden photo in the
 * folder view is still a photo that is still on disk (§2.9), and it must not read as broken.
 *
 * Out of selection mode there are still two ways to pick a photograph up without opening it: the
 * corner target (drawn on hover, and always on a touch screen where there is no hover to draw it
 * with) and press-and-hold. Both exist because the mode switch lives at the top of the page, and
 * "scroll back up to the top, flip a switch, scroll back down" is not a thing anybody does twice.
 *
 * The root is a div-with-a-role rather than a <button> for exactly one reason: the corner target is
 * itself a button, and a button inside a button is not a thing HTML has an answer for.
 */
function PhotoTile({ tile, onOpen, selection, onTap, onHold, plaqueArtist = null }) {
  const { item, width, height } = tile;
  const style = { width, height };
  const label = item.path?.split("/").pop() ?? "";
  const selecting = !!selection?.active;
  const selected = selecting && selection.has(item.id);
  const { handlers, consumeClick } = useLongPress(() => onHold?.(item.id));

  const activate = () => {
    // The click the browser sends after a long press would undo what the hold just selected.
    if (consumeClick()) return;
    if (selecting) onTap?.(item.id);
    else onOpen?.(item);
  };

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
      {/* §2.12's shelf badge, drawn on the FOLDER view where both shelves are shown together. Same
          reasoning as the group mark beside it: on the "what is actually on disk" surface an absence
          from the timeline is a mystery, and a two-word mark is the explanation. Suppressed inside a
          gallery, where every picture is archived and the badge would be noise on all of them. */}
      {item.shelf === "Archive" && !plaqueArtist && (
        <span className="photo-tile-shelf" title="In the gallery — not on the family timeline">
          gallery
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
      {/* The corner target: tap it and the photograph is picked up, whether or not selection mode was
          already on. It is the whole answer to "I wanted it in an album and it opened instead". */}
      {selection && (
        <button
          type="button"
          className="photo-tile-select"
          aria-label={selected ? `Deselect ${label}` : `Select ${label}`}
          aria-pressed={selected}
          onClick={(event) => {
            event.stopPropagation();
            if (!selecting) selection.enable?.();
            onTap?.(item.id);
          }}
        >
          <span className="photo-tile-check">{selected ? "✓" : ""}</span>
        </button>
      )}
    </>
  );

  const className = [
    "photo-tile",
    item.gridUrl ? "" : `photo-tile-placeholder photo-tile-${(item.thumbState || "Pending").toLowerCase()}`,
    selected ? "photo-tile-selected" : "",
    selecting ? "photo-tile-selectable" : "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <div
      role="button"
      tabIndex={0}
      className={className}
      style={style}
      onClick={activate}
      onKeyDown={(event) => {
        if (event.key !== "Enter" && event.key !== " ") return;
        event.preventDefault();
        if (selecting) onTap?.(item.id);
        else onOpen?.(item);
      }}
      title={label}
      aria-pressed={selecting ? selected : undefined}
      {...handlers}
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
      {/* The plaque (§2.12): what a gallery puts beside a painting rather than on it. The title is
          derived from the filename because that is the only name these files have — nothing invents
          one — and the artist is the collection's, since every piece on this wall is theirs. */}
      {plaqueArtist && (
        <span className="photo-tile-plaque">
          <span className="photo-tile-plaque-title">{plaqueTitle(label)}</span>
          <span className="photo-tile-plaque-artist">{plaqueArtist}</span>
        </span>
      )}
    </div>
  );
}

/**
 * A filename made presentable for a plaque: the extension goes, separators become spaces, and any
 * run of whitespace collapses.
 *
 * Deliberately NOT title-cased and not otherwise "improved". These names came off the internet and
 * out of scanners, and a plaque that rewrote them would be inventing a title for a picture whose
 * real one nobody recorded — the same reason §2.7 refuses to invent a date. Exported because it is
 * the one piece of the museum treatment with a right and a wrong answer per input.
 */
export function plaqueTitle(fileName) {
  const base = String(fileName || "").replace(/\.[^.]+$/, "");
  return base.replace(/[_-]+/g, " ").replace(/\s+/g, " ").trim() || String(fileName || "");
}
