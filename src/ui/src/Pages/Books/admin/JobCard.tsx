/**
 * One host job as a card: its state, progress (when the host knows its remaining count), the last
 * batch line, the error, and Start / Stop. Start is disabled while the kind is running; a 409 from
 * the host ("already running") is shown as a warning, not a failure. The card owns nothing about
 * WHAT the job does — the tab passes the start call, which may carry its own parameters.
 */
import { Alert, Button, Progress, Space, Tag, message } from "antd";
import type { ReactNode } from "react";
import { useState } from "react";
import { AdminApiError, isRunning, jobPercent, stopJob, type JobStart, type JobStatus } from "./adminApi";
import useJobStatus from "./useJobStatus";

const STATE_COLOR: Record<string, string> = { idle: "default", running: "processing", stopping: "warning", done: "success", failed: "error", stopped: "default" };

export function JobStateTag({ status }: { status: JobStatus | null }) {
  const state = status?.state ?? "idle";
  return <Tag color={STATE_COLOR[state] ?? "default"} className="bka-state">{state}</Tag>;
}

export function JobProgress({ status }: { status: JobStatus | null }) {
  if (!status) return null;
  const pct = jobPercent(status);
  const running = isRunning(status);
  return (
    <div className="bka-job-progress">
      {pct != null
        ? <Progress percent={pct} status={status.state === "failed" ? "exception" : running ? "active" : undefined} size="small" />
        : running ? <Progress percent={100} status="active" showInfo={false} size="small" /> : null}
      <div className="bka-job-nums">
        <span>{status.processed.toLocaleString()} processed</span>
        {status.remaining > 0 && <span>· {status.remaining.toLocaleString()} remaining</span>}
        {status.failed > 0 && <span className="bka-warn">· {status.failed.toLocaleString()} failed</span>}
        {status.batches > 0 && <span>· {status.batches} batches</span>}
      </div>
      {status.lastLine && <div className="bka-job-line">{status.lastLine}</div>}
      {status.error && <Alert type="error" showIcon title={status.error} className="bka-job-error" />}
    </div>
  );
}

export interface JobCardProps {
  kind: string;
  title: string;
  description?: ReactNode;
  /** The start call; omit to render an observe-only card. */
  start?: () => Promise<JobStart>;
  startLabel?: string;
  /** A destructive start: the button asks first. */
  confirm?: ReactNode;
  /** Extra controls beside Start (a root picker, a reset toggle). */
  controls?: ReactNode;
  children?: ReactNode;
  onStarted?: (s: JobStart) => void;
  onFinished?: (s: JobStatus) => void;
}

export default function JobCard({ kind, title, description, start, startLabel = "Start", controls, children, onStarted }: JobCardProps) {
  const job = useJobStatus(kind);
  const [busy, setBusy] = useState(false);

  const onStart = async () => {
    if (!start) return;
    setBusy(true);
    try {
      const r = await start();
      job.apply(r.job);
      onStarted?.(r);
      message.success(`${title}: started (${r.job.processed.toLocaleString()} in the first batch).`);
    } catch (e) {
      if (e instanceof AdminApiError && e.status === 409) { message.warning(e.message || `${title} is already running.`); void job.refresh(); }
      else message.error(e instanceof Error ? e.message : `${title} could not start.`);
    } finally {
      setBusy(false);
    }
  };
  const onStop = async () => {
    setBusy(true);
    try { job.apply(await stopJob(kind)); message.info(`${title}: stopping at the next batch boundary.`); }
    catch (e) { message.error(e instanceof Error ? e.message : "Stop failed."); }
    finally { setBusy(false); }
  };

  return (
    <section className="bka-card" data-job={kind}>
      <header className="bka-card-head">
        <div className="bka-card-text">
          <h3 className="bka-card-title">{title} <JobStateTag status={job.status} />{job.live && <Tag color="green" className="bka-live">live</Tag>}</h3>
          {description && <p className="bka-card-desc">{description}</p>}
        </div>
        <Space wrap>
          {controls}
          {start && <Button type="primary" onClick={onStart} disabled={job.running || busy} loading={busy && !job.running}>{startLabel}</Button>}
          {job.running && <Button danger onClick={onStop} disabled={busy || job.status?.state === "stopping"}>Stop</Button>}
        </Space>
      </header>
      <JobProgress status={job.status} />
      {children}
    </section>
  );
}
