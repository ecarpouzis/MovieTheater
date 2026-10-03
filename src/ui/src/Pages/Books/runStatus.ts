/**
 * A comic run's publication status (books-run-status) as the series modal shows it: one status chip — styled like
 * the Volume / Omnibus level chips — and a "held" note saying how much of the run the library has.
 */
export type RunStatusName = "Unknown" | "Ongoing" | "Completed" | "Ended" | "Cancelled";

export interface SeriesRunStatus {
  status: RunStatusName;
  planned: number | null;
  published: number | null;
  held: number | null;
  basis: string | null;
}

export interface RunBadge { status: RunStatusName; label: string; title: string; held: string | null; complete: boolean }

const issues = (n: number) => `${n.toLocaleString()} ${n === 1 ? "issue" : "issues"}`;

/** The issues a reader would call "the whole run": a finished limited run is its plan, anything else what came out. */
export function runTarget(r: SeriesRunStatus): number | null {
  if ((r.status === "Completed" || r.status === "Cancelled") && r.planned != null)
    return r.published != null ? Math.min(r.planned, r.published) : r.planned;
  return r.published;
}

export function runBadge(r: SeriesRunStatus | null | undefined): RunBadge | null {
  if (!r || r.status === "Unknown") return null;
  const p = r.published;
  const label =
    r.status === "Ongoing" ? (r.planned != null && p != null ? `Ongoing · ${p} of ${r.planned}` : "Ongoing")
    : r.status === "Completed" ? (p != null ? `Complete · ${issues(p)}` : "Complete")
    : r.status === "Ended" ? (p != null ? `Ended · ${issues(p)}` : "Ended")
    : p != null ? `Cancelled at #${p}${r.planned != null && r.planned > p ? ` of ${r.planned}` : ""}` : "Cancelled";
  const title = {
    Ongoing: "Still being published",
    Completed: r.planned != null ? "A limited run that reached its planned length" : "Finished — a limited series, one-shot or graphic novel",
    Ended: "An open-ended series that has stopped",
    Cancelled: "Stopped before its planned end",
  }[r.status] + (r.basis ? ` (${r.basis})` : "");

  const target = runTarget(r);
  let held: string | null = null;
  let complete = false;
  if (r.held != null && target != null && target > 0) {
    complete = r.held >= target;
    held = complete
      ? (r.status === "Ongoing" ? "Have every issue so far" : "Complete run in the library")
      : `Have ${r.held.toLocaleString()} of ${target.toLocaleString()}`;
  }
  return { status: r.status, label, title, held, complete };
}
