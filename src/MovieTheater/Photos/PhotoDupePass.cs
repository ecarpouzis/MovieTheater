using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>Which grouping lane a <c>photos-dupes</c> run is draining (docs/photos-plan.md §2.6).
    /// Three lanes because the three kinds of sameness differ in WHO settles them, not merely in how
    /// they are found.</summary>
    public enum PhotoDupePassKind
    {
        /// <summary>Byte-identical (equal SHA-256). Auto-grouped, auto-mastered, still listed for review.</summary>
        Exact,

        /// <summary>Perceptually similar. Proposed only — never auto-resolved, and never re-proposed
        /// after a human has said "not the same photo".</summary>
        Near,

        /// <summary>One capture, several files by design. Auto-paired and machine-settled; never offered
        /// for "pick the better copy".</summary>
        Variant,
    }

    public sealed class PhotoDupeOptions
    {
        /// <summary>Units per batch: SHA keys (or groups, in the revalidate phase) for Exact, rows for
        /// Near, rows for Variant.</summary>
        public int BatchSize = 500;

        /// <summary>§2.6's near threshold, in differing pHash bits out of 64. Eight is ~12% of the word:
        /// tight enough that two different photographs of the same scene stay apart, loose enough to
        /// survive a re-encode, a resize and a second pass over the same print.</summary>
        public int NearDistance = 8;

        /// <summary>How many candidate pairs one Near batch may emit before it stops and hands back a
        /// cursor. A single row in a folder of a thousand near-identical frames can otherwise produce a
        /// batch whose size has nothing to do with <see cref="BatchSize"/>.</summary>
        public int MaxPairsPerBatch = 500;

        public PhotoVariantPairs.Options Variant = new PhotoVariantPairs.Options();
    }

    /// <summary>
    /// The <c>photos-dupes</c> engine (docs/photos-plan.md §2.6): three resumable, DB-only passes that
    /// assert sameness as rows and nothing else.
    ///
    /// <para><b>Nothing here is a file operation</b> (§6). No file is opened — every lane reads columns
    /// the ingest already persisted — and "merging" the merge-needed folders means a master row wins the
    /// timeline while the disk stays exactly as it is.</para>
    ///
    /// <para><b>Bulk-job contract</b>, same as every other pass on this repo: a bounded amount of work
    /// per call, <c>{processed, remaining, nextCursor}</c> after each batch, a cursor whose ordering IS
    /// the page query's ordering (audited per lane below), <c>remaining</c> re-counted from the database
    /// rather than decremented, and a deterministic no-progress stop.</para>
    ///
    /// <para><b>Idempotent by construction.</b> Every lane's write path is an upsert against the group a
    /// member already belongs to, and every ordering carries a final tie-break on id, so re-running a
    /// drained pass creates nothing and changes nothing. That is the property the acceptance check
    /// measures, and it is the reason the master heuristic is not allowed a single unstable comparison
    /// (<see cref="PhotoDupeMasters.PickMaster"/>).</para>
    /// </summary>
    public sealed class PhotoDupePass
    {
        private readonly Func<MovieDb> dbFactory;
        private readonly PhotoDupeOptions options;
        private readonly Action<string> log;

        /// <summary>Built once per RUN and reused across that run's batches (§2.6: "per run"). See
        /// <see cref="PhotoHashIndex"/> for what the rebuild costs and why a driver loop should give the
        /// near pass several batches per invocation rather than one.</summary>
        private PhotoHashIndex? nearIndex;

        /// <summary>Pairs a human has already refused, loaded with the index. A rejection is about the
        /// PAIR, so it is stored as one — and it is checked before any proposal, because re-proposing a
        /// pair someone has already dismissed is how a review queue becomes something nobody opens.</summary>
        private HashSet<long>? rejectedPairs;

        public PhotoDupePass(Func<MovieDb> dbFactory, PhotoDupeOptions options, Action<string> log)
        {
            this.dbFactory = dbFactory;
            this.options = options;
            this.log = log;
        }

        // ── Driver ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Runs up to <paramref name="maxBatches"/> bounded batches (0 drains), printing the
        /// per-chunk line the standing rule requires and stopping deterministically.</summary>
        public async Task<PhotoIngestBatchResult> RunAsync(PhotoDupePassKind kind, string? cursor, int maxBatches)
        {
            var total = new PhotoIngestBatchResult { NextCursor = cursor ?? "" };
            var batches = 0;
            while (maxBatches <= 0 || batches < maxBatches)
            {
                var result = await BatchAsync(kind, batches == 0 ? cursor : total.NextCursor);
                batches++;
                total.Processed += result.Processed;
                total.Remaining = result.Remaining;
                total.NextCursor = result.NextCursor;
                foreach (var kv in result.Counts) total.Add(kv.Key, kv.Value);

                var counts = result.CountsText();
                log($"{{ processed: {result.Processed}, remaining: {result.Remaining}, nextCursor: \"{result.NextCursor}\" }}"
                    + (counts.Length > 0 ? $"  [{counts}]" : ""));

                if (result.Remaining <= 0) break;
                if (result.Processed <= 0)
                {
                    log("No progress in a batch while work remained — stopping.");
                    break;
                }
            }
            return total;
        }

        public Task<PhotoIngestBatchResult> BatchAsync(PhotoDupePassKind kind, string? cursor) => kind switch
        {
            PhotoDupePassKind.Exact => ExactBatchAsync(cursor),
            PhotoDupePassKind.Near => NearBatchAsync(cursor),
            PhotoDupePassKind.Variant => VariantBatchAsync(cursor),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // ── Exact (§2.6) ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One bounded Exact batch, in TWO phases behind one cursor.
        ///
        /// <para>Phase <c>v</c> re-validates existing Exact groups: a re-ingested file whose bytes
        /// changed carries a new <see cref="PhotoAsset.Sha256"/>, and a group is an assertion about
        /// content, so it must be re-tested rather than blindly kept. Phase <c>s</c> then walks the
        /// SHA-256 values that have more than one live row and upserts a group for each. The phases are
        /// ordered so the second cannot re-add what the first has just removed.</para>
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> Phase <c>v</c> pages <c>WHERE Id &gt; cursor ORDER BY
        /// Id</c> over groups and the cursor is the last group id; phase <c>s</c> pages
        /// <c>WHERE Sha256 &gt; cursor ORDER BY Sha256</c> and the cursor is the last SHA. One column,
        /// one direction, in the page query and in the cursor, in both phases. <c>remaining</c> is
        /// counted from the database after the writes rather than decremented.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> ExactBatchAsync(string? cursor)
        {
            var (phase, mark) = ParsePhaseCursor(cursor);
            using var db = dbFactory();

            if (phase == "v")
            {
                var afterGroup = int.TryParse(mark, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : 0;
                var result = await RevalidateExactAsync(db, afterGroup);
                if (result.Remaining > 0) return result;

                // The revalidate queue is drained, so this batch carries straight on into the first SHA
                // batch. Handing back a cursor with nothing processed would look exactly like a stalled
                // batch to the driver's no-progress guard — and the guard is right to stop for that, so
                // the phase change must not produce one. The cost is a batch that may do up to two
                // batch-sizes of work, once per run, at the seam.
                var sha = await ExactShaBatchAsync(db, null);
                sha.Processed += result.Processed;
                foreach (var kv in result.Counts) sha.Add(kv.Key, kv.Value);
                return sha;
            }

            return await ExactShaBatchAsync(db, string.IsNullOrEmpty(mark) ? null : mark);
        }

        private async Task<PhotoIngestBatchResult> RevalidateExactAsync(MovieDb db, int afterGroupId)
        {
            var result = new PhotoIngestBatchResult
            {
                NextCursor = "v:" + afterGroupId.ToString(CultureInfo.InvariantCulture),
            };

            var groups = await db.PhotoDupeGroups
                .Where(g => g.Kind == PhotoDupeGroupKind.Exact
                            && g.Status != PhotoDupeGroupStatus.Rejected
                            && g.Id > afterGroupId)
                .OrderBy(g => g.Id)
                .Take(Math.Max(1, options.BatchSize))
                .Include(g => g.Members).ThenInclude(m => m.PhotoAsset)
                .ToListAsync();

            foreach (var group in groups)
            {
                var members = group.Members.ToList();
                // The content the group claims: the master's hash, or the most common one when the
                // master is gone. Ties break on the lowest id so two runs never disagree.
                var master = members.FirstOrDefault(m => m.IsMaster);
                var claimed = master?.PhotoAsset.Sha256
                              ?? members.Where(m => m.PhotoAsset.Sha256 != null)
                                  .GroupBy(m => m.PhotoAsset.Sha256!)
                                  .OrderByDescending(x => x.Count())
                                  .ThenBy(x => x.Min(m => m.PhotoAssetId))
                                  .Select(x => x.Key)
                                  .FirstOrDefault();

                var stale = claimed == null
                    ? members
                    : members.Where(m => !string.Equals(m.PhotoAsset.Sha256, claimed, StringComparison.OrdinalIgnoreCase)).ToList();
                if (stale.Count > 0)
                {
                    db.PhotoDupeMembers.RemoveRange(stale);
                    foreach (var s in stale) group.Members.Remove(s);
                    result.Add("revalidated-out", stale.Count);
                }

                if (group.Members.Count < 2)
                {
                    db.PhotoDupeMembers.RemoveRange(group.Members);
                    db.PhotoDupeGroups.Remove(group);
                    result.Add("groups-dissolved");
                }
                else if (stale.Count > 0)
                {
                    await EnsureMasterAsync(db, group, PhotoDupeMasters.PickMaster, result);
                }
                result.Processed++;
            }

            await db.SaveChangesAsync();

            var last = groups.Count > 0 ? groups[groups.Count - 1].Id : afterGroupId;
            result.NextCursor = "v:" + last.ToString(CultureInfo.InvariantCulture);
            result.Remaining = await db.PhotoDupeGroups
                .CountAsync(g => g.Kind == PhotoDupeGroupKind.Exact
                                 && g.Status != PhotoDupeGroupStatus.Rejected
                                 && g.Id > last);
            return result;
        }

        /// <summary>The Exact queue: SHA-256 values carried by more than one live row. Hidden assets are
        /// INCLUDED — byte equality is objective, and a hidden screenshot that exists twice is still one
        /// file twice.</summary>
        private IQueryable<string> ExactShaQueue(MovieDb db, string? afterSha)
        {
            var rows = db.PhotoAssets.Where(a => a.Sha256 != null && a.MissingSinceUtc == null);
            if (afterSha != null) rows = rows.Where(a => string.Compare(a.Sha256!, afterSha) > 0);
            return rows.GroupBy(a => a.Sha256!).Where(g => g.Count() > 1).Select(g => g.Key);
        }

        private async Task<PhotoIngestBatchResult> ExactShaBatchAsync(MovieDb db, string? afterSha)
        {
            var result = new PhotoIngestBatchResult { NextCursor = "s:" + (afterSha ?? "") };

            var keys = await ExactShaQueue(db, afterSha)
                .OrderBy(sha => sha)
                .Take(Math.Max(1, options.BatchSize))
                .ToListAsync();
            if (keys.Count == 0)
            {
                result.Remaining = 0;
                return result;
            }

            var assets = await db.PhotoAssets
                .Where(a => a.Sha256 != null && a.MissingSinceUtc == null && keys.Contains(a.Sha256!))
                .ToListAsync();
            var bySha = assets.GroupBy(a => a.Sha256!, StringComparer.OrdinalIgnoreCase);

            foreach (var group in bySha)
            {
                var members = group.OrderBy(a => a.Id).ToList();
                if (members.Count < 2) continue;
                await SyncGroupAsync(db, PhotoDupeGroupKind.Exact, members,
                    PhotoDupeGroupStatus.Pending, PhotoDupeMasters.PickMaster, null, result);
            }
            result.Processed = keys.Count;

            await db.SaveChangesAsync();

            var last = keys[keys.Count - 1];
            result.NextCursor = "s:" + last;
            result.Remaining = await ExactShaQueue(db, last).CountAsync();
            return result;
        }

        // ── Near (§2.6) ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The Near queue: live, non-hidden photos that have a perceptual hash.
        ///
        /// <para>Hidden assets are deliberately OUT. A screenshots pile is thousands of nearly identical
        /// frames, and proposing them for "which of these is the better copy" would bury the scanned
        /// prints this lane exists for under noise a human already curated away (§2.9). Exact grouping
        /// still covers them, because byte equality needs no judgement.</para>
        ///
        /// <para>So are the copies a SETTLED group has already collapsed. A phone-backup folder with a
        /// thousand byte-identical files would otherwise be proposed a second time, as a thousand near
        /// groups, about photographs the exact lane has already spoken for with a stronger claim. The
        /// lane therefore works on what browse actually shows — which is also why <c>--pass all</c> runs
        /// exact and variant BEFORE near.</para>
        /// </summary>
        private static IQueryable<PhotoAsset> NearQueue(MovieDb db)
        {
            var collapsed = PhotoDupeMasters.CollapsedAssetIds(db);
            return db.PhotoAssets
                .Where(a => a.PHash != null && a.MissingSinceUtc == null && !a.Hidden)
                .Where(a => !collapsed.Contains(a.Id));
        }

        /// <summary>
        /// One bounded Near batch.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> <c>WHERE Id &gt; cursor ORDER BY Id</c>, cursor = the
        /// last id examined — the same column and direction in both, and the pair cap only ever moves
        /// the cursor BACK to the last row fully processed, never past one.</para>
        ///
        /// <para><b>Never auto-resolves</b> (§2.6). Every group this lane touches stays
        /// <see cref="PhotoDupeGroupStatus.Pending"/> with a master merely PROPOSED, and a pair inside a
        /// <see cref="PhotoDupeGroupStatus.Rejected"/> group is never proposed again.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> NearBatchAsync(string? cursor)
        {
            var afterId = int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var result = new PhotoIngestBatchResult { NextCursor = afterId.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();

            await EnsureNearIndexAsync(db);

            var rows = await NearQueue(db)
                .Where(a => a.Id > afterId)
                .OrderBy(a => a.Id)
                .Take(Math.Max(1, options.BatchSize))
                .ToListAsync();
            if (rows.Count == 0)
            {
                result.Remaining = 0;
                return result;
            }

            var pairs = 0;
            var lastId = afterId;
            foreach (var row in rows)
            {
                var neighbours = nearIndex!.Query(row.PHash!.Value)
                    .Where(n => n.AssetId != row.Id)
                    .ToList();
                pairs += neighbours.Count;
                if (neighbours.Count > 0)
                    await LinkNearAsync(db, row, neighbours, result);

                lastId = row.Id;
                result.Processed++;
                // The cap is checked AFTER a row is finished, so the cursor always names a row whose
                // neighbours were all considered — a batch cut in half would otherwise lose pairs.
                if (pairs >= options.MaxPairsPerBatch) break;
            }
            result.Add("pairs-considered", pairs);

            await db.SaveChangesAsync();

            result.NextCursor = lastId.ToString(CultureInfo.InvariantCulture);
            result.Remaining = await NearQueue(db).CountAsync(a => a.Id > lastId);
            return result;
        }

        /// <summary>
        /// Attaches an EXTERNALLY proposed candidate set to the Near lane — the door
        /// <c>photos-sync-immich</c> comes through with Immich's CLIP duplicate candidates (§2.4/§2.6).
        ///
        /// <para>It is deliberately a door into <see cref="LinkNearAsync"/> rather than a second
        /// implementation: the rejected-pair check, the one-active-group-per-kind invariant, the Pending
        /// status and the master heuristic are then literally the same code, so a candidate from the
        /// sidecar can never be settled by a rule the near pass does not also obey. In particular, "these
        /// are not the same photo" binds a PAIR and is kind-agnostic (§2.6), so a human's refusal blocks
        /// this lane exactly as it blocks the pHash one — which is what makes re-running a sync after a
        /// rejection re-propose nothing.</para>
        ///
        /// <para>The caller owns the <see cref="MovieDb"/> and the SaveChanges around it, because it is
        /// batching its own work; the rejected-pair set is loaded here on first use.</para>
        /// </summary>
        public async Task LinkExternalNearAsync(MovieDb db, PhotoAsset row, List<PhotoHashNeighbour> neighbours,
            PhotoIngestBatchResult result)
        {
            if (neighbours.Count == 0) return;
            await EnsureRejectedPairsAsync(db);
            await LinkNearAsync(db, row, neighbours, result);
        }

        private async Task EnsureNearIndexAsync(MovieDb db)
        {
            if (nearIndex != null) return;

            var index = new PhotoHashIndex(options.NearDistance);
            var hashed = await NearQueue(db)
                .Select(a => new { a.Id, a.PHash })
                .ToListAsync();
            foreach (var row in hashed) index.Add(row.Id, row.PHash!.Value);
            nearIndex = index;

            await EnsureRejectedPairsAsync(db);
            log($"  near index: {index.Count} hashed assets, threshold {options.NearDistance} bits, "
                + $"{rejectedPairs!.Count} rejected pair(s) remembered");
        }

        /// <summary>
        /// Every pair inside a rejected group, both ways round, as one flat set — loaded once per run.
        ///
        /// <para>KIND-AGNOSTIC on purpose. "These are not the same photo" is a statement about the
        /// photographs, not about the lane that happened to propose them, so a rejection blocks the
        /// exact lane from re-minting the same grouping just as it blocks the near lane. Without that,
        /// rejecting a group would un-collapse its copies and the very next run would propose them
        /// again — the loop a review queue must never have.</para>
        /// </summary>
        private async Task EnsureRejectedPairsAsync(MovieDb db)
        {
            if (rejectedPairs != null) return;

            var tombstones = await db.PhotoDupeMembers
                .Where(m => m.PhotoDupeGroup.Status == PhotoDupeGroupStatus.Rejected)
                .Select(m => new { m.PhotoDupeGroupId, m.PhotoAssetId })
                .ToListAsync();
            rejectedPairs = new HashSet<long>();
            foreach (var group in tombstones.GroupBy(m => m.PhotoDupeGroupId))
            {
                var ids = group.Select(m => m.PhotoAssetId).ToList();
                for (var i = 0; i < ids.Count; i++)
                    for (var j = i + 1; j < ids.Count; j++)
                        rejectedPairs.Add(PairKey(ids[i], ids[j]));
            }
        }

        /// <summary>Drops candidates a human has already refused against one of the others, keeping the
        /// lowest ids (the deterministic survivors). Returns the set that may legitimately be grouped.</summary>
        private List<PhotoAsset> WithoutRejectedPairs(List<PhotoAsset> candidates, PhotoIngestBatchResult result)
        {
            if (rejectedPairs!.Count == 0) return candidates;

            var accepted = new List<PhotoAsset>();
            foreach (var candidate in candidates.OrderBy(a => a.Id))
            {
                if (accepted.Any(other => rejectedPairs.Contains(PairKey(other.Id, candidate.Id))))
                {
                    result.Add("rejected-pair-skipped");
                    continue;
                }
                accepted.Add(candidate);
            }
            return accepted;
        }

        /// <summary>
        /// Attaches one asset and its neighbours to a Near group.
        ///
        /// <para><b>An asset belongs to at most one ACTIVE group per kind.</b> A
        /// <see cref="PhotoDupeGroupStatus.Rejected"/> group is a tombstone about a pair, not a
        /// membership, so being in one never bars an asset from a group with a different photo — which
        /// is the only reading that lets "these two are not the same" mean what it says.</para>
        ///
        /// <para>Two assets that already sit in DIFFERENT active groups are left alone and counted:
        /// silently merging two review queues a human is part-way through is a worse answer than a
        /// reported skip, and the human can merge them from the review UI by resolving one.</para>
        /// </summary>
        private async Task LinkNearAsync(MovieDb db, PhotoAsset row, List<PhotoHashNeighbour> neighbours,
            PhotoIngestBatchResult result)
        {
            var ids = neighbours.Select(n => n.AssetId).Append(row.Id).Distinct().ToList();
            var memberships = await db.PhotoDupeMembers
                .Where(m => ids.Contains(m.PhotoAssetId)
                            && m.PhotoDupeGroup.Kind == PhotoDupeGroupKind.Near
                            && m.PhotoDupeGroup.Status != PhotoDupeGroupStatus.Rejected)
                .Include(m => m.PhotoDupeGroup)
                .ToListAsync();
            // Lowest group id wins if an asset somehow sits in two active Near groups (a state a merge
            // should have removed): deterministic, and never a duplicate-key throw mid-batch.
            var groupOf = new Dictionary<int, int>();
            foreach (var m in memberships.OrderBy(m => m.PhotoDupeGroupId))
                if (!groupOf.ContainsKey(m.PhotoAssetId)) groupOf[m.PhotoAssetId] = m.PhotoDupeGroupId;

            var target = groupOf.TryGetValue(row.Id, out var own)
                ? own
                : neighbours.Select(n => groupOf.TryGetValue(n.AssetId, out var g) ? g : 0)
                    .Where(g => g > 0)
                    .DefaultIfEmpty(0)
                    .Min();

            PhotoDupeGroup? group = null;
            List<PhotoAsset> current;
            if (target > 0)
            {
                group = await db.PhotoDupeGroups
                    .Include(g => g.Members).ThenInclude(m => m.PhotoAsset)
                    .FirstAsync(g => g.Id == target);
                current = group.Members.Select(m => m.PhotoAsset).ToList();
            }
            else
            {
                current = new List<PhotoAsset>();
            }

            var similarity = neighbours.ToDictionary(n => n.AssetId, n => (double?)n.Similarity);
            var additions = new List<PhotoAsset>();
            if (target == 0 || !groupOf.ContainsKey(row.Id)) additions.Add(row);

            foreach (var neighbour in neighbours)
            {
                if (groupOf.TryGetValue(neighbour.AssetId, out var theirs))
                {
                    if (theirs != target) result.Add("cross-group-skipped");
                    continue;
                }
                var asset = await db.PhotoAssets.FirstOrDefaultAsync(a => a.Id == neighbour.AssetId);
                if (asset == null) continue;
                additions.Add(asset);
            }

            // A rejection binds the PAIR, so a candidate is refused when it collides with ANY member of
            // the group it would be joining — including the ones added a moment ago in this same batch.
            var settled = current.Concat(additions.Where(a => a.Id == row.Id)).Select(a => a.Id).ToList();
            var accepted = new List<PhotoAsset>();
            foreach (var candidate in additions)
            {
                if (settled.Any(other => other != candidate.Id && rejectedPairs!.Contains(PairKey(other, candidate.Id))))
                {
                    result.Add("rejected-pair-skipped");
                    continue;
                }
                accepted.Add(candidate);
                if (!settled.Contains(candidate.Id)) settled.Add(candidate.Id);
            }
            if (accepted.Count == 0) return;

            if (group == null)
            {
                if (accepted.Count < 2) return;
                group = new PhotoDupeGroup
                {
                    Kind = PhotoDupeGroupKind.Near,
                    Status = PhotoDupeGroupStatus.Pending,
                    CreatedUtc = DateTime.UtcNow,
                };
                db.PhotoDupeGroups.Add(group);
                foreach (var asset in accepted.OrderBy(a => a.Id))
                    group.Members.Add(new PhotoDupeMember
                    {
                        PhotoDupeGroup = group,
                        PhotoAsset = asset,
                        PhotoAssetId = asset.Id,
                        Similarity = similarity.TryGetValue(asset.Id, out var s) ? s : null,
                    });
                await EnsureMasterAsync(db, group, PhotoDupeMasters.PickMaster, result);
                result.Add("groups-created");
                // Flushed per row, deliberately. The next row in this same batch asks the DATABASE which
                // group its neighbours are in, and an unsaved group is invisible to that question — the
                // neighbours would then be handed a SECOND group, which is exactly the "one active group
                // per kind" invariant this lane exists to keep.
                await db.SaveChangesAsync();
                return;
            }

            var added = 0;
            foreach (var asset in accepted.OrderBy(a => a.Id))
            {
                if (group.Members.Any(m => m.PhotoAssetId == asset.Id)) continue;
                group.Members.Add(new PhotoDupeMember
                {
                    PhotoDupeGroup = group,
                    PhotoAsset = asset,
                    PhotoAssetId = asset.Id,
                    Similarity = similarity.TryGetValue(asset.Id, out var s) ? s : null,
                });
                added++;
            }
            if (added > 0)
            {
                result.Add("members-added", added);
                await EnsureMasterAsync(db, group, PhotoDupeMasters.PickMaster, result);
                await db.SaveChangesAsync();
            }
        }

        private static long PairKey(int a, int b)
        {
            var low = Math.Min(a, b);
            var high = Math.Max(a, b);
            return ((long)low << 32) | (uint)high;
        }

        // ── Variant (§2.6) ───────────────────────────────────────────────────────────────────────

        private static IQueryable<PhotoAsset> VariantQueue(MovieDb db) =>
            db.PhotoAssets.Where(a => a.MissingSinceUtc == null);

        /// <summary>
        /// One bounded Variant batch, paged by PATH so a directory's files arrive together — the pairing
        /// key is the directory plus the stem, and a cluster split across a batch boundary would pair
        /// nothing.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> <c>WHERE Path &gt; cursor ORDER BY Path</c>, cursor =
        /// the last path taken. The batch is then EXTENDED to the end of its final directory (bounded by
        /// that directory's size), which only ever moves the cursor forward, so ordering and progress
        /// both hold. Ordinal-ish string ordering is the database's, and the same comparison decides the
        /// page and the cursor.</para>
        ///
        /// <para>Every live row is visited each run, which is what makes this lane self-revalidating: a
        /// cluster that no longer classifies (its video half was removed, say) DISSOLVES its group here
        /// rather than being kept on the strength of a decision the data no longer supports.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> VariantBatchAsync(string? cursor)
        {
            var after = string.IsNullOrEmpty(cursor) ? null : cursor;
            var result = new PhotoIngestBatchResult { NextCursor = after ?? "" };
            using var db = dbFactory();

            var query = VariantQueue(db);
            if (after != null) query = query.Where(a => string.Compare(a.Path, after) > 0);
            var rows = await query.OrderBy(a => a.Path).Take(Math.Max(1, options.BatchSize)).ToListAsync();
            if (rows.Count == 0)
            {
                result.Remaining = 0;
                return result;
            }

            // The extension query pages by the SAME comparison the cursor uses, so its final row is the
            // greatest path under the DATABASE's ordering. Taking an ordinal max in memory instead would
            // disagree with a case-insensitive server collation and silently skip the rows in between.
            var extra = await RestOfDirectoryAsync(db, rows[rows.Count - 1].Path);
            var lastPath = extra.Count > 0 ? extra[extra.Count - 1].Path : rows[rows.Count - 1].Path;
            rows.AddRange(extra);

            foreach (var cluster in rows.GroupBy(a => PhotoVariantPairs.Key(a.Path)))
            {
                var members = cluster.OrderBy(a => a.Path, StringComparer.Ordinal).ToList();
                foreach (var asset in members)
                    if (asset.Kind == PhotoAssetKind.Photo && PhotoVariantPairs.LooksLikeEmbeddedMotionPhoto(asset))
                        result.Add("embedded-motion-photo");

                var rule = PhotoVariantPairs.Classify(members, options.Variant);
                if (rule == null)
                {
                    await DissolveVariantAsync(db, members, result);
                    continue;
                }

                await SyncGroupAsync(db, PhotoDupeGroupKind.Variant, members,
                    PhotoDupeGroupStatus.Resolved, PhotoDupeMasters.PickVariantMaster, null, result);
                result.Add(rule);
            }
            result.Processed = rows.Count;

            await db.SaveChangesAsync();

            result.NextCursor = lastPath;
            result.Remaining = await VariantQueue(db).CountAsync(a => string.Compare(a.Path, lastPath) > 0);
            return result;
        }

        /// <summary>The remaining files sitting DIRECTLY in the last row's directory — the extension that
        /// keeps a stem cluster whole across a batch boundary. Bounded by one directory, expressed as a
        /// prefix match plus "no further separator" so it is one query and not a tree walk.</summary>
        private static async Task<List<PhotoAsset>> RestOfDirectoryAsync(MovieDb db, string lastPath)
        {
            var slash = lastPath.LastIndexOf('/');
            var query = VariantQueue(db).Where(a => string.Compare(a.Path, lastPath) > 0);
            if (slash < 0)
            {
                query = query.Where(a => !a.Path.Contains("/"));
            }
            else
            {
                var prefix = lastPath.Substring(0, slash + 1);
                var length = prefix.Length;
                query = query.Where(a => a.Path.StartsWith(prefix) && !a.Path.Substring(length).Contains("/"));
            }
            return await query.OrderBy(a => a.Path).ToListAsync();
        }

        /// <summary>Removes assets from any Variant group they are in, deleting a group that falls below
        /// two members. Auto-pairing owns these groups outright, so an assertion the data stopped
        /// supporting is withdrawn rather than left standing.</summary>
        private async Task DissolveVariantAsync(MovieDb db, List<PhotoAsset> assets, PhotoIngestBatchResult result)
        {
            var ids = assets.Select(a => a.Id).ToList();
            var memberships = await db.PhotoDupeMembers
                .Where(m => ids.Contains(m.PhotoAssetId) && m.PhotoDupeGroup.Kind == PhotoDupeGroupKind.Variant)
                .Select(m => m.PhotoDupeGroupId)
                .Distinct()
                .ToListAsync();
            if (memberships.Count == 0) return;

            var groups = await db.PhotoDupeGroups
                .Where(g => memberships.Contains(g.Id))
                .Include(g => g.Members)
                .ToListAsync();
            foreach (var group in groups)
            {
                var leaving = group.Members.Where(m => ids.Contains(m.PhotoAssetId)).ToList();
                db.PhotoDupeMembers.RemoveRange(leaving);
                foreach (var m in leaving) group.Members.Remove(m);
                result.Add("revalidated-out", leaving.Count);

                if (group.Members.Count < 2)
                {
                    db.PhotoDupeMembers.RemoveRange(group.Members);
                    db.PhotoDupeGroups.Remove(group);
                    result.Add("groups-dissolved");
                }
                else
                {
                    await EnsureMasterAsync(db, group, PhotoDupeMasters.PickVariantMaster, result);
                }
            }
        }

        // ── Shared group upsert ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Makes the group of <paramref name="kind"/> containing <paramref name="wanted"/> be exactly
        /// that set — creating it, merging the several it may have become, dropping members the data no
        /// longer supports, and settling the master.
        ///
        /// <para>Used by the two lanes whose membership is KNOWN outright (a SHA's rows; a stem
        /// cluster's files). Near cannot use it: its membership grows one neighbour at a time and is
        /// never closed, which is why <see cref="LinkNearAsync"/> exists separately.</para>
        ///
        /// <para><b>A human's master pick is never overwritten.</b> A group somebody resolved
        /// (<see cref="PhotoDupeGroup.ResolvedByUserId"/> set) keeps its master as long as that member is
        /// still in the group. Machine-settled Variant groups have no such user and are re-derived
        /// freely, which is what keeps re-runs identical.</para>
        /// </summary>
        private async Task SyncGroupAsync(MovieDb db, PhotoDupeGroupKind kind, List<PhotoAsset> wanted,
            PhotoDupeGroupStatus statusForNew, Func<IEnumerable<PhotoAsset>, PhotoAsset> masterPicker,
            Dictionary<int, double?>? similarity, PhotoIngestBatchResult result)
        {
            // A human's "not the same photo" binds here too — see EnsureRejectedPairsAsync.
            await EnsureRejectedPairsAsync(db);
            wanted = WithoutRejectedPairs(wanted, result);
            if (wanted.Count < 2) return;

            var wantedIds = wanted.Select(a => a.Id).ToHashSet();
            var groupIds = await db.PhotoDupeMembers
                .Where(m => wantedIds.Contains(m.PhotoAssetId)
                            && m.PhotoDupeGroup.Kind == kind
                            && m.PhotoDupeGroup.Status != PhotoDupeGroupStatus.Rejected)
                .Select(m => m.PhotoDupeGroupId)
                .Distinct()
                .ToListAsync();

            if (groupIds.Count == 0)
            {
                var created = new PhotoDupeGroup
                {
                    Kind = kind,
                    Status = statusForNew,
                    CreatedUtc = DateTime.UtcNow,
                    // A machine-settled group records WHEN, and deliberately no WHO: a Variant pair is
                    // not somebody's judgement, and stamping a user on it would misreport the record.
                    ResolvedUtc = statusForNew == PhotoDupeGroupStatus.Resolved ? DateTime.UtcNow : (DateTime?)null,
                };
                db.PhotoDupeGroups.Add(created);
                foreach (var asset in wanted.OrderBy(a => a.Id))
                    created.Members.Add(new PhotoDupeMember
                    {
                        PhotoDupeGroup = created,
                        PhotoAsset = asset,
                        PhotoAssetId = asset.Id,
                        Similarity = similarity != null && similarity.TryGetValue(asset.Id, out var s) ? s : null,
                    });
                await EnsureMasterAsync(db, created, masterPicker, result);
                result.Add("groups-created");
                return;
            }

            var groups = await db.PhotoDupeGroups
                .Where(g => groupIds.Contains(g.Id))
                .Include(g => g.Members).ThenInclude(m => m.PhotoAsset)
                .OrderBy(g => g.Id)
                .ToListAsync();
            var target = groups[0];

            // Merge the strays into the lowest-id group. Two groups for one SHA can only happen after a
            // re-ingest changed content underneath them, and leaving both would collapse the timeline
            // twice for one photograph.
            foreach (var other in groups.Skip(1))
            {
                foreach (var member in other.Members.ToList())
                {
                    db.PhotoDupeMembers.Remove(member);
                    other.Members.Remove(member);
                }
                db.PhotoDupeGroups.Remove(other);
                result.Add("groups-merged");
            }

            var stale = target.Members.Where(m => !wantedIds.Contains(m.PhotoAssetId)).ToList();
            if (stale.Count > 0)
            {
                db.PhotoDupeMembers.RemoveRange(stale);
                foreach (var m in stale) target.Members.Remove(m);
                result.Add("revalidated-out", stale.Count);
            }

            var added = 0;
            foreach (var asset in wanted.OrderBy(a => a.Id))
            {
                if (target.Members.Any(m => m.PhotoAssetId == asset.Id)) continue;
                target.Members.Add(new PhotoDupeMember
                {
                    PhotoDupeGroup = target,
                    PhotoAsset = asset,
                    PhotoAssetId = asset.Id,
                    Similarity = similarity != null && similarity.TryGetValue(asset.Id, out var s) ? s : null,
                });
                added++;
            }
            if (added > 0) result.Add("members-added", added);

            if (target.Members.Count < 2)
            {
                db.PhotoDupeMembers.RemoveRange(target.Members);
                db.PhotoDupeGroups.Remove(target);
                result.Add("groups-dissolved");
                return;
            }

            await EnsureMasterAsync(db, target, masterPicker, result);
        }

        /// <summary>
        /// Exactly one master, per §2.6's heuristic — and the same one on every re-run, which is the
        /// whole reason the picker's last comparison is the id.
        ///
        /// <para>A MOVE clears the old flag in its own round trip before setting the new one.
        /// <c>IX_PhotoDupeMember_Master</c> is a filtered UNIQUE index, and the order in which a single
        /// SaveChanges emits two updates is not ours to choose — "no master for an instant" is legal,
        /// "two masters for an instant" is not.
        /// </para>
        /// </summary>
        private static async Task EnsureMasterAsync(MovieDb db, PhotoDupeGroup group,
            Func<IEnumerable<PhotoAsset>, PhotoAsset> picker, PhotoIngestBatchResult result)
        {
            var members = group.Members.ToList();
            if (members.Count == 0) return;

            var human = group.ResolvedByUserId != null;
            var existing = members.FirstOrDefault(m => m.IsMaster);
            if (human && existing != null) return;

            var wanted = picker(members.Select(m => m.PhotoAsset));
            if (existing != null && existing.PhotoAssetId == wanted.Id) return;

            if (existing != null)
            {
                existing.IsMaster = false;
                if (db.Entry(existing).State != EntityState.Added) await db.SaveChangesAsync();
            }
            foreach (var member in members) member.IsMaster = member.PhotoAssetId == wanted.Id;
            result.Add(existing == null ? "masters-set" : "masters-moved");
        }

        private static (string Phase, string Mark) ParsePhaseCursor(string? cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return ("v", "0");
            var colon = cursor!.IndexOf(':');
            if (colon < 0) return ("s", cursor);
            return (cursor.Substring(0, colon), cursor.Substring(colon + 1));
        }
    }
}
