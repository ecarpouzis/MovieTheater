import { useCallback, useEffect, useRef, useState } from "react";
import { Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";
import useInfiniteScroll from "../../hooks/useInfiniteScroll";
import PhotoGrid from "./PhotoGrid";

// The timeline (docs/photos-plan.md §1: the primary browse surface, §2.7: undated items get their
// own shelf rather than being scattered at epoch 0).
//
// Paging is KEYSET, driven by the cursor the server hands back — never an offset. An ingest running
// while someone browses shifts every offset, and the skipped or repeated photos that produces read
// as data loss on a page whose entire job is to show that nothing was lost.

export default function PhotoTimeline({ undated = false, includeHidden = false, onOpen, selection }) {
  const [items, setItems] = useState([]);
  const [state, setState] = useState("loading");
  const cursorRef = useRef(null);
  const hasMoreRef = useRef(true);
  const inFlightRef = useRef(false);
  const [hasMore, setHasMore] = useState(true);

  const load = useCallback(async () => {
    if (inFlightRef.current || !hasMoreRef.current) return;
    inFlightRef.current = true;
    try {
      const cursor = cursorRef.current;
      const response = await MovieAPI.getPhotosTimeline({
        beforeTakenAt: cursor?.takenAt ?? undefined,
        beforeId: cursor?.id ?? undefined,
        undated,
        includeHidden,
      });
      if (!response.ok) {
        setState("error");
        hasMoreRef.current = false;
        setHasMore(false);
        return;
      }
      const body = await response.json();
      cursorRef.current = body.nextCursor;
      hasMoreRef.current = !!body.hasMore;
      setHasMore(!!body.hasMore);
      setItems((prev) => prev.concat(body.items || []));
      setState("ready");
    } catch {
      setState("error");
      hasMoreRef.current = false;
      setHasMore(false);
    } finally {
      inFlightRef.current = false;
    }
  }, [undated, includeHidden]);

  // Switching shelves — or asking to see hidden items — is a different list, not more of this one.
  useEffect(() => {
    setItems([]);
    setState("loading");
    cursorRef.current = null;
    hasMoreRef.current = true;
    setHasMore(true);
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [undated, includeHidden]);

  const { sentinelRef, recheck } = useInfiniteScroll({
    enabled: state !== "loading" || items.length > 0,
    hasMore,
    onLoadMore: load,
  });

  // Nudge the position check after an append rather than letting the hook re-subscribe: it also
  // auto-fills a list too short to scroll, which is the stall the site's pattern exists to avoid.
  useEffect(() => {
    recheck();
  }, [items.length, recheck]);

  if (state === "loading" && items.length === 0) return <Spin />;
  if (state === "error" && items.length === 0) return <p className="photos-note">Could not load the timeline.</p>;

  return (
    <>
      <PhotoGrid
        items={items}
        groupBySection={!undated}
        onOpen={onOpen}
        selection={selection}
        emptyText={undated ? "Nothing is waiting for a date." : "No dated photos yet."}
      />
      <div ref={sentinelRef} className="photos-sentinel">
        {hasMore && items.length > 0 && <Spin size="small" />}
      </div>
    </>
  );
}
