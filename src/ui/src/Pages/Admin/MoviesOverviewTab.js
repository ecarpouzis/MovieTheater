import { useEffect, useState } from "react";
import { Alert } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { AdminCard, AdminStats, NeedsAttention } from "../../admin/AdminOverview";
import { guardStatus } from "./MoviesUsersTab";

// The movie section's Overview: a REPORT built from endpoints the site already serves — no new API
// (R9 S6). What it reads, and what each number is:
//   /API/GetTotalMovieCount                          — the library's size
//   /API/Admin/IngestReview/List?scope=batch         — rows quarantined by a ReviewBatch
//   /API/Admin/IngestReview/List?scope=gaps          — approved series with unmapped/unplayable episodes
//   /API/Admin/IngestReview/SyncCandidates           — untracked files a sync classified
//   /API/Admin/Users                                 — accounts, and how many can stream
//   /API/Admin/PatchedArtifacts                      — the patched-binary guard's liveness
// A row with no endpoint behind it is NOT invented — it reports "—" and says why.
const EMPTY = { titles: null, pending: null, gaps: null, scan: null, users: null, guard: null, error: null };

async function readJson(promise) {
  const r = await promise;
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  return r.json();
}

export default function MoviesOverviewTab({ isAdmin }) {
  const [d, setD] = useState(EMPTY);

  useEffect(() => {
    let alive = true;
    const settle = (patch) => { if (alive) setD((prev) => ({ ...prev, ...patch })); };

    readJson(MovieAPI.getTotalMovieCount())
      .then((v) => settle({ titles: v?.totalCount ?? null }))
      .catch(() => settle({ titles: null }));
    readJson(MovieAPI.ingestReviewList("batch"))
      .then((v) => settle({ pending: (v.items ?? []).length, batches: v.batches ?? [] }))
      .catch((e) => settle({ pending: null, error: String(e.message || e) }));
    readJson(MovieAPI.ingestReviewList("gaps"))
      .then((v) => settle({ gaps: (v.items ?? []).length }))
      .catch(() => settle({ gaps: null }));
    readJson(MovieAPI.syncCandidatesList())
      .then((v) => settle({ scan: v.counts ?? {}, scanSeries: (v.seriesGroups ?? []).length }))
      .catch(() => settle({ scan: null }));
    if (isAdmin) {
      readJson(MovieAPI.adminGetUsers()).then((v) => settle({ users: Array.isArray(v) ? v : [] })).catch(() => settle({ users: null }));
      MovieAPI.adminGetPatchedArtifacts().then((r) => (r.ok ? r.json() : null)).then((g) => settle({ guard: g })).catch(() => settle({ guard: null }));
    }
    return () => { alive = false; };
  }, [isAdmin]);

  const scan = d.scan ?? {};
  const guardLine = d.guard === null && !isAdmin ? null : guardStatus(d.guard);
  const guardBad = !!d.guard && (d.guard.stale || d.guard.ok === false);

  return (
    <div className="adm-tab">
      {d.error && <Alert type="warning" showIcon title={`The review queue did not answer (${d.error}). The counts below may be incomplete.`} />}

      <AdminStats
        stats={[
          { label: "Titles", value: d.titles },
          { label: "Pending review", value: d.pending, bad: (d.pending ?? 0) > 0 },
          { label: "Episode gaps", value: d.gaps, bad: (d.gaps ?? 0) > 0 },
          { label: "Scan candidates", value: (scan.upgrades ?? 0) + (scan.newTitles ?? 0) + (scan.unclassified ?? 0) },
          { label: "Accounts", value: d.users ? d.users.length : null },
          { label: "Can stream", value: d.users ? d.users.filter((u) => u.hasPassword).length : null },
        ]}
      />

      <NeedsAttention
        basePath="/movies/admin"
        description="Each row names the tab that fixes it."
        rows={[
          { key: "pending", label: "Rows quarantined by an ingest batch", count: d.pending, tab: "review-ingest", detail: "A ReviewBatch hides a row from every browse surface until it is approved or rejected." },
          { key: "gaps", label: "Approved series with unmapped episodes", count: d.gaps, tab: "review-ingest", tone: "warn", detail: "No watch button on those episodes until a file is mapped (the Gaps scope)." },
          { key: "upgrades", label: "Sync-scan upgrades waiting", count: scan.upgrades ?? null, tab: "review-ingest", tone: "warn", detail: "A better file for a title we already have — approving re-points it in place." },
          { key: "new", label: "Sync-scan new titles waiting", count: scan.newTitles ?? null, tab: "review-ingest", tone: "warn", detail: "Untracked files the scan believes are titles the library does not hold." },
          { key: "unclassified", label: "Sync-scan rows the classifier could not place", count: scan.unclassified ?? null, tab: "review-ingest", detail: "These need a human call before anything else can act on them." },
          { key: "nopw", label: "Accounts with no streaming password", count: d.users ? d.users.filter((u) => !u.hasPassword).length : null, tab: "users", tone: "warn", detail: "Passwordless accounts can browse and track, but not stream." },
          ...(isAdmin ? [{ key: "guard", label: "Patched-binary guard", count: guardBad ? 1 : 0, always: guardBad, tone: "bad", detail: guardLine?.text }] : []),
        ]}
      />

      <AdminCard
        title="Where the rest of the movie tooling lives"
        description="These are CLI jobs, not site buttons — the ingest pipeline (sync-library-to-db, scrape-imdb, map-series-files) runs from the API host. This page reports what those jobs left behind."
      >
        <div className="adm-facts">
          <span>Ratings are a member surface — <code>/rate</code> is on the sider, and the Rate tab here is the same page.</span>
          <span>Poster repair and thumbnail backfill live in the Review ingest tab's own actions.</span>
        </div>
      </AdminCard>
    </div>
  );
}
