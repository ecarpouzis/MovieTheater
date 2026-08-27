import AdminShell from "../../admin/AdminShell";
import { AdminCard, AdminStats, NeedsAttention } from "../../admin/AdminOverview";
import PhotoReview from "./PhotoReview";
import PhotoDupes from "./PhotoDupes";
import PhotoTagQueue from "./PhotoTagQueue";
import PhotoGoogle from "./PhotoGoogle";

// `/photos/admin?tab=` — the album's curation tools on the site's admin shell (R9 S6). The four
// surfaces are exactly the pages that used to own `/photos/review`, `/photos/dupes` and
// `/photos/tag` (those redirect here now), plus the Google-archive reconciler, which never had a
// route of its own — it is rendered inside PhotoReview, and is offered here as its own tab too so
// it can be linked to. Their internals are untouched.
//
// This page is mounted INSIDE PhotosPage's Switch, not App's, because every tab needs the section's
// live state (the people list, the refresh key, the "something changed" beat) — plumbing that state
// out to App would have been a bigger change than the tabs themselves.

// The Overview is a REPORT off ONE existing endpoint: `/API/Photos/Status`, which already answers
// every count the album's rails and tabs read (it is a handful of grouped aggregates, one query per
// table, by design). No new API.
export function PhotosOverviewTab({ status, basePath = "/photos/admin" }) {
  const s = status ?? {};
  return (
    <div className="adm-tab">
      <AdminStats
        stats={[
          { label: "Photographs", value: s.photos },
          { label: "Videos", value: s.videos },
          { label: "On the timeline", value: s.timelineCount },
          { label: "In the gallery", value: s.archived },
          { label: "Albums", value: s.albums },
          { label: "People", value: s.namedPeople },
          { label: "Hidden", value: s.hidden },
          { label: "Missing from disk", value: s.missing, bad: (s.missing ?? 0) > 0 },
        ]}
      />

      <NeedsAttention
        basePath={basePath}
        description="Each row names the tab that fixes it."
        rows={[
          { key: "missing", label: "Files the walk stopped finding", count: s.missing ?? null, tab: "review", tone: "bad", detail: "Flagged, never deleted (§2.5) — drift is visible rather than discovered." },
          { key: "quarantine", label: "Ingest batches waiting for approval", count: s.quarantinedBatches ?? null, tab: "review", detail: "A quarantined batch is out of the album until somebody approves it." },
          { key: "proposals", label: "Hide proposals waiting", count: s.pendingHideProposals ?? null, tab: "review", tone: "warn", detail: "Never bulk-accept a misc-folder proposal — they are read one at a time." },
          { key: "dupes", label: "Duplicate groups waiting for a keeper", count: s.pendingDupeGroups ?? null, tab: "dupes", detail: s.pendingNearGroups ? `${s.pendingNearGroups.toLocaleString()} of them are NEAR matches, which need the closer look.` : undefined },
          { key: "faces", label: "Unnamed face groups", count: s.unnamedFaceGroups ?? null, tab: "tag", tone: "warn", detail: "An imported cluster is a queue item until it is named." },
          { key: "suggested", label: "Suggested person tags to confirm", count: s.pendingTagSuggestions ?? null, tab: "tag" },
          { key: "untagged", label: "Photographs with nobody tagged", count: s.untaggedPhotos ?? null, tab: "tag", tone: "ok" },
          { key: "google", label: "Google-archive items the library does not hold", count: s.googleOnly ?? null, tab: "google", detail: "Keep or ignore, one at a time (§2.10)." },
          { key: "unplayable", label: "Videos on disk that cannot play", count: (s.videos ?? 0) - (s.videosSynced ?? 0), tab: "review", tone: "warn", detail: "No Jellyfin item id — browsable and taggable, but not playable (§2.3)." },
          { key: "store", label: "The curation store is not configured", count: s.curationStore === false ? 1 : 0, always: s.curationStore === false, tone: "bad", detail: "Review cannot record a decision until it is." },
          { key: "plane", label: "The photo data plane is not configured", count: s.dataPlane === false ? 1 : 0, always: s.dataPlane === false, tone: "bad" },
        ]}
      />

      <AdminCard
        title="What this page does not report"
        description="Scanning, dating and the Immich sidecar are host-side jobs with no status endpoint on the site — nothing here can count them, so nothing here pretends to."
      >
        <div className="adm-facts">
          <span>Immich sidecar <code>{s.immich ? "configured" : "absent"}</code></span>
          <span>Video playback <code>{s.videoPlayback ? "configured" : "absent"}</code></span>
          <span>Google archive holds <code>{(s.googleItems ?? 0).toLocaleString()}</code> items</span>
        </div>
      </AdminCard>
    </div>
  );
}

export default function PhotosAdminPage({ status, people, refreshPeople, changed, refreshKey }) {
  const admin = !!status?.admin;
  return (
    <AdminShell
      section="photos"
      eyebrow="Family album administration"
      allowed={admin}
      deniedBody="The album's curation tools are for administrators. Members can browse, tag and make albums from the section itself."
      tabs={[
        { key: "overview", label: "Overview", render: () => <PhotosOverviewTab status={status} /> },
        { key: "review", label: "Review", render: () => <PhotoReview key={`review-${refreshKey}`} admin={admin} onChanged={changed} /> },
        { key: "dupes", label: "Dupes", render: () => <PhotoDupes key={`dupes-${refreshKey}`} onChanged={changed} /> },
        { key: "tag", label: "Tag queue", render: () => <PhotoTagQueue key={`tag-${refreshKey}`} people={people} onReloadPeople={refreshPeople} onChanged={changed} /> },
        { key: "google", label: "Google", render: () => <PhotoGoogle key={`google-${refreshKey}`} /> },
      ]}
    />
  );
}
