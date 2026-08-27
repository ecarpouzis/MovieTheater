/**
 * The job vocabulary every section's admin speaks (R9 S6, lifted out of `Pages/Books/admin`).
 *
 * A long operation is a JOB: a start answers with the first batch's numbers, the loop runs on the
 * server, a second start of the same kind is a 409, and a stop lands at the next batch boundary.
 * Nothing here loops — a card OBSERVES a job (`useJobStatus`) and a whole paged list is walked by
 * the caller (`driveBatches`). That split is the house rule for bulk work: bounded per call,
 * progress reported, resumable, and the driver lives in the client.
 *
 * A section supplies a `JobApi` adapter (Books: `Pages/Books/admin/adminApi.ts`); the shell owns no
 * URL of its own, so a section whose jobs live behind a different controller reuses every card.
 */

export type JobState = "idle" | "running" | "stopping" | "done" | "failed" | "stopped";

export interface JobStatus {
  kind: string;
  state: JobState;
  processed: number;
  remaining: number;
  nextCursor: string | null;
  failed: number;
  startedAt: string | null;
  finishedAt: string | null;
  error: string | null;
  lastLine: string | null;
  batches: number;
}

export interface JobStart {
  job: JobStatus;
  statusUrl: string;
}

/**
 * An admin call that failed with the server's OWN sentence in it — the tabs show that sentence
 * ("Root 3 still holds 412 items; scan it empty first.") rather than a status code.
 */
export class AdminApiError extends Error {
  constructor(public readonly status: number, public readonly url: string, message: string) {
    super(message);
    this.name = "AdminApiError";
  }
}

/** A job's progress as 0–100 when it knows its remaining count; null while it cannot say. */
export function jobPercent(j: JobStatus | null | undefined): number | null {
  if (!j) return null;
  const total = j.processed + j.remaining;
  if (total <= 0) return j.state === "done" ? 100 : null;
  return Math.max(0, Math.min(100, Math.round((j.processed / total) * 100)));
}

export const isRunning = (j: JobStatus | null | undefined) => !!j && (j.state === "running" || j.state === "stopping");

/**
 * What a section hands the shell so its cards can observe and stop a job. `eventsUrl` is optional:
 * a section with no live feed falls back to the poll alone.
 */
export interface JobApi {
  fetchJob(kind: string, signal?: AbortSignal): Promise<JobStatus>;
  stopJob(kind: string): Promise<JobStatus>;
  eventsUrl?(kind: string): string | null;
}
