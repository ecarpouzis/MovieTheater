/**
 * One job kind, observed: the host's Server-Sent Events feed (`/admin/{kind}/events`, `event: status`
 * per change, closes on done/failed/stopped) with a 2 s poll behind it — for the first snapshot,
 * for an intermediary that swallows the stream, and for a browser without EventSource. The status
 * a start/stop call answers with is pushed in through `apply`, so the card never waits a tick to
 * show what it just did.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import usePolling from "../../../hooks/usePolling";
import { fetchJob, isRunning, jobEventsUrl, type JobStatus } from "./adminApi";

export interface UseJobStatus {
  status: JobStatus | null;
  running: boolean;
  /** True while the SSE feed is connected. */
  live: boolean;
  apply: (status: JobStatus | null | undefined) => void;
  refresh: () => Promise<void>;
}

export const JOB_POLL_MS = 2000;

export default function useJobStatus(kind: string, enabled = true): UseJobStatus {
  const [status, setStatus] = useState<JobStatus | null>(null);
  const [live, setLive] = useState(false);
  const [feedNonce, setFeedNonce] = useState(0);
  const sourceRef = useRef<EventSource | null>(null);

  const refresh = useCallback(async () => {
    try {
      const s = await fetchJob(kind);
      setStatus(s);
    } catch {
      /* 404 = the host has not run this kind since it started; keep what we have */
    }
  }, [kind]);

  // Poll while the feed is not live, or the job is running (a belt beside the braces).
  usePolling(() => { void refresh(); }, JOB_POLL_MS, { enabled: enabled && (!live || isRunning(status)) });

  // The SSE feed: opened while a job runs (or right after a start), closed when the host closes it.
  const running = isRunning(status);
  useEffect(() => {
    if (!enabled || !running || typeof EventSource === "undefined") { setLive(false); return; }
    const es = new EventSource(jobEventsUrl(kind));
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
  }, [kind, enabled, running, feedNonce]);

  const apply = useCallback((s: JobStatus | null | undefined) => {
    if (!s) return;
    setStatus(s);
    if (isRunning(s)) setFeedNonce((n) => n + 1);
  }, []);

  return { status, running, live, apply, refresh };
}
