using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// Review state for the two batch surfaces (docs/photos-plan.md §2.5 ingest quarantine and §2.9
    /// suggested-hide proposals), held in <see cref="PhotoCurationBatch"/> /
    /// <see cref="PhotoCurationBatchItem"/> rows.
    ///
    /// <para><b>Phase 3 moved this out of files.</b> Phase 2 kept it as JSON under
    /// <c>PhotosReportDir</c> and said so plainly: "the CLI writes proposals on the host that can read
    /// the collection and the site reads them to render the review surface, so <c>PhotosReportDir</c>
    /// must resolve to the SAME directory for both … A future <c>PhotoCurationBatch</c> table would
    /// remove that requirement." In production it never can — the site pods have no path to the CLI
    /// host — so every JSON-backed review surface was empty there while looking healthy. The state now
    /// lives in the one place both halves already share. <c>PhotosReportDir</c> keeps the artifacts that
    /// never cross that boundary: ambiguous-pairing reports (§2.5) and exports (§2.11).</para>
    ///
    /// <para><b>The public seams are unchanged in shape</b> — same four verbs per lane, same DTOs — so
    /// the controller and the passes read the same as they did; they are async now because a row read
    /// is. Nothing here writes to the NAS, and nothing here hides anything: a proposal stays a proposal
    /// until a human accepts it, which is the whole reason the pass writes rows instead of flags.</para>
    /// </summary>
    public sealed class PhotoCurationStore
    {
        /// <summary>Version of the shapes below, for the export format. Bumped only when a reader would
        /// misread an older payload; everything so far is additive.</summary>
        public const int SchemaVersion = 2;

        private readonly MovieDb db;

        public PhotoCurationStore(MovieDb db)
        {
            this.db = db;
        }

        /// <summary>
        /// Always true since Phase 3: the state lives in the database, which every caller already has.
        ///
        /// <para>Kept as a seam rather than deleted because the surfaces above it still report it, and
        /// because the property is the honest place for a future "this host cannot review" answer. The
        /// fail-open posture it used to express — a missing directory must never look like an empty
        /// album — is now structural: there is nothing left to be missing.</para>
        /// </summary>
        public bool Configured => true;

        // ── Ingest-batch quarantine (§2.5) ───────────────────────────────────────────────────────

        /// <summary>
        /// Which ingest batches have been approved into the timeline.
        ///
        /// <para><b>The baseline rule.</b> The first time this state is materialized, every batch that
        /// already exists is recorded as approved, alongside an
        /// <see cref="PhotoCurationBatchKind.IngestBaseline"/> marker that makes "we have materialized"
        /// answerable even when there were no batches to approve. Quarantine is therefore about what
        /// arrives NEXT: it can never black out a collection that was ingested before the feature
        /// existed, which is the failure that would make a family open /photos to an empty page and
        /// conclude the album ate their pictures.</para>
        /// </summary>
        public async Task<PhotoIngestBatchReview> LoadIngestReviewAsync(IEnumerable<string> existingBatchIds)
        {
            var baseline = await db.PhotoCurationBatches
                .FirstOrDefaultAsync(b => b.Kind == PhotoCurationBatchKind.IngestBaseline);

            if (baseline == null)
            {
                baseline = await MaterializeBaselineAsync(existingBatchIds);
            }

            var approved = await db.PhotoCurationBatches
                .Where(b => b.Kind == PhotoCurationBatchKind.IngestApproval
                            && b.Status == PhotoCurationBatchStatus.Accepted)
                .Select(b => b.BatchId)
                .ToListAsync();

            return new PhotoIngestBatchReview
            {
                BaselineUtc = baseline.CreatedUtc,
                Approved = approved.OrderBy(b => b, StringComparer.Ordinal).ToList(),
                LastApprovedUtc = await db.PhotoCurationBatches
                    .Where(b => b.Kind == PhotoCurationBatchKind.IngestApproval)
                    .MaxAsync(b => (DateTime?)b.DecidedUtc),
            };
        }

        private async Task<PhotoCurationBatch> MaterializeBaselineAsync(IEnumerable<string> existingBatchIds)
        {
            var now = DateTime.UtcNow;
            var marker = new PhotoCurationBatch
            {
                Kind = PhotoCurationBatchKind.IngestBaseline,
                BatchId = "",
                Status = PhotoCurationBatchStatus.Accepted,
                CreatedUtc = now,
                DecidedUtc = now,
                Complete = true,
            };
            db.PhotoCurationBatches.Add(marker);

            foreach (var id in existingBatchIds.Where(b => !string.IsNullOrEmpty(b))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                db.PhotoCurationBatches.Add(new PhotoCurationBatch
                {
                    Kind = PhotoCurationBatchKind.IngestApproval,
                    BatchId = Truncate(id, 128),
                    Status = PhotoCurationBatchStatus.Accepted,
                    CreatedUtc = now,
                    DecidedUtc = now,
                    Complete = true,
                });
            }

            try
            {
                await db.SaveChangesAsync();
                return marker;
            }
            catch (DbUpdateException)
            {
                // Two members opening /photos at the same second is a normal Tuesday, and the unique
                // (Kind, BatchId) index is what makes the race safe rather than a second baseline. The
                // loser simply reads the winner's marker.
                foreach (var entry in db.ChangeTracker.Entries<PhotoCurationBatch>().ToList())
                    entry.State = EntityState.Detached;
                return await db.PhotoCurationBatches
                    .FirstAsync(b => b.Kind == PhotoCurationBatchKind.IngestBaseline);
            }
        }

        /// <summary>Approves batch ids into the timeline. Returns the state as it now stands.</summary>
        public async Task<PhotoIngestBatchReview> ApproveIngestBatchesAsync(
            IEnumerable<string> existingBatchIds, IEnumerable<string> approve, int? userId)
        {
            await LoadIngestReviewAsync(existingBatchIds);

            var wanted = approve.Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => Truncate(a, 128))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = await db.PhotoCurationBatches
                .Where(b => b.Kind == PhotoCurationBatchKind.IngestApproval && wanted.Contains(b.BatchId))
                .ToListAsync();
            var have = new HashSet<string>(rows.Select(r => r.BatchId), StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow;
            foreach (var row in rows.Where(r => r.Status != PhotoCurationBatchStatus.Accepted))
            {
                row.Status = PhotoCurationBatchStatus.Accepted;
                row.DecidedUtc = now;
                row.DecidedByUserId = userId;
            }
            foreach (var id in wanted.Where(w => !have.Contains(w)))
            {
                db.PhotoCurationBatches.Add(new PhotoCurationBatch
                {
                    Kind = PhotoCurationBatchKind.IngestApproval,
                    BatchId = id,
                    Status = PhotoCurationBatchStatus.Accepted,
                    CreatedUtc = now,
                    DecidedUtc = now,
                    DecidedByUserId = userId,
                    Complete = true,
                });
            }
            await db.SaveChangesAsync();

            return await LoadIngestReviewAsync(existingBatchIds);
        }

        /// <summary>
        /// How a chunked ingest's many markers are shown as ONE thing to review. A walk driven to
        /// completion by a caller loop mints a marker PER INVOCATION (the Phase 1 fact), so a night's
        /// ingest can leave dozens of them; listing those raw would be a review surface nobody can use.
        /// The default marker is <c>photos-yyyyMMdd-HHmmss</c>, so the group is everything before the
        /// time: same prefix, same DAY. An id that does not carry that shape is its own group, which is
        /// also what a hand-passed <c>--batch-id</c> should do.
        /// </summary>
        public static string GroupKey(string batchId)
        {
            if (string.IsNullOrEmpty(batchId)) return "";
            var match = Regex.Match(batchId, @"^(?<head>.*-\d{8})-\d{6}$");
            return match.Success ? match.Groups["head"].Value : batchId;
        }

        // ── Suggested-hide proposals (§2.9) ──────────────────────────────────────────────────────

        /// <summary>
        /// Appends items to a proposal batch, creating it if this is the first chunk. The pass that
        /// calls this is chunked and resumable, so <paramref name="cursor"/> is stored with the batch —
        /// a killed run resumes from the row rather than re-examining the collection from item 1.
        /// </summary>
        /// <remarks>Never touches <c>Hidden</c>. A proposal is a proposal until a human accepts it
        /// (§2.9: "human-confirmed batch-wise") — that is the entire reason these rows exist rather than
        /// the pass simply writing the flag.</remarks>
        public async Task<PhotoHideProposal> AppendProposalAsync(
            string batchId, IEnumerable<PhotoHideProposalItem> items, string cursor, bool complete)
        {
            var id = Truncate(batchId, 128);
            var batch = await db.PhotoCurationBatches
                .FirstOrDefaultAsync(b => b.Kind == PhotoCurationBatchKind.HideProposal && b.BatchId == id);

            if (batch == null)
            {
                batch = new PhotoCurationBatch
                {
                    Kind = PhotoCurationBatchKind.HideProposal,
                    BatchId = id,
                    Status = PhotoCurationBatchStatus.Pending,
                    CreatedUtc = DateTime.UtcNow,
                };
                db.PhotoCurationBatches.Add(batch);
                await db.SaveChangesAsync();
            }

            var already = new HashSet<int>(await db.PhotoCurationBatchItems
                .Where(i => i.PhotoCurationBatchId == batch.Id)
                .Select(i => i.PhotoAssetId)
                .ToListAsync());

            foreach (var item in items)
            {
                if (!already.Add(item.AssetId)) continue;
                db.PhotoCurationBatchItems.Add(new PhotoCurationBatchItem
                {
                    PhotoCurationBatchId = batch.Id,
                    PhotoAssetId = item.AssetId,
                    Path = Truncate(item.Path, 850),
                    Sha256 = item.Sha256,
                    Rule = Truncate(item.Rule, 64),
                });
            }

            batch.Cursor = Truncate(cursor, 128);
            batch.Complete = complete;
            await db.SaveChangesAsync();

            return await LoadProposalAsync(id) ?? ToProposal(batch, new List<PhotoCurationBatchItem>());
        }

        public async Task<PhotoHideProposal?> LoadProposalAsync(string batchId)
        {
            var id = Truncate(batchId, 128);
            var batch = await db.PhotoCurationBatches
                .FirstOrDefaultAsync(b => b.Kind == PhotoCurationBatchKind.HideProposal && b.BatchId == id);
            if (batch == null) return null;

            var items = await db.PhotoCurationBatchItems
                .Where(i => i.PhotoCurationBatchId == batch.Id)
                .OrderBy(i => i.Id)
                .ToListAsync();
            return ToProposal(batch, items);
        }

        /// <summary>
        /// Every proposal, newest first — WITHOUT their items.
        ///
        /// <para>The list surface shows a rule breakdown and a count, never ten thousand file names, so
        /// loading the items to draw it would read a screenshots pile off disk to display a number.
        /// <see cref="RuleCountsAsync"/> answers the breakdown with a GROUP BY instead.</para>
        /// </summary>
        public async Task<List<PhotoHideProposal>> ListProposalsAsync()
        {
            var batches = await db.PhotoCurationBatches
                .Where(b => b.Kind == PhotoCurationBatchKind.HideProposal)
                .OrderByDescending(b => b.CreatedUtc).ThenByDescending(b => b.Id)
                .ToListAsync();
            return batches.Select(b => ToProposal(b, new List<PhotoCurationBatchItem>())).ToList();
        }

        /// <summary>Per-rule counts and a handful of example paths for one proposal, as aggregates.</summary>
        public async Task<(Dictionary<string, int> Rules, int Count, List<string> Samples)> RuleCountsAsync(string batchId)
        {
            var id = Truncate(batchId, 128);
            var batchDbId = await db.PhotoCurationBatches
                .Where(b => b.Kind == PhotoCurationBatchKind.HideProposal && b.BatchId == id)
                .Select(b => (int?)b.Id)
                .FirstOrDefaultAsync();
            if (batchDbId == null) return (new Dictionary<string, int>(StringComparer.Ordinal), 0, new List<string>());

            var counts = await db.PhotoCurationBatchItems
                .Where(i => i.PhotoCurationBatchId == batchDbId.Value)
                .GroupBy(i => i.Rule)
                .Select(g => new { rule = g.Key, count = g.Count() })
                .ToListAsync();
            var samples = await db.PhotoCurationBatchItems
                .Where(i => i.PhotoCurationBatchId == batchDbId.Value)
                .OrderBy(i => i.Id)
                .Select(i => i.Path)
                .Take(8)
                .ToListAsync();

            return (counts.ToDictionary(c => c.rule, c => c.count, StringComparer.Ordinal),
                counts.Sum(c => c.count), samples);
        }

        /// <summary>A page of a proposal's items, in the order the pass produced them.</summary>
        public async Task<List<PhotoHideProposalItem>> ProposalItemsAsync(string batchId, int skip, int take)
        {
            var id = Truncate(batchId, 128);
            var batchDbId = await db.PhotoCurationBatches
                .Where(b => b.Kind == PhotoCurationBatchKind.HideProposal && b.BatchId == id)
                .Select(b => (int?)b.Id)
                .FirstOrDefaultAsync();
            if (batchDbId == null) return new List<PhotoHideProposalItem>();

            return await db.PhotoCurationBatchItems
                .Where(i => i.PhotoCurationBatchId == batchDbId.Value)
                .OrderBy(i => i.Id)
                .Skip(skip).Take(take)
                .Select(i => new PhotoHideProposalItem
                {
                    AssetId = i.PhotoAssetId,
                    Path = i.Path,
                    Sha256 = i.Sha256,
                    Rule = i.Rule,
                })
                .ToListAsync();
        }

        /// <summary>
        /// The proposal's items in bounded pages — what an accept sweeps over, without ever loading a
        /// whole screenshots pile into memory to do it. The item id is returned alongside the asset id
        /// because it is the sweep's cursor, and it pages by the same column it orders by.
        /// </summary>
        public async Task<List<PhotoProposalRef>> ProposalAssetPageAsync(string batchId, int afterItemId, int take)
        {
            var id = Truncate(batchId, 128);
            var batchDbId = await db.PhotoCurationBatches
                .Where(b => b.Kind == PhotoCurationBatchKind.HideProposal && b.BatchId == id)
                .Select(b => (int?)b.Id)
                .FirstOrDefaultAsync();
            if (batchDbId == null) return new List<PhotoProposalRef>();

            return await db.PhotoCurationBatchItems
                .Where(i => i.PhotoCurationBatchId == batchDbId.Value && i.Id > afterItemId)
                .OrderBy(i => i.Id)
                .Take(take)
                .Select(i => new PhotoProposalRef { ItemId = i.Id, AssetId = i.PhotoAssetId })
                .ToListAsync();
        }

        /// <summary>How many items a proposal holds, as an aggregate.</summary>
        public async Task<int> ProposalItemCountAsync(string batchId)
        {
            var id = Truncate(batchId, 128);
            return await db.PhotoCurationBatchItems
                .CountAsync(i => i.PhotoCurationBatch.Kind == PhotoCurationBatchKind.HideProposal
                                 && i.PhotoCurationBatch.BatchId == id);
        }

        /// <summary>Records a human's verdict. The FLAG writes happen in the caller's transaction; this
        /// only stamps who decided and what it ended up doing, so a re-post of the same decision is a
        /// no-op rather than a second sweep.</summary>
        public async Task<PhotoHideProposal?> DecideAsync(string batchId, string status, int? userId, int appliedCount)
        {
            var id = Truncate(batchId, 128);
            var batch = await db.PhotoCurationBatches
                .FirstOrDefaultAsync(b => b.Kind == PhotoCurationBatchKind.HideProposal && b.BatchId == id);
            if (batch == null) return null;

            batch.Status = status == PhotoHideProposal.StatusAccepted
                ? PhotoCurationBatchStatus.Accepted
                : PhotoCurationBatchStatus.Rejected;
            batch.DecidedUtc = DateTime.UtcNow;
            batch.DecidedByUserId = userId;
            batch.AppliedCount = appliedCount;
            await db.SaveChangesAsync();

            return ToProposal(batch, new List<PhotoCurationBatchItem>());
        }

        // ── Row → DTO ────────────────────────────────────────────────────────────────────────────

        private static PhotoHideProposal ToProposal(PhotoCurationBatch batch, List<PhotoCurationBatchItem> items) =>
            new PhotoHideProposal
            {
                Version = SchemaVersion,
                BatchId = batch.BatchId,
                CreatedUtc = batch.CreatedUtc,
                Status = StatusText(batch.Status),
                DecidedUtc = batch.DecidedUtc,
                DecidedByUserId = batch.DecidedByUserId,
                AppliedCount = batch.AppliedCount,
                Cursor = batch.Cursor,
                Complete = batch.Complete,
                Items = items.Select(i => new PhotoHideProposalItem
                {
                    AssetId = i.PhotoAssetId,
                    Path = i.Path,
                    Sha256 = i.Sha256,
                    Rule = i.Rule,
                }).ToList(),
            };

        public static string StatusText(PhotoCurationBatchStatus status) => status switch
        {
            PhotoCurationBatchStatus.Accepted => PhotoHideProposal.StatusAccepted,
            PhotoCurationBatchStatus.Rejected => PhotoHideProposal.StatusRejected,
            _ => PhotoHideProposal.StatusPending,
        };

        public static PhotoCurationBatchStatus ParseStatus(string? status) => status switch
        {
            PhotoHideProposal.StatusAccepted => PhotoCurationBatchStatus.Accepted,
            PhotoHideProposal.StatusRejected => PhotoCurationBatchStatus.Rejected,
            _ => PhotoCurationBatchStatus.Pending,
        };

        private static string Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value!.Length <= max ? value : value.Substring(0, max);
        }
    }

    /// <summary>Which ingest batches may appear in the timeline (§2.5). Absence of an id is
    /// "unreviewed", which is what quarantine means — there is no rejected state, because rejecting an
    /// ingest is not a thing that can be done to files that exist on disk.</summary>
    public sealed class PhotoIngestBatchReview
    {
        public int Version { get; set; } = PhotoCurationStore.SchemaVersion;

        /// <summary>When the state was first materialized; everything present at that moment is
        /// approved, so quarantine only ever describes what arrived afterwards.</summary>
        public DateTime BaselineUtc { get; set; }

        public DateTime? LastApprovedUtc { get; set; }

        public List<string> Approved { get; set; } = new List<string>();

        public bool IsApproved(string? batchId) =>
            string.IsNullOrEmpty(batchId)
            || Approved.Any(a => string.Equals(a, batchId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One <c>photos-suggest-hide</c> run's proposal (§2.9). Items are named by id AND by the
    /// content key, so a proposal stays meaningful after a path re-point (§2.5) — and so accepting one
    /// can re-check that the asset it is about is still the asset it was about.</summary>
    public sealed class PhotoHideProposal
    {
        public const string StatusPending = "pending";
        public const string StatusAccepted = "accepted";
        public const string StatusRejected = "rejected";

        public int Version { get; set; } = PhotoCurationStore.SchemaVersion;

        public string BatchId { get; set; } = "";

        public DateTime CreatedUtc { get; set; }

        public string Status { get; set; } = StatusPending;

        public DateTime? DecidedUtc { get; set; }

        public int? DecidedByUserId { get; set; }

        /// <summary>How many rows the accept actually flipped. Lower than the item count is normal and
        /// not a fault: an asset already hidden, or gone since the proposal was written, is skipped.</summary>
        public int AppliedCount { get; set; }

        /// <summary>Resume marker for the chunked pass that fills this batch.</summary>
        public string? Cursor { get; set; }

        /// <summary>Whether the pass drained the collection. A half-written proposal is reviewable —
        /// it just does not claim to be everything.</summary>
        public bool Complete { get; set; }

        /// <summary>Populated only by <see cref="PhotoCurationStore.LoadProposalAsync"/>; the list
        /// surface deliberately leaves it empty and reads counts as aggregates instead.</summary>
        public List<PhotoHideProposalItem> Items { get; set; } = new List<PhotoHideProposalItem>();

        public Dictionary<string, int> RuleCounts()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in Items)
            {
                var rule = item.Rule ?? "";
                counts[rule] = (counts.TryGetValue(rule, out var v) ? v : 0) + 1;
            }
            return counts;
        }
    }

    /// <summary>One row of a proposal sweep: the item's own id (the cursor) and the asset it names.</summary>
    public sealed class PhotoProposalRef
    {
        public int ItemId { get; set; }

        public int AssetId { get; set; }
    }

    public sealed class PhotoHideProposalItem
    {
        public int AssetId { get; set; }

        /// <summary>Root-relative path as it stood when proposed — context for the reviewer, and the
        /// fallback identity when the row's hash has not been computed yet.</summary>
        public string Path { get; set; } = "";

        public string? Sha256 { get; set; }

        /// <summary>Which heuristic proposed it (<see cref="PhotoHideSuggestions"/>). Carried per item
        /// so a reviewer can see WHY, and so a rule that turns out to be wrong is visible as a cluster
        /// rather than as scattered mistakes.</summary>
        public string Rule { get; set; } = "";

        public static string FormatCursor(int assetId) =>
            assetId.ToString(CultureInfo.InvariantCulture);
    }
}
