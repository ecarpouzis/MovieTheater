using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The <c>photos-suggest-hide</c> engine (docs/photos-plan.md §2.9): reads the catalogue in bounded
    /// batches and writes a REVIEWABLE PROPOSAL of what looks like clutter. It never sets
    /// <see cref="PhotoAsset.Hidden"/> — accepting a batch is a human action on the review surface, and
    /// this pass has no path to that flag at all.
    ///
    /// <para>Same bulk-job contract as the ingest pipeline: bounded rows per batch,
    /// <c>{processed, remaining, nextCursor}</c> after each, resume from the cursor (which is also
    /// persisted into the proposal, so a killed run continues from the file), and a no-progress safety
    /// break. Cursor ordering IS the query ordering — <c>Id</c> ascending in both — which is the rule a
    /// previous cursor bug on this repo was written in blood.</para>
    ///
    /// <para>No file is opened. Every heuristic reads columns the ingest already persisted, so this
    /// pass never touches the collection root and can run anywhere the database is reachable.</para>
    ///
    /// <para><b>Phase 3:</b> the proposal is written as <see cref="PhotoCurationBatch"/> rows rather
    /// than JSON under <c>PhotosReportDir</c>, so the site can actually read it in prod. The store is
    /// therefore built per batch around the batch's own context instead of being handed in.</para>
    /// </summary>
    public sealed class PhotoSuggestHidePass
    {
        private readonly Func<MovieDb> dbFactory;
        private readonly PhotoHideSuggestions.Options rules;
        private readonly int batchSize;
        private readonly Action<string> log;

        public PhotoSuggestHidePass(Func<MovieDb> dbFactory,
            PhotoHideSuggestions.Options rules, int batchSize, Action<string> log)
        {
            this.dbFactory = dbFactory;
            this.rules = rules;
            this.batchSize = Math.Max(1, batchSize);
            this.log = log;
        }

        /// <summary>Runs up to <paramref name="maxBatches"/> bounded batches (0 drains), printing the
        /// per-chunk line the standing rule requires and stopping deterministically.</summary>
        public async Task<PhotoIngestBatchResult> RunAsync(string batchId, string? cursor, int maxBatches)
        {
            var total = new PhotoIngestBatchResult { NextCursor = cursor ?? "0" };
            var batches = 0;
            while (maxBatches <= 0 || batches < maxBatches)
            {
                var result = await BatchAsync(batchId, ParseCursor(batches == 0 ? cursor : total.NextCursor));
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
                    log("No progress in a batch while rows remained — stopping.");
                    break;
                }
            }
            return total;
        }

        /// <summary>One bounded batch. The <see cref="PhotoAsset"/> rows are read-only here; the only
        /// write is the proposal batch itself.</summary>
        public async Task<PhotoIngestBatchResult> BatchAsync(string batchId, int cursorId)
        {
            var result = new PhotoIngestBatchResult { NextCursor = cursorId.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();
            var store = new PhotoCurationStore(db);

            var rows = await Queue(db).Where(a => a.Id > cursorId).OrderBy(a => a.Id).Take(batchSize).ToListAsync();
            if (rows.Count == 0)
            {
                result.Remaining = 0;
                // An empty tail still closes the proposal: "the pass drained" is a fact the review
                // surface shows, and it can only be learned here.
                await store.AppendProposalAsync(batchId, Array.Empty<PhotoHideProposalItem>(), result.NextCursor, complete: true);
                return result;
            }

            var proposed = new List<PhotoHideProposalItem>();
            foreach (var row in rows)
            {
                var rule = PhotoHideSuggestions.Evaluate(row, rules);
                if (rule == null) continue;
                proposed.Add(new PhotoHideProposalItem
                {
                    AssetId = row.Id,
                    Path = row.Path,
                    Sha256 = row.Sha256,
                    Rule = rule,
                });
                result.Add(rule);
            }

            result.Processed = rows.Count;
            result.NextCursor = rows[rows.Count - 1].Id.ToString(CultureInfo.InvariantCulture);
            // Independently recounted from the database rather than decremented, so an early "done"
            // cannot be faked by a miscounted batch.
            var lastId = rows[rows.Count - 1].Id;
            result.Remaining = await Queue(db).CountAsync(a => a.Id > lastId);
            result.Add("proposed", proposed.Count);

            await store.AppendProposalAsync(batchId, proposed, result.NextCursor, complete: result.Remaining <= 0);

            return result;
        }

        /// <summary>
        /// What the pass examines: live rows that are not hidden already. Missing files are excluded —
        /// proposing to hide something the walk can no longer find would review a decision about a file
        /// that is not there, and <c>MissingSinceUtc</c> already carries that fact.
        /// </summary>
        private static IQueryable<PhotoAsset> Queue(MovieDb db) =>
            db.PhotoAssets.Where(a => a.MissingSinceUtc == null && !a.Hidden);

        private static int ParseCursor(string? cursor) =>
            int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
