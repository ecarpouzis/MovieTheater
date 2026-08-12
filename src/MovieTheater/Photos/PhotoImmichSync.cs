using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Photos
{
    /// <summary>Which lane a <c>photos-sync-immich</c> run is draining. Ordered, because each depends on
    /// the one before it: nothing can be tagged before its asset is mapped, and no face can become a
    /// suggestion before its cluster has a <see cref="FamilyPerson"/> row to hang on.</summary>
    public enum PhotoImmichPass
    {
        /// <summary>Map Immich assets onto ours by root-relative path suffix; stamp
        /// <see cref="PhotoAsset.ImmichAssetId"/>; fill <see cref="PhotoAsset.LocationLabel"/> where —
        /// and only where — it is null.</summary>
        Assets,

        /// <summary>Import face clusters as <see cref="FamilyPerson"/> rows, linked by
        /// <see cref="FamilyPerson.ImmichPersonId"/>. A cluster nobody has named here arrives UNNAMED.</summary>
        People,

        /// <summary>Turn each mapped asset's faces into <see cref="PhotoTagSource.Suggested"/> tags.</summary>
        Faces,

        /// <summary>Append Immich's own duplicate candidates to the §2.6 Near lane as Pending groups.</summary>
        Duplicates,
    }

    public sealed class PhotoImmichSyncOptions
    {
        /// <summary>Units per batch: Immich page size for the paged lanes, our rows for the face lane.</summary>
        public int BatchSize = 200;

        /// <summary>How many trailing path segments a mapping match needs (§2.4). See
        /// <see cref="ImmichClient.DefaultSuffixSegments"/> for why two.</summary>
        public int SuffixSegments = ImmichClient.DefaultSuffixSegments;

        /// <summary>Where cached face crops are written, when this host can write them. Null simply
        /// means the tag queue draws boxes over our own derivatives instead (§2.4).</summary>
        public string? ThumbCacheDir;

        /// <summary>Report what WOULD be written and write nothing. The first run against a real
        /// collection is a human-supervised checkpoint, and this is what makes that possible.</summary>
        public bool DryRun;
    }

    /// <summary>
    /// The <c>photos-sync-immich</c> engine (docs/photos-plan.md §2.4).
    ///
    /// <para><b>It writes SUGGESTIONS, never truth.</b> Face clusters land as
    /// <see cref="PhotoTagSource.Suggested"/> rows a human promotes or refuses; reverse-geocode labels
    /// fill <see cref="PhotoAsset.LocationLabel"/> ONLY where it is null; duplicate candidates are
    /// Pending Near groups nobody has agreed to. Nothing here auto-confirms anything, and nothing here
    /// touches a file (§6) — the sidecar's own library mount is read-only CIFS precisely so that is
    /// physically true and not merely intended.</para>
    ///
    /// <para><b>Immich is disposable.</b> Every id this pass stores is re-derivable from paths, so the
    /// container and its database can be thrown away and rebuilt without losing a single row of ours.
    /// Pulling it leaves hand-tagging working exactly as it did — which is the acceptance criterion §5
    /// Phase 4 states, and the reason none of this is on the read path of any browse surface.</para>
    ///
    /// <para><b>Bulk-job contract</b>, as every pass here: bounded work per call,
    /// <c>{processed, remaining, nextCursor}</c> per chunk, an audited cursor ordering, idempotent
    /// re-runs, and a deterministic no-progress stop. The one honest deviation is that the paged Immich
    /// lanes cannot report a true <c>remaining</c> — the sidecar answers "is there another page", not
    /// "how many are left" — so those lanes report 1/0 and say so in the log rather than inventing a
    /// count. The face lane, which pages OUR rows, reports a real one.</para>
    /// </summary>
    public sealed class PhotoImmichSync
    {
        private readonly Func<MovieDb> dbFactory;
        private readonly IImmichApi immich;
        private readonly PhotoImmichSyncOptions options;
        private readonly Action<string> log;

        /// <summary>Our live paths, indexed by suffix key, built ONCE per run.
        /// <para>Cost, stated because it is paid up front: one projection query and a dictionary of
        /// (key, id) — tens of MB and about a second at 150k photos, the same profile as the near lane's
        /// hash index. It exists because the alternative is a <c>LIKE '%…'</c> per Immich asset, which is
        /// a full scan per asset and would make a sync of a real collection unfinishable.</para></summary>
        private Dictionary<string, List<int>>? pathIndex;

        /// <summary>Immich's duplicate candidates, fetched once per run: the route is not paged, so the
        /// chunking happens over the fetched list rather than over the wire.</summary>
        private List<ImmichDuplicateGroup>? duplicates;

        public PhotoImmichSync(Func<MovieDb> dbFactory, IImmichApi immich, PhotoImmichSyncOptions options,
            Action<string> log)
        {
            this.dbFactory = dbFactory;
            this.immich = immich;
            this.options = options;
            this.log = log;
        }

        // ── Driver ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Runs up to <paramref name="maxBatches"/> bounded batches of one lane (0 drains),
        /// printing the per-chunk line the standing rule requires and stopping deterministically.</summary>
        public async Task<PhotoIngestBatchResult> RunAsync(PhotoImmichPass pass, string? cursor, int maxBatches,
            CancellationToken cancel = default)
        {
            var total = new PhotoIngestBatchResult { NextCursor = cursor ?? "" };
            var batches = 0;
            while (maxBatches <= 0 || batches < maxBatches)
            {
                var result = await BatchAsync(pass, batches == 0 ? cursor : total.NextCursor, cancel);
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

        public Task<PhotoIngestBatchResult> BatchAsync(PhotoImmichPass pass, string? cursor,
            CancellationToken cancel = default) => pass switch
        {
            PhotoImmichPass.Assets => AssetsBatchAsync(cursor, cancel),
            PhotoImmichPass.People => PeopleBatchAsync(cursor, cancel),
            PhotoImmichPass.Faces => FacesBatchAsync(cursor, cancel),
            PhotoImmichPass.Duplicates => DuplicatesBatchAsync(cursor, cancel),
            _ => throw new ArgumentOutOfRangeException(nameof(pass)),
        };

        // ── Assets (§2.4 mapping + §2.4 geocode) ─────────────────────────────────────────────────

        /// <summary>
        /// One page of Immich assets, mapped onto our rows by path suffix.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> The cursor is the Immich page number and the page
        /// query asks for exactly that page, in the sidecar's own stable order — one dimension, one
        /// direction, in the request and in the cursor. Immich reports <c>hasNextPage</c> itself, which
        /// is what terminates the lane; a short page is NOT treated as the end, because a filtered
        /// search can legitimately return fewer than asked for.</para>
        ///
        /// <para><b>Ambiguity is refused, never guessed</b> (the §2.5 identity stance): an Immich path
        /// whose suffix matches several of our rows is counted and skipped. Mapping the wrong photograph
        /// would attach a stranger's face suggestions to a family picture.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> AssetsBatchAsync(string? cursor, CancellationToken cancel)
        {
            var page = ParsePage(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = "a:" + page.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();

            await EnsurePathIndexAsync(db);

            var batch = await immich.AssetsAsync(page, Math.Max(1, options.BatchSize), cancel);
            if (batch.Items.Count == 0)
            {
                result.Remaining = batch.HasNextPage ? 1 : 0;
                return result;
            }

            foreach (var item in batch.Items)
            {
                result.Processed++;
                var key = ImmichClient.SuffixKey(item.OriginalPath, options.SuffixSegments);
                if (key.Length == 0 || !pathIndex!.TryGetValue(key, out var ids))
                {
                    result.Add("unmapped");
                    continue;
                }
                if (ids.Count > 1)
                {
                    result.Add("ambiguous-path");
                    continue;
                }

                var asset = await db.PhotoAssets.FirstOrDefaultAsync(a => a.Id == ids[0], cancel);
                if (asset == null) { result.Add("unmapped"); continue; }

                if (!string.Equals(asset.ImmichAssetId, item.Id, StringComparison.Ordinal))
                {
                    if (!options.DryRun) asset.ImmichAssetId = item.Id;
                    result.Add("mapped");
                }
                else
                {
                    result.Add("already-mapped");
                }

                var label = GeocodeLabel(item);
                if (label != null)
                {
                    // ONLY where null (§2.4). A label a family member typed, or one a Takeout sidecar
                    // supplied, outranks a machine's guess from GPS — and this pass is the machine.
                    if (asset.LocationLabel == null)
                    {
                        if (!options.DryRun)
                        {
                            asset.LocationLabel = label.Length > 256 ? label.Substring(0, 256) : label;
                            asset.LocationSource = PhotoLocationSource.ImmichGeocode;
                        }
                        result.Add("geocode-filled");
                    }
                    else
                    {
                        result.Add("geocode-kept-existing");
                    }
                }
            }

            if (!options.DryRun) await db.SaveChangesAsync(cancel);

            result.NextCursor = "a:" + (page + 1).ToString(CultureInfo.InvariantCulture);
            // The sidecar answers "is there another page", not "how many assets remain", so this is a
            // boolean wearing a number's clothes. Said plainly rather than dressed up as a count.
            result.Remaining = batch.HasNextPage ? 1 : 0;
            return result;
        }

        /// <summary>"City, State" (or the best of what the offline geodata had). Null when the photo
        /// carried no GPS, which is most scans and many indoor phone shots.</summary>
        private static string? GeocodeLabel(ImmichAsset asset)
        {
            var parts = new[] { asset.City, asset.State, asset.Country }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.Trim())
                .ToList();
            if (parts.Count == 0) return null;
            // Country alone is not a place a family recognizes; it is kept only as a last resort.
            return string.Join(", ", parts.Take(2));
        }

        // ── People / clusters (§2.8) ─────────────────────────────────────────────────────────────

        /// <summary>
        /// One page of face clusters, each becoming (or finding) a <see cref="FamilyPerson"/> row linked
        /// by <see cref="FamilyPerson.ImmichPersonId"/>.
        ///
        /// <para><b>A cluster arrives UNNAMED, always.</b> Its row is created with an empty name, and the
        /// UI shows it as "unnamed group of N faces" for a family member to name — or to map onto a
        /// person who already exists. Immich's own name for a cluster is deliberately NOT imported: names
        /// are the family's, they live in our rows and nowhere else (§6), and a machine inventing one
        /// would be the auto-confirmation §2.8 forbids wearing a different hat. Naming it here is what
        /// "links" it, and that single act fans its suggestions across the library.</para>
        ///
        /// <para><b>Cursor-ordering audit (§6):</b> the cursor is the Immich page number; the request
        /// asks for that page and the sidecar reports <c>hasNextPage</c>. Same one dimension in both.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> PeopleBatchAsync(string? cursor, CancellationToken cancel)
        {
            var page = ParsePage(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = "p:" + page.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();

            var batch = await immich.PeopleAsync(page, Math.Max(1, options.BatchSize), cancel);
            if (batch.People.Count == 0)
            {
                result.Remaining = batch.HasNextPage ? 1 : 0;
                return result;
            }

            var clusterIds = batch.People.Select(p => p.Id).Where(id => id.Length > 0).ToList();
            var known = await db.FamilyPeople
                .Where(p => p.ImmichPersonId != null && clusterIds.Contains(p.ImmichPersonId))
                .ToListAsync(cancel);
            var byCluster = known.ToDictionary(p => p.ImmichPersonId!, StringComparer.Ordinal);

            foreach (var cluster in batch.People)
            {
                result.Processed++;
                if (cluster.Id.Length == 0) continue;
                if (byCluster.ContainsKey(cluster.Id)) { result.Add("clusters-already-linked"); continue; }

                if (!options.DryRun)
                {
                    var person = new FamilyPerson
                    {
                        // Empty on purpose — see the remarks above. This is the "unnamed group of N
                        // faces" state, and it is the only state a machine may create a person in.
                        Name = "",
                        ImmichPersonId = cluster.Id,
                        CreatedUtc = DateTime.UtcNow,
                    };
                    db.FamilyPeople.Add(person);
                    byCluster[cluster.Id] = person;
                }
                result.Add("clusters-imported");
            }

            if (!options.DryRun) await db.SaveChangesAsync(cancel);

            // The face crop is fetched HERE — server-side, on the host that can actually reach the
            // LAN-only sidecar — and cached into the derivative cache the gateway already serves (§2.4).
            // The site then hands the browser an ordinary capability URL and never mentions Immich. A
            // failure is silent by design: no crop simply means the queue draws the box over our own
            // thumb instead, which is the same picture with a plainer frame.
            if (!options.DryRun && options.ThumbCacheDir != null)
                foreach (var cluster in batch.People.Where(p => p.Id.Length > 0))
                    if (await PhotoFaceCrops.EnsureAsync(options.ThumbCacheDir, immich, cluster.Id, cancel) != null)
                        result.Add("face-crops-cached");

            result.NextCursor = "p:" + (page + 1).ToString(CultureInfo.InvariantCulture);
            result.Remaining = batch.HasNextPage ? 1 : 0;
            return result;
        }

        // ── Faces → suggestions (§2.8) ───────────────────────────────────────────────────────────

        /// <summary>Our side of the face lane's queue: the assets Immich knows about.</summary>
        private static IQueryable<PhotoAsset> FaceQueue(MovieDb db) =>
            db.PhotoAssets.Where(a => a.ImmichAssetId != null && a.MissingSinceUtc == null);

        /// <summary>
        /// One bounded batch of mapped assets, each asked for its faces.
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> <c>WHERE Id &gt; cursor ORDER BY Id</c> over OUR rows,
        /// cursor = the last id examined — one column, one direction, in the page query and in the
        /// cursor. <c>remaining</c> is a real count here, taken from the database after the writes.</para>
        ///
        /// <para>Every suggestion goes through <see cref="PhotoPersonTags.SuggestAsync"/>, which carries
        /// the no-clobber rules: a human's Manual/Confirmed tag is never overwritten or duplicated, a
        /// Rejected tombstone is never revived, and an existing suggestion is refreshed in place. Those
        /// three together are what make re-running this pass converge rather than accumulate — the
        /// property "re-sync re-proposes nothing a human has answered" is measured on them.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> FacesBatchAsync(string? cursor, CancellationToken cancel)
        {
            var afterId = ParseId(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = "f:" + afterId.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();

            var rows = await FaceQueue(db)
                .Where(a => a.Id > afterId)
                .OrderBy(a => a.Id)
                .Take(Math.Max(1, options.BatchSize))
                .ToListAsync(cancel);
            if (rows.Count == 0)
            {
                result.Remaining = 0;
                return result;
            }

            // The cluster → person map, for this batch's faces. Loaded once: a family has tens of
            // people, not thousands, so this is a small table and a per-face lookup would be silly.
            var people = await db.FamilyPeople
                .Where(p => p.ImmichPersonId != null)
                .Select(p => new { p.Id, p.ImmichPersonId })
                .ToListAsync(cancel);
            var personByCluster = people
                .GroupBy(p => p.ImmichPersonId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Min(p => p.Id), StringComparer.Ordinal);

            var lastId = afterId;
            foreach (var asset in rows)
            {
                lastId = asset.Id;
                result.Processed++;

                var faces = await immich.FacesForAssetAsync(asset.ImmichAssetId!, cancel);
                foreach (var face in faces)
                {
                    if (face.PersonId.Length == 0) continue;
                    if (!personByCluster.TryGetValue(face.PersonId, out var personId))
                    {
                        // A cluster the People lane has not imported yet. Counted, not invented: creating
                        // a person here would race the lane whose job that is.
                        result.Add("faces-unlinked-cluster");
                        continue;
                    }
                    if (options.DryRun) { result.Add("suggestions-would-write"); continue; }

                    result.Add(await PhotoPersonTags.SuggestAsync(db, asset.Id, personId, face.PersonId,
                        face.Confidence, face.X, face.Y, face.W, face.H));
                }

                if (!options.DryRun) await db.SaveChangesAsync(cancel);
            }

            result.NextCursor = "f:" + lastId.ToString(CultureInfo.InvariantCulture);
            result.Remaining = await FaceQueue(db).CountAsync(a => a.Id > lastId, cancel);
            return result;
        }

        // ── Duplicate candidates → the Near lane (§2.6) ──────────────────────────────────────────

        /// <summary>
        /// Immich's own duplicate candidates, appended to the §2.6 Near lane as PENDING groups.
        ///
        /// <para>CLIP catches the crops and recolors a perceptual hash misses, which is the whole reason
        /// §2.6 asks for them — but they arrive with exactly the standing of a pHash candidate: proposed,
        /// never resolved, and a pair a human has already marked "not the same photo" is never proposed
        /// again. That last rule is not re-implemented here: the grouping is handed to
        /// <see cref="PhotoDupePass.LinkExternalNearAsync"/>, the same code path the near pass uses, so
        /// the rejected-pair check, the one-active-group-per-kind invariant and the master heuristic are
        /// literally the same code rather than a second copy that can drift.</para>
        ///
        /// <para><b>Cursor-ordering audit (§6).</b> The candidate list is fetched once per run and the
        /// cursor is an INDEX into it in the order the sidecar returned; the page takes
        /// <c>[index, index + batch)</c> from that same order. <c>remaining</c> is the rest of the list —
        /// a real count, because unlike the wire lanes this one holds the whole set.</para>
        /// </summary>
        private async Task<PhotoIngestBatchResult> DuplicatesBatchAsync(string? cursor, CancellationToken cancel)
        {
            var index = ParseId(cursor);
            var result = new PhotoIngestBatchResult { NextCursor = "d:" + index.ToString(CultureInfo.InvariantCulture) };
            using var db = dbFactory();

            duplicates ??= (await immich.DuplicatesAsync(cancel)).ToList();
            if (index >= duplicates.Count)
            {
                result.Remaining = 0;
                return result;
            }

            var take = Math.Min(Math.Max(1, options.BatchSize), duplicates.Count - index);
            var slice = duplicates.GetRange(index, take);

            // One lookup for the whole slice: Immich ids → our rows.
            var immichIds = slice.SelectMany(g => g.AssetIds).Distinct().ToList();
            var mapped = await db.PhotoAssets
                .Where(a => a.ImmichAssetId != null && immichIds.Contains(a.ImmichAssetId)
                            && a.MissingSinceUtc == null)
                .ToListAsync(cancel);
            var byImmichId = mapped
                .GroupBy(a => a.ImmichAssetId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id).First(), StringComparer.Ordinal);

            var pass = new PhotoDupePass(dbFactory, new PhotoDupeOptions(), _ => { });
            foreach (var group in slice)
            {
                result.Processed++;
                var members = group.AssetIds
                    .Select(id => byImmichId.TryGetValue(id, out var a) ? a : null)
                    .Where(a => a != null)
                    .Select(a => a!)
                    .GroupBy(a => a.Id).Select(g => g.First())
                    .OrderBy(a => a.Id)
                    .ToList();
                if (members.Count < 2) { result.Add("candidates-unmapped"); continue; }
                if (options.DryRun) { result.Add("candidates-would-group"); continue; }

                // Provenance: the candidate is stamped at the sidecar's own claim strength (see
                // ImmichCandidateDistance), and the count key below is what the run's report names — so
                // a group a human is looking at can be traced to the lane that proposed it.
                var head = members[0];
                var neighbours = members.Skip(1)
                    .Select(m => new PhotoHashNeighbour(m.Id, ImmichCandidateDistance))
                    .ToList();
                await pass.LinkExternalNearAsync(db, head, neighbours, result);
                result.Add("candidates-proposed");
            }

            if (!options.DryRun) await db.SaveChangesAsync(cancel);

            result.NextCursor = "d:" + (index + take).ToString(CultureInfo.InvariantCulture);
            result.Remaining = duplicates.Count - (index + take);
            return result;
        }

        /// <summary>
        /// The hash distance stamped on a member the sidecar proposed — zero, i.e. similarity 1.0.
        ///
        /// <para>Immich's duplicate job does not hand back a distance; it hands back "these are
        /// duplicates". Any number here is therefore a LABEL, not a measurement, and zero is the honest
        /// one: it says "this lane asserted sameness outright" rather than dressing an assertion up as a
        /// plausible-looking 0.97 that a reader would take for something the near lane computed.</para>
        /// </summary>
        public const int ImmichCandidateDistance = 0;

        // ── Shared ───────────────────────────────────────────────────────────────────────────────

        private async Task EnsurePathIndexAsync(MovieDb db)
        {
            if (pathIndex != null) return;

            var rows = await db.PhotoAssets
                .Where(a => a.MissingSinceUtc == null)
                .Select(a => new { a.Id, a.Path })
                .ToListAsync();

            var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var key = ImmichClient.SuffixKey(row.Path, options.SuffixSegments);
                if (key.Length == 0) continue;
                if (!index.TryGetValue(key, out var list)) index[key] = list = new List<int>();
                list.Add(row.Id);
            }
            pathIndex = index;

            var ambiguous = index.Count(kv => kv.Value.Count > 1);
            log($"  path index: {rows.Count} live assets, {index.Count} distinct {options.SuffixSegments}-segment keys"
                + (ambiguous > 0 ? $", {ambiguous} ambiguous (those will be skipped, never guessed)" : ""));
        }

        private static int ParsePage(string? cursor)
        {
            var mark = Mark(cursor);
            return int.TryParse(mark, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1;
        }

        private static int ParseId(string? cursor)
        {
            var mark = Mark(cursor);
            return int.TryParse(mark, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
        }

        /// <summary>Cursors are <c>phase:mark</c>, the <see cref="PhotoDupePass"/> shape, so a cursor
        /// pasted back into a different lane is visibly the wrong one rather than a silent restart.</summary>
        private static string Mark(string? cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return "";
            var colon = cursor!.IndexOf(':');
            return colon < 0 ? cursor : cursor.Substring(colon + 1);
        }
    }
}
