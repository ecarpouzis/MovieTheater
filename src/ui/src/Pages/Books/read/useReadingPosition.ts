/**
 * The readers' side of the ONE progress API (`/positions/{id}`): where to resume, the debounced
 * position writes (220 ms for a page turn, 600 ms for an EPUB spot), and the two explicit acts —
 * Mark read (`lastPage: -1`, the only Finished signal) and its undo. The standalone's laws stand:
 *   • reaching the last page is NEVER "read" — only the button is;
 *   • a book already marked read is never downgraded to in-progress by reopening it;
 *   • a position already saved is not written again;
 *   • one write in flight at a time; a turn during a write queues exactly one follow-up.
 * The resume point is decided ONCE from the first answer — later refetches (a mark, a shelf
 * invalidation) must not yank the reader to another page mid-read.
 */
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getPosition, markFinished as apiMarkFinished, putPosition, resetPosition, type ReadingPosition } from "../booksApi";
import { bk, invalidateAfter } from "../booksQuery";

export type ReadStatus = ReadingPosition["status"];

export interface ResumePoint {
  /** The page to open on (paged), or null when the book was never opened. */
  page: number | null;
  /** The spine item + fraction to open at (EPUB), or null. */
  spineIndex: number | null;
  scrollPercent: number | null;
  status: ReadStatus;
}

export const PAGE_SAVE_MS = 220;
export const EPUB_SAVE_MS = 600;

export default function useReadingPosition(itemId: number, pageCount: number | null | undefined) {
  const qc = useQueryClient();
  const query = useQuery({ queryKey: bk.position(itemId), queryFn: ({ signal }) => getPosition(itemId, signal), retry: false });

  const [resume, setResume] = useState<ResumePoint | null>(null);
  const savedPageRef = useRef<number | null>(null);
  const savedStatusRef = useRef<ReadStatus | null>(null);
  const savedEpubRef = useRef<string>("");
  const inFlightRef = useRef(false);
  const queuedRef = useRef<{ page: number } | null>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const lastPageRef = useRef<number | null>(null);
  const dirtyRef = useRef(false);

  // Decide the resume point exactly once per book.
  useEffect(() => {
    savedPageRef.current = null; savedStatusRef.current = null; savedEpubRef.current = "";
    lastPageRef.current = null; dirtyRef.current = false; queuedRef.current = null;
    setResume(null);
  }, [itemId]);
  useEffect(() => {
    if (!query.data || resume) return;
    const p = query.data;
    if (p.status === "finished") {
      savedPageRef.current = -1;
      savedStatusRef.current = "finished";
      setResume({ page: pageCount && pageCount > 0 ? pageCount - 1 : null, spineIndex: p.lastSpineItemIndex, scrollPercent: p.lastScrollPercent, status: "finished" });
      return;
    }
    if (p.lastPage != null && p.lastPage >= 0 && (p.status === "inprogress" || p.lastPage > 0)) {
      savedPageRef.current = p.lastPage;
      savedStatusRef.current = p.status;
    }
    if (p.lastSpineItemIndex != null) savedEpubRef.current = `${p.lastSpineItemIndex}:${(p.lastScrollPercent ?? 0).toFixed(3)}`;
    setResume({
      page: p.lastPage != null && p.lastPage > 0 ? Math.min(p.lastPage, Math.max(0, (pageCount ?? p.lastPage + 1) - 1)) : null,
      spineIndex: p.lastSpineItemIndex,
      scrollPercent: p.lastScrollPercent,
      status: p.status,
    });
  }, [query.data, resume, pageCount]);

  const write = useCallback(async (page: number) => {
    inFlightRef.current = true;
    try {
      await putPosition(itemId, { lastPage: page });
      savedPageRef.current = page;
      savedStatusRef.current = page === 0 ? "unread" : "inprogress";
      dirtyRef.current = true;
    } catch {
      /* the next turn retries */
    } finally {
      inFlightRef.current = false;
      const q = queuedRef.current;
      queuedRef.current = null;
      if (q && savedPageRef.current !== -1 && q.page !== savedPageRef.current) void write(q.page);
    }
  }, [itemId]);

  /** A page turn: debounced, deduped, never a downgrade of a finished book. */
  const savePage = useCallback((page: number) => {
    lastPageRef.current = page;
    if (timerRef.current) clearTimeout(timerRef.current);
    if (savedPageRef.current === -1) return;
    if (page === savedPageRef.current && savedStatusRef.current === "inprogress") return;
    if (inFlightRef.current) { queuedRef.current = { page }; return; }
    timerRef.current = setTimeout(() => { timerRef.current = undefined; void write(page); }, PAGE_SAVE_MS);
  }, [write]);

  /** An EPUB spot: debounced and deduped to the millifraction. */
  const saveEpub = useCallback((spineIndex: number, scrollPercent: number) => {
    if (timerRef.current) clearTimeout(timerRef.current);
    const key = `${spineIndex}:${scrollPercent.toFixed(3)}`;
    if (key === savedEpubRef.current) return;
    timerRef.current = setTimeout(async () => {
      timerRef.current = undefined;
      try {
        await putPosition(itemId, { lastSpineItemIndex: spineIndex, lastScrollPercent: scrollPercent });
        savedEpubRef.current = key;
        dirtyRef.current = true;
      } catch { /* retried on the next move */ }
    }, EPUB_SAVE_MS);
  }, [itemId]);

  const markFinished = useCallback(async () => {
    if (timerRef.current) clearTimeout(timerRef.current);
    await apiMarkFinished(itemId);
    savedPageRef.current = -1;
    savedStatusRef.current = "finished";
    await invalidateAfter(qc, { kind: "position", itemId });
  }, [itemId, qc]);

  const reset = useCallback(async () => {
    if (timerRef.current) clearTimeout(timerRef.current);
    await resetPosition(itemId);
    savedPageRef.current = null;
    savedStatusRef.current = "unread";
    await invalidateAfter(qc, { kind: "position", itemId });
  }, [itemId, qc]);

  // Flush on unload (the debounce would never fire) and on unmount; tell the shelves on the way out.
  useEffect(() => {
    const onUnload = () => {
      if (timerRef.current) { clearTimeout(timerRef.current); timerRef.current = undefined; }
      const page = lastPageRef.current;
      if (page == null || savedPageRef.current === -1) return;
      if (page === savedPageRef.current && savedStatusRef.current === "inprogress") return;
      void putPosition(itemId, { lastPage: page }, { keepalive: true }).catch(() => {});
    };
    window.addEventListener("beforeunload", onUnload);
    return () => {
      window.removeEventListener("beforeunload", onUnload);
      onUnload();
      if (dirtyRef.current) void invalidateAfter(qc, { kind: "position", itemId });
    };
  }, [itemId, qc]);

  // A STABLE object: the readers keep it in effect deps (the EPUB save effect, the page-turn save), and
  // a fresh object per render would re-arm every debounce on every render.
  const data = query.data ?? null;
  const loading = query.isLoading;
  const isFinished = savedPageRef.current === -1 || data?.status === "finished";
  return useMemo(() => ({ position: data, loading, resume, isFinished, savePage, saveEpub, markFinished, reset }),
    [data, loading, resume, isFinished, savePage, saveEpub, markFinished, reset]);
}
