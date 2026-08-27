/**
 * `/<section>/admin?tab=` — the ONE operator shell every section wears (R9 S6, lifted out of
 * `Pages/Books/admin/AdminPage.tsx`).
 *
 * The tab is in the URL, so "the Users tab" is a real link and the Overview's needs-attention rows
 * can point at the tab that fixes them. Only the active tab's body is mounted — an operator tab is
 * usually a page's worth of fetching, and ten of them mounted at once is ten queries nobody asked
 * for. `when: false` REMOVES a tab rather than disabling it (the Long Box rule the bar follows).
 *
 * Gating is a courtesy: the route re-checks the same `isAdmin` flag the bar uses so a member who
 * types the URL gets a plate instead of a broken page — but every endpoint behind these tabs is
 * independently gated on the server, which is the real gate.
 */
import { Tabs } from "antd";
import type { ReactNode } from "react";
import { Suspense } from "react";
import { useHistory, useLocation } from "react-router-dom";
import "./admin.css";

export interface AdminTabDef {
  key: string;
  label: string;
  /** Mounted only while this tab is the active one. */
  render: () => ReactNode;
  /** Absent or true = shown; false REMOVES the tab. */
  when?: boolean;
}

export interface AdminShellProps {
  /** The section key (`movies`, `photos`, …) — lands on the root as `data-admin-section`. */
  section: string;
  eyebrow: string;
  title?: string;
  tabs: AdminTabDef[];
  /** Extra classes on the shell root (Books keeps `books-admin books-surface` for its skin). */
  className?: string;
  /** False draws the refusal plate instead of the tabs. */
  allowed?: boolean;
  deniedTitle?: string;
  deniedBody?: ReactNode;
}

/** The tabs a shell actually shows, after `when`. */
export function visibleTabs(tabs: AdminTabDef[]): AdminTabDef[] {
  return tabs.filter((t) => t.when !== false);
}

/** The `?tab=` a URL asks for, falling back to the first visible tab. */
export function readAdminTab(search: string, tabs: AdminTabDef[]): string {
  const shown = visibleTabs(tabs);
  const asked = new URLSearchParams(search || "").get("tab");
  if (asked && shown.some((t) => t.key === asked)) return asked;
  return shown[0]?.key ?? "";
}

/** The link an Overview row (or anything else) uses to send the operator to a sibling tab. */
export function adminTabHref(basePath: string, tab: string): string {
  return `${basePath}?tab=${encodeURIComponent(tab)}`;
}

export default function AdminShell({ section, eyebrow, title = "Admin", tabs, className, allowed = true, deniedTitle = "Administrators only", deniedBody }: AdminShellProps) {
  const history = useHistory();
  const location = useLocation();
  const shown = visibleTabs(tabs);
  const active = readAdminTab(location.search, tabs);

  const setTab = (next: string) => {
    const p = new URLSearchParams(location.search);
    p.set("tab", next);
    history.push({ pathname: location.pathname, search: `?${p.toString()}` });
  };

  return (
    <div className={`admin-page${className ? ` ${className}` : ""}`} data-admin-section={section}>
      <header className="adm-head">
        <div className="adm-eyebrow">{eyebrow}</div>
        <h1 className="adm-title">{title}</h1>
      </header>
      {!allowed ? (
        <div className="adm-plate">
          <h2>{deniedTitle}</h2>
          <p>{deniedBody ?? "These tools are for administrators. Every endpoint behind them checks again on the server."}</p>
        </div>
      ) : (
        <Tabs
          activeKey={active}
          onChange={setTab}
          className="adm-tabs"
          items={shown.map((t) => ({
            key: t.key,
            label: t.label,
            // A tab body is commonly a lazy chunk (Books' ten are); the boundary lives here so no
            // section has to remember it.
            children: active === t.key ? <Suspense fallback={<div className="adm-muted">Loading…</div>}>{t.render()}</Suspense> : null,
          }))}
        />
      )}
    </div>
  );
}
