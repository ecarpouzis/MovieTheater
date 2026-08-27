/**
 * One job kind, observed: the server's Server-Sent Events feed (`event: status` per change, closed
 * on done/failed/stopped) with a 2 s poll behind it — for the first snapshot, for an intermediary
 * that swallows the stream, and for a browser without EventSource. The status a start/stop call
 * answers with is pushed in through `apply`, so the card never waits a tick to show what it just
 * did.
 *
 * The URLs are the SECTION's (`JobApi`), never this hook's: R9 S6 lifted the hook out of
 * `Pages/Books/admin` so any section's admin can observe its own jobs. A section without a live
 * feed omits `eventsUrl` and gets the poll alone.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import usePolling from "../hooks/usePolling";
import { isRunning, type JobApi, type JobStatus } from "./jobs";

export interface UseJobStatus {
  status: JobStatus | null;
  running: boolean;
  /** True while the SSE feed is connected. */
  live: boolean;
  apply: (status: JobStatus | null | undefined) => void;
  refresh: () => Promise<void>;
}

export const JOB_POLL_MS = 2000;

export default function useJobStatus(kind: string, api: JobApi, enabled = true): UseJobStatus {
  const [status, setStatus] = useState<JobStatus | null>(null);
  const [live, setLive] = useState(false);
  const [feedNonce, setFeedNonce] = useState(0);
  const sourceRef = useRef<EventSource | null>(null);
  const apiRef = useRef(api);
  apiRef.current = api;

  const refresh = useCallback(async () => {
    try {
      const s = await apiRef.current.fetchJob(kind);
      setStatus(s);
    } catch {
      /* 404 = the server has not run this kind since it started; keep what we have */
    }
  }, [kind]);

  // Poll while the feed is not live, or the job is running (a belt beside the braces).
  usePolling(() => { void refresh(); }, JOB_POLL_MS, { enabled: enabled && (!live || isRunning(status)) });

  // The SSE feed: opened while a job runs (or right after a start), closed when the server closes it.
  const running = isRunning(status);
  const feedUrl = enabled && running ? (apiRef.current.eventsUrl?.(kind) ?? null) : null;
  useEffect(() => {
    if (!feedUrl || typeof EventSource === "undefined") { setLive(false); return; }
    const es = new EventSource(feedUrl);
    sourceRef.current = es;
    es.onopen = () => setLive(true);
    es.addEventListener("status", (e) => {
      try {
        const s = JSON.parse((e as MessageEvent).data) as JobStatus;
        setStatus(s);
        if (!isRunning(s)) { es.close(); setLive(false); }
      } catch { /* a malformed frame is ignored; the poll still runs */ }
    });
    es.onerror = () => { setLive(false); };
    return () => { es.close(); sourceRef.current = null; setLive(false); };
  }, [feedUrl, feedNonce]);

  const apply = useCallback((s: JobStatus | null | undefined) => {
    if (!s) return;
    setStatus(s);
    if (isRunning(s)) setFeedNonce((n) => n + 1);
  }, []);

  return { status, running, live, apply, refresh };
}
