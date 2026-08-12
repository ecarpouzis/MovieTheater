import { useCallback, useEffect, useState } from "react";
import { Spin, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import PhotoGoogle from "./PhotoGoogle";

// The review surface (docs/photos-plan.md §2.9 suggested-hide batches, §2.5 ingest-batch quarantine).
//
// Two lists, both about batches a machine proposed and a human decides:
//
//  · Suggested hides — what `photos-suggest-hide` thinks is clutter. NOTHING is hidden until someone
//    accepts the batch here, which is the entire reason the pass writes a proposal instead of a flag.
//  · New ingests — assets from an ingest nobody has approved are kept out of the timeline until they
//    are. Admin-only, because it describes the pipeline rather than the photos.

export default function PhotoReview({ admin, onChanged }) {
  const [proposals, setProposals] = useState(null);
  const [batches, setBatches] = useState(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const response = await MovieAPI.getPhotosHideProposals(false);
      setProposals(response.ok ? await response.json() : { configured: false, proposals: [] });
    } catch {
      setProposals({ configured: false, proposals: [] });
    }

    if (!admin) {
      setBatches(null);
      return;
    }
    try {
      const response = await MovieAPI.getPhotosIngestBatches();
      setBatches(response.ok ? await response.json() : null);
    } catch {
      setBatches(null);
    }
  }, [admin]);

  useEffect(() => {
    load();
  }, [load]);

  const decide = async (batchId, decision) => {
    setBusy(true);
    try {
      const response = await MovieAPI.decidePhotosHideProposal(batchId, decision);
      if (!response.ok) {
        message.error("Could not record that decision.");
        return;
      }
      const body = await response.json();
      message.success(decision === "accept" ? `${body.applied} hidden.` : "Batch rejected; nothing was hidden.");
      await load();
      onChanged?.();
    } finally {
      setBusy(false);
    }
  };

  const approve = async (groupKey) => {
    setBusy(true);
    try {
      const response = await MovieAPI.approvePhotosIngestBatches({ groupKey });
      if (!response.ok) {
        message.error("Could not approve that ingest.");
        return;
      }
      await load();
      onChanged?.();
    } finally {
      setBusy(false);
    }
  };

  if (!proposals) return <Spin />;

  return (
    <div className="photo-review">
      <section>
        <h2 className="photos-panel-head">Suggested hides</h2>
        {!proposals.configured && (
          <p className="photos-note">
            No review directory is configured on this host, so proposals cannot be read here. Nothing
            is hidden as a result — this surface fails open on purpose.
          </p>
        )}
        {proposals.configured && proposals.proposals.length === 0 && (
          <p className="photos-note">Nothing is waiting. Run the suggest-hide pass to propose a batch.</p>
        )}

        {proposals.proposals.map((proposal) => (
          <div className="photo-review-batch" key={proposal.batchId}>
            <div className="photo-review-head">
              <span className="photo-review-title">{proposal.batchId}</span>
              <span className="photo-review-count">{proposal.count.toLocaleString()} photos</span>
            </div>
            <ul className="photo-review-rules">
              {Object.entries(proposal.rules || {}).map(([rule, count]) => (
                <li key={rule}>
                  <span className="photo-review-rule">{rule}</span>
                  <span className="photo-review-count">{count.toLocaleString()}</span>
                </li>
              ))}
            </ul>
            {proposal.samplePaths?.length > 0 && (
              <ul className="photo-review-samples">
                {proposal.samplePaths.map((path) => (
                  <li key={path}>{path}</li>
                ))}
              </ul>
            )}
            {!proposal.complete && (
              <p className="photos-note">This pass has not finished the collection yet — the batch may grow.</p>
            )}
            <div className="photo-review-actions">
              <button type="button" className="photos-button" disabled={busy} onClick={() => decide(proposal.batchId, "accept")}>
                Hide all of these
              </button>
              <button type="button" className="photos-button" disabled={busy} onClick={() => decide(proposal.batchId, "reject")}>
                Reject
              </button>
            </div>
          </div>
        ))}
      </section>

      {admin && batches && (
        <section>
          <h2 className="photos-panel-head">New ingests</h2>
          {batches.quarantineActive === false && (
            <p className="photos-note">
              Too many unreviewed ingests ({batches.quarantinedBatches}) — the timeline has stopped
              filtering by them. Approve some to turn quarantine back on.
            </p>
          )}
          {batches.groups.filter((g) => !g.approved).length === 0 && (
            <p className="photos-note">Every ingest has been approved into the timeline.</p>
          )}

          {batches.groups
            .filter((group) => !group.approved)
            .map((group) => (
              <div className="photo-review-batch" key={group.groupKey}>
                <div className="photo-review-head">
                  <span className="photo-review-title">{group.groupKey}</span>
                  <span className="photo-review-count">{group.count.toLocaleString()} new items</span>
                </div>
                <p className="photos-note">
                  {/* A chunked walk mints a marker per invocation, so one night's ingest is many
                      markers reviewed here as the single ingest it actually was. */}
                  {group.batchIds.length} batch marker{group.batchIds.length === 1 ? "" : "s"} ·{" "}
                  {String(group.firstSeenUtc).split("T")[0]}
                </p>
                <div className="photo-review-actions">
                  <button type="button" className="photos-button" disabled={busy} onClick={() => approve(group.groupKey)}>
                    Approve into the timeline
                  </button>
                </div>
              </div>
            ))}
        </section>
      )}

      {/* The Google mesh (§2.10). Member-visible like the rest of curation: deciding a Google-only
          photo is not worth keeping is a family judgement, not an operator action. It loads its own
          state, so a database that has never meshed an archive costs this tab one cheap count. */}
      <PhotoGoogle />
    </div>
  );
}
