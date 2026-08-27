/**
 * The Overview tab is a REPORT, not a dashboard (the Long Box's rule, R9 S6): stat tiles say what
 * the section HOLDS, and the "needs attention" rows say what is WRONG — each row naming the tab
 * that fixes it and linking straight to it. A row with nothing wrong is not drawn; a section with
 * nothing wrong says so in a sentence.
 *
 * Every number here comes from an endpoint the section ALREADY serves. When a count genuinely has
 * no source, the row says that out loud (`unavailable`) rather than inventing one — a report that
 * guesses is worse than a report with a gap in it.
 */
import { Statistic } from "antd";
import type { ReactNode } from "react";
import { useHistory } from "react-router-dom";
import { adminTabHref } from "./AdminShell";
import "./admin.css";

export interface StatDef {
  label: string;
  value: number | string | null | undefined;
  /** Drawn in the alarm colour when true (a non-zero "broken" count). */
  bad?: boolean;
}

export function AdminStats({ stats }: { stats: StatDef[] }) {
  return (
    <div className="adm-stats">
      {stats.map((s) => (
        <Statistic
          key={s.label}
          title={s.label}
          value={s.value ?? "—"}
          // `styles.content`, not the deprecated `valueStyle` (antd 6).
          styles={s.bad ? { content: { color: "var(--rating-bad, #c0392b)" } } : undefined}
        />
      ))}
    </div>
  );
}

export interface AttentionRow {
  key: string;
  /** What is wrong, in the operator's words. */
  label: string;
  /** How many; null = no endpoint reports this (the row says so and does not link). */
  count: number | null;
  detail?: ReactNode;
  /** The tab that fixes it — the row becomes a link to `?tab=`. */
  tab?: string;
  /** A route to send the operator to instead of a tab (a tool that is not a tab). */
  to?: string;
  tone?: "bad" | "warn" | "ok";
  /** Draw the row even at zero (a standing fact, e.g. "the guard is silent"). */
  always?: boolean;
}

export interface NeedsAttentionProps {
  basePath: string;
  rows: AttentionRow[];
  /** Shown when every row is clear. */
  clearText?: string;
  title?: string;
  description?: ReactNode;
}

/**
 * The rows worth showing: a count of 0 is not a problem, an unknown count (null) IS worth saying,
 * and `always` pins a row that is a standing fact rather than a queue.
 */
export function attentionRows(rows: AttentionRow[]): AttentionRow[] {
  return rows.filter((r) => r.always || r.count === null || (r.count ?? 0) > 0);
}

export function NeedsAttention({ basePath, rows, clearText = "Nothing needs attention.", title = "Needs attention", description }: NeedsAttentionProps) {
  const history = useHistory();
  const shown = attentionRows(rows);
  const go = (r: AttentionRow) => {
    const target = r.to ?? (r.tab ? adminTabHref(basePath, r.tab) : null);
    if (target) history.push(target);
  };
  return (
    <section className="adm-card">
      <header className="adm-card-head">
        <div className="adm-card-text">
          <h3 className="adm-card-title">{title}</h3>
          {description && <p className="adm-card-desc">{description}</p>}
        </div>
      </header>
      {shown.length === 0 ? (
        <div className="adm-clear">{clearText}</div>
      ) : (
        <div className="adm-attention">
          {shown.map((r) => {
            const linked = !!(r.tab || r.to);
            const body = (
              <>
                <span className="adm-att-count" data-tone={r.tone ?? (r.count === null ? "ok" : "bad")}>
                  {r.count === null ? "—" : r.count.toLocaleString()}
                </span>
                <span className="adm-att-label">{r.label}</span>
                {r.detail && <span className="adm-att-detail">{r.detail}</span>}
                {linked && <span className="adm-att-goto">go →</span>}
              </>
            );
            return linked ? (
              <button key={r.key} type="button" className="adm-att-row" onClick={() => go(r)}>{body}</button>
            ) : (
              <div key={r.key} className="adm-att-row">{body}</div>
            );
          })}
        </div>
      )}
    </section>
  );
}

/** A plain card wrapper, for the facts an Overview states beside its two blocks. */
export function AdminCard({ title, description, children, actions }: { title: string; description?: ReactNode; children?: ReactNode; actions?: ReactNode }) {
  return (
    <section className="adm-card">
      <header className="adm-card-head">
        <div className="adm-card-text">
          <h3 className="adm-card-title">{title}</h3>
          {description && <p className="adm-card-desc">{description}</p>}
        </div>
        {actions}
      </header>
      {children}
    </section>
  );
}
