import { useCallback, useEffect, useRef, useState } from "react";
import { Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";
import useInfiniteScroll from "../../hooks/useInfiniteScroll";
import PhotoGrid from "./PhotoGrid";
import PhotoYearRail, { jumpCursorFor } from "./PhotoYearRail";
import { applyPatch } from "./photoPatch";
import LoadFailure from "../../Components/LoadFailure";

// The timeline (docs/photos-plan.md §1: the primary browse surface, §2.7: undated items get their
// own shelf rather than being scattered at epoch 0).
//
// Paging is KEYSET, driven by the cursor the server hands back — never an offset. An ingest running
// while someone browses shifts every offset, and the skipped or repeated photos that produces read
// as data loss on a page whose entire job is to show that nothing was lost.
//
// A YEAR JUMP is the same machinery pointed elsewhere: the rail seeds the cursor at Jan 1 of the
// following year and the list reloads from there. One list, one direction, one cursor — scrolling
// up from a jump hits the top of the list, where the "Back to newest" chip says where you are, which
// beats a bidirectional prepend that has to fight the scroll anchor for every inserted row.

export default function PhotoTimeline({ undated = false, includeHidden = false, onOpen, selection, patch = null }) {
  const [items, setItems] = useState([]);
  const [state, setState] = useState("loading");
  const cursorRef = useRef(null);
  const hasMoreRef = useRef(true);
  const inFlightRef = useRef(false);
  const [hasMore, setHasMore] = useState(true);
  // The year a rail jump landed on, or null for the ordinary newest-first view. Kept as state so the
  // chip and the rail's active mark both read it; the CURSOR it implies is set at jump time.
  const [anchorYear, setAnchorYear] = useState(null);
  // The year currently under the reader's eye, reported by the grid's windowing — the rail's "you
  // are here" mark while scrolling an un-jumped timeline.
  const [visibleYear, setVisibleYear] = useState(null);
  // Guards a slow page from a list that was reset while it was in flight: a page belonging to a
  // previous generation (different jump anchor, different shelf) must not be appended to this one.
  const generationRef = useRef(0);

  const load = useCallback(async () => {
    if (inFlightRef.current || !hasMoreRef.current) return;
    inFlightRef.current = true;
    const generation = generationRef.current;
    try {
      const cursor = cursorRef.current;
      const response = await MovieAPI.getPhotosTimeline({
        beforeTakenAt: cursor?.takenAt ?? undefined,
        beforeId: cursor?.id ?? undefined,
        undated,
        includeHidden,
      });
      if (generation !== generationRef.current) return;
      if (!response.ok) {
        setState("error");
        hasMoreRef.current = false;
        setHasMore(false);
        return;
      }
      const body = await response.json();
      if (generation !== generationRef.current) return;
      cursorRef.current = body.nextCursor;
      hasMoreRef.current = !!body.hasMore;
      setHasMore(!!body.hasMore);
      setItems((prev) => prev.concat(body.items || []));
      setState("ready");
    } catch {
      if (generation !== generationRef.current) return;
      setState("error");
      hasMoreRef.current = false;
      setHasMore(false);
    } finally {
      inFlightRef.current = false;
    }
  }, [undated, includeHidden]);

  /** Restart the list from a cursor (null = the newest photograph). Shared by the mount/prop reset
   *  and the rail's jumps, so there is exactly one way a list begins. */
  const restart = useCallback(
    (cursor) => {
      generationRef.current += 1;
      inFlightRef.current = false;
      setItems([]);
      setState("loading");
      cursorRef.current = cursor;
      hasMoreRef.current = true;
      setHasMore(true);
      load();
    },
    [load]
  );

  // Switching shelves — or asking to see hidden items — is a different list, not more of this one.
  useEffect(() => {
    setAnchorYear(null);
    restart(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [undated, includeHidden]);

  const jump = (year) => {
    setAnchorYear(year);
    setVisibleYear(null);
    restart(year == null ? null : jumpCursorFor(year));
    // A jump reads as a page turn, not a scroll: land at the top of the new page.
    const scroller = document.scrollingElement || document.documentElement;
    if (scroller) scroller.scrollTop = 0;
  };

  const { sentinelRef, recheck } = useInfiniteScroll({
    enabled: state !== "loading" || items.length > 0,
    hasMore,
    onLoadMore: load,
  });

  // A curation write, applied to the cards already on screen rather than re-fetched (photoPatch.js).
  // This shelf's membership rule, stated once: the timeline is the photographs that are dated, not
  // hidden, and on the family shelf — so a picture that just became any of those things leaves, and
  // the reader keeps their place in everything that did not.
  useEffect(() => {
    if (!patch) return;
    setItems((prev) =>
      applyPatch(prev, patch, (item) => {
        if (item.hidden && !includeHidden) return false;
        if (item.shelf === "Archive") return false;
        return undated ? !item.takenAt : true;
      })
    );
  }, [patch, includeHidden, undated]);

  // Nudge the position check after an append rather than letting the hook re-subscribe: it also
  // auto-fills a list too short to scroll, which is the stall the site's pattern exists to avoid.
  useEffect(() => {
    recheck();
  }, [items.length, recheck]);

  // "June 2024" → 2024. The grid reports section keys (YYYY-MM or "undated"); the rail wants years.
  const onSection = useCallback((key) => {
    const year = /^\d{4}/.test(key || "") ? Number(key.slice(0, 4)) : null;
    setVisibleYear((prev) => (prev === year ? prev : year));
  }, []);

  if (state === "loading" && items.length === 0) return <Spin />;
  if (state === "error" && items.length === 0) return <LoadFailure message="Could not load the timeline." />;

  return (
    <div className={undated ? "" : "photos-timeline-shell"}>
      <div className="photos-timeline-main">
        {anchorYear != null && (
          <div className="photos-jump-chip">
            <span>
              Showing from <strong>{anchorYear}</strong>
            </span>
            <button type="button" onClick={() => jump(null)}>
              Back to newest
            </button>
          </div>
        )}
        <PhotoGrid
          items={items}
          groupBySection={!undated}
          onOpen={onOpen}
          selection={selection}
          onSection={undated ? undefined : onSection}
          emptyText={undated ? "Nothing is waiting for a date." : "No dated photos yet."}
        />
        <div ref={sentinelRef} className="photos-sentinel">
          {hasMore && items.length > 0 && <Spin size="small" />}
        </div>
      </div>
      {!undated && (
        <PhotoYearRail
          includeHidden={includeHidden}
          currentYear={visibleYear}
          activeYear={anchorYear}
          onJump={jump}
        />
      )}
    </div>
  );
}
