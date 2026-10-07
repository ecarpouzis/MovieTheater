import { useEffect, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import SectionIndexRail from "../catalog/rail/SectionIndexRail";
import { MovieAPI } from "../MovieAPI";
import { REQUEST_SECTIONS } from "../Pages/Requests/requestSections";
import { NavUserBlock } from "./navShared";

/**
 * The Requests rail (2026-10-06): the user block, then a counted index — the open queue, the viewer's
 * own, the resolved history, and the open count per part of the site. Every row carries a number the
 * bar's one tab does not (the "an index row must say something the tabs do not" rule), and every row
 * is a URL the page reads (`?status=`, `?mine=`, `?section=`), so the rail and the page cannot drift.
 *
 * Counts come from /API/Requests/Summary, fetched once per mount and again whenever the page announces
 * a change (the `requests:changed` event the page fires after a write) — never a poll.
 */
export default function RequestsNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, railVisible = true }) {
  const location = useLocation();
  const history = useHistory();
  const [summary, setSummary] = useState(null);

  useEffect(() => {
    if (!railVisible || !userData) { setSummary(null); return undefined; }
    let cancelled = false;
    const load = () => {
      MovieAPI.getRequestsSummary()
        .then((r) => (r.ok ? r.json() : null))
        .then((s) => { if (!cancelled && s) setSummary(s); })
        .catch(() => { /* the rail shows no counts; the page has its own failure surface */ });
    };
    load();
    window.addEventListener("requests:changed", load);
    return () => { cancelled = true; window.removeEventListener("requests:changed", load); };
  }, [railVisible, userData]);

  const params = new URLSearchParams(location.search);
  const status = params.get("status") ?? "open";
  const mine = params.get("mine") === "1";
  const section = params.get("section");
  const activeKey = section ? `sec:${section}` : mine ? "mine" : status === "open" ? "open" : status === "closed" ? "closed" : "";

  const groups = [
    {
      key: "queue", label: "Queue",
      views: [
        { key: "open", label: "Open requests", path: "/requests", count: summary?.open ?? null, waiting: !!summary?.canResolve && (summary?.needsConfirmation ?? 0) > 0 },
        { key: "mine", label: "Mine", path: "/requests?mine=1", count: summary?.mine ?? null },
        { key: "closed", label: "Resolved", path: "/requests?status=closed" },
      ],
    },
    {
      key: "sections", label: "For",
      views: REQUEST_SECTIONS.map((s) => ({ key: `sec:${s.key}`, label: s.label, path: `/requests?section=${s.key}`, count: summary?.bySection?.[s.key] ?? (summary ? 0 : null) })),
    },
  ];

  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn} setSettingsModalOpen={setSettingsModalOpen} />
      {userData && (
        <SectionIndexRail groups={groups} activeKey={activeKey} ariaLabel="Request views" onNavigate={(path) => history.push(path)} />
      )}
    </>
  );
}
