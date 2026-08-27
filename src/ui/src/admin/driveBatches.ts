/**
 * The client-driven loop for a WHOLE list the server only serves in pages (every decision, every
 * broken file, every dedup group): the caller's `step` fetches one page and reports what it got;
 * this accumulates, reports progress, honours an abort, RESUMES from a cursor, and BREAKS on no
 * progress — a page that returns nothing new, or the same cursor twice — so a server that keeps
 * answering the same page can never spin the browser forever. The house rule for bulk work, on the
 * client side (R9 S6: lifted out of `Pages/Books/admin` so every section's admin drives its jobs
 * the same way).
 */
export class NoProgressError extends Error {
  constructor(public readonly cursor: number) {
    super(`No progress at cursor ${cursor}: the same page came back twice.`);
    this.name = "NoProgressError";
  }
}

export interface BatchStep<T> {
  (cursor: number, signal?: AbortSignal): Promise<{ items: T[]; nextCursor: number | null }>;
}

export interface DriveOptions<T> {
  onProgress?: (info: { loaded: number; cursor: number; batch: T[] }) => void;
  signal?: AbortSignal;
  /** A hard ceiling on the number of steps (default 500). */
  maxSteps?: number;
  /**
   * Resume: the cursor to start from (default 0). An interrupted drive reports the cursor it
   * stopped at through `onProgress`, and handing that back here continues instead of restarting.
   */
  from?: number;
}

export async function driveBatches<T>(step: BatchStep<T>, opts: DriveOptions<T> = {}): Promise<T[]> {
  const out: T[] = [];
  let cursor = opts.from ?? 0;
  let steps = 0;
  const maxSteps = opts.maxSteps ?? 500;
  while (steps < maxSteps) {
    if (opts.signal?.aborted) break;
    const r = await step(cursor, opts.signal);
    steps += 1;
    out.push(...r.items);
    opts.onProgress?.({ loaded: out.length, cursor, batch: r.items });
    if (r.nextCursor == null) break;
    if (r.items.length === 0 || r.nextCursor <= cursor) throw new NoProgressError(cursor);
    cursor = r.nextCursor;
  }
  return out;
}

/** A page step over a `{ totalCount, skip, top, items }` envelope. */
export function pagedStep<T>(fetchPage: (skip: number, top: number, signal?: AbortSignal) => Promise<{ totalCount: number; items: T[] }>, top = 100): BatchStep<T> {
  return async (cursor, signal) => {
    const r = await fetchPage(cursor, top, signal);
    const next = cursor + r.items.length;
    return { items: r.items, nextCursor: next < r.totalCount && r.items.length > 0 ? next : null };
  };
}
