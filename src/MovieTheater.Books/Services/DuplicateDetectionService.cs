using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Services
{
    /// <summary>What one dedup batch did.</summary>
    public sealed record DedupBatchResult(int Processed, long Remaining, long? NextCursor, int Groups, int Duplicates)
    {
        public bool Done => Processed == 0 || NextCursor == null;
        public override string ToString() =>
            $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\" }}  [dedup, groups: {Groups}, duplicates: {Duplicates}]";
    }

    /// <summary>
    /// <c>books-dedup</c> — find the same book stored twice and say which copy to keep.
    ///
    /// <para><b>Three signals, strongest first.</b> A shared `ContentFingerprint` is a byte-identical file; a
    /// shared `PageSignature` is the same comic re-zipped or re-scanned; a shared `CoverPHash` is the same cover
    /// at (probably) different quality. Each is a `DuplicateGroup` with a relationship that says which.</para>
    ///
    /// <para><b>The keeper is a suggestion, never an action.</b> Nothing is deleted or hidden by this job — it
    /// writes groups and members and the review state stays Pending until someone resolves it. Resolving marks
    /// the losers `IsExcluded` (hidden, still on disk, still in the Directory drill); the file is never touched.</para>
    ///
    /// <para><b>The keeper heuristic is biased by what the reader has done.</b> A copy the reader has opened,
    /// marked or rated wins outright — losing that state to a "better" copy is worse than keeping a slightly
    /// smaller scan. After that: a canonical series folder beats an event/chronology re-gathering tree, which
    /// beats an unsorted holding folder; then depth, cover area, a ComicVine match, and file size.</para>
    ///
    /// <para><b>Chunked by `Item.Id`, grouped across the whole table.</b> A page is the WALK, not the group
    /// boundary: for every signature the page carries, the matching items are pulled from the entire
    /// `ItemSignature` table, so two copies 100k ids apart still meet (until 2026-09-01 a group could only form
    /// inside one page). An item that already sits in a group — from an earlier page, an earlier run or a
    /// resolved decision — is skipped, which is what makes a re-run idempotent, and an in-run `claimed` set does
    /// the same for a dry run, which persists nothing. The dry run's cursor lives in the VERB (`after`), exactly
    /// like `books-import-calibre`; only `--apply` persists it.</para>
    /// </summary>
    public sealed class DuplicateDetectionService
    {
        public const string CursorKey = "books:dedup:cursor";
        public const int DefaultBatchSize = 5_000;

        /// <summary>0 = byte-identical, 1 = identical contents, 2 = same comic different scan, 3 = contained in.</summary>
        public const int IdenticalFile = 0, IdenticalContents = 1, SameComicDifferentScan = 2, ContainedIn = 3;

        /// <summary>Folder markers that suggest a holding/unsorted location — the LOSER when picking a keeper.</summary>
        public static readonly string[] UnsortedMarkers =
            { "unsorted", "incoming", "to sort", "tosort", "duplicate", "dupe", "_new", "new folder", "temp" };

        /// <summary>
        /// Markers for an event / chronology READING-ORDER tree — a re-gathering of issues that also live in
        /// their own series folders. A copy sitting in one LOSES keeper selection, so the canonical series copy
        /// stays visible and the event copy becomes the directory-only shadow. Plural "events" / "crossovers"
        /// deliberately, so a real series whose title contains a singular "Crossover" is not caught.
        /// </summary>
        public static readonly string[] EventTreeMarkers =
            { "chronolog", "events", "read order", "reading order", "crossovers" };

        private readonly ILogger<DuplicateDetectionService> logger;
        public DuplicateDetectionService(ILogger<DuplicateDetectionService> logger) => this.logger = logger;

        /// <summary>One candidate copy, with everything the keeper heuristic weighs.</summary>
        public sealed record Candidate(
            int Id, string Path, string FileName, long FileSize, int PageCount, int FolderId,
            string? ContentFingerprint, long? CoverPHash, string? PageSignature,
            int CoverArea, bool HasCv, bool HasUserState);

        public async Task ResetAsync(BooksDb db, CancellationToken ct = default)
        {
            var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == CursorKey, ct);
            if (row != null) db.SystemStates.Remove(row);
            await db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// One bounded batch. <paramref name="csv"/>, when given, receives one line per member — the sheet an
        /// operator reviews outside the app. <paramref name="after"/> overrides the persisted cursor (the dry
        /// run's cursor, kept by the caller); <paramref name="claimed"/> is the caller's in-run set of item ids
        /// already grouped, so a dry run — which writes no membership rows — does not report a copy twice.
        /// </summary>
        public async Task<DedupBatchResult> RunBatchAsync(
            BooksDb db, int batchSize, bool apply = true, TextWriter? csv = null, CancellationToken ct = default,
            long? after = null, ISet<int>? claimed = null)
        {
            batchSize = Math.Clamp(batchSize, 1, 50_000);
            var cursor = after ?? await ReadCursorAsync(db, ct);
            claimed ??= new HashSet<int>();

            var page = await db.Items.AsNoTracking()
                .Where(i => i.Id > cursor && !i.IsExcluded)
                .OrderBy(i => i.Id).Take(batchSize)
                .Select(i => new { i.Id, i.Path, i.FileName, i.FileSize, i.PageCount, i.FolderId })
                .ToListAsync(ct);
            if (page.Count == 0) return new DedupBatchResult(0, 0, null, 0, 0);

            var pageLast = page[^1].Id;
            // Already grouped (any run, any state) → not a candidate again. One indexed range read over the
            // page's contiguous id span, never an id IN-list.
            var grouped = (await db.DuplicateMembers.AsNoTracking()
                .Where(m => m.ItemId > cursor && m.ItemId <= pageLast)
                .Select(m => m.ItemId).ToListAsync(ct)).ToHashSet();
            var seeds = page.Where(p => !grouped.Contains(p.Id) && !claimed.Contains(p.Id)).ToList();
            if (seeds.Count == 0)
            {
                if (apply) { await WriteCursorAsync(db, pageLast, ct); await db.SaveChangesAsync(ct); }
                var left = await db.Items.AsNoTracking().CountAsync(i => i.Id > pageLast && !i.IsExcluded, ct);
                return new DedupBatchResult(page.Count, left, pageLast, 0, 0);
            }

            var seedIds = seeds.Select(p => p.Id).ToList();
            var signatures = await ReadSignaturesAsync(db, seedIds, ct);

            // The other half of every match may live ANYWHERE in the table: pull every item sharing one of the
            // page's signature values, then load those items too. Chunked IN-lists keep SQLite under its cap.
            var partnerIds = await PartnerIdsAsync(db, signatures.Values, ct);
            partnerIds.ExceptWith(seedIds);
            if (partnerIds.Count > 0)
            {
                var memberElsewhere = new HashSet<int>();
                foreach (var chunk in Chunk(partnerIds.ToList(), 400))
                    memberElsewhere.UnionWith(await db.DuplicateMembers.AsNoTracking().Where(m => chunk.Contains(m.ItemId)).Select(m => m.ItemId).ToListAsync(ct));
                partnerIds.ExceptWith(memberElsewhere);
                partnerIds.ExceptWith(claimed);
            }
            var partners = new List<(int Id, string Path, string FileName, long FileSize, int? PageCount, int FolderId)>();
            foreach (var chunk in Chunk(partnerIds.ToList(), 400))
                partners.AddRange(await db.Items.AsNoTracking()
                    .Where(i => chunk.Contains(i.Id) && !i.IsExcluded)
                    .Select(i => new ValueTuple<int, string, string, long, int?, int>(i.Id, i.Path, i.FileName, i.FileSize, i.PageCount, i.FolderId))
                    .ToListAsync(ct));
            foreach (var kv in await ReadSignaturesAsync(db, partners.Select(p => p.Id).ToList(), ct)) signatures[kv.Key] = kv.Value;

            var all = seeds.Select(p => (p.Id, p.Path, p.FileName, p.FileSize, p.PageCount, p.FolderId)).Concat(partners).ToList();
            var ids = all.Select(p => p.Id).ToList();
            var states = new Dictionary<int, ItemState>();
            var cvLinked = new HashSet<int>();
            var userState = new HashSet<int>();
            foreach (var chunk in Chunk(ids, 400))
            {
                foreach (var st in await db.ItemStates.AsNoTracking().Where(s => chunk.Contains(s.ItemId)).ToListAsync(ct)) states[st.ItemId] = st;
                cvLinked.UnionWith(await db.ItemProviderLinks.AsNoTracking()
                    .Where(l => chunk.Contains(l.ItemId) && l.Provider == Provider.Cv && l.Status == LinkStatus.Matched)
                    .Select(l => l.ItemId).ToListAsync(ct));
                userState.UnionWith(await db.UserItemStates.AsNoTracking().Where(s => chunk.Contains(s.ItemId)).Select(s => s.ItemId).ToListAsync(ct));
            }

            var candidates = all.Select(p =>
            {
                signatures.TryGetValue(p.Id, out var sig);
                states.TryGetValue(p.Id, out var st);
                return new Candidate(
                    p.Id, p.Path, p.FileName, p.FileSize, p.PageCount ?? 0, p.FolderId,
                    sig?.ContentFingerprint, sig?.CoverPHash, sig?.PageSignature,
                    (st?.CoverWidth ?? 0) * (st?.CoverHeight ?? 0),
                    cvLinked.Contains(p.Id), userState.Contains(p.Id));
            }).ToList();

            var clusters = BuildClusters(candidates);
            foreach (var c in clusters) foreach (var m in c.Members) claimed.Add(m.Id);

            var folderCounts = await db.Items.AsNoTracking()
                .Where(i => candidates.Select(c => c.FolderId).Contains(i.FolderId))
                .GroupBy(i => i.FolderId).Select(g => new { FolderId = g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.FolderId, x => x.N, ct);

            var nextGroupId = (await db.DuplicateGroups.AsNoTracking().Select(g => (int?)g.Id).MaxAsync(ct) ?? 0) + 1;
            var nextMemberId = (await db.DuplicateMembers.AsNoTracking().Select(m => (int?)m.Id).MaxAsync(ct) ?? 0) + 1;

            int groups = 0, duplicates = 0;
            foreach (var cluster in clusters)
            {
                ct.ThrowIfCancellationRequested();
                var keeper = PickKeeper(cluster.Members, cluster.Relationship);
                if (apply)
                {
                    db.DuplicateGroups.Add(new DuplicateGroup
                    {
                        Id = nextGroupId,
                        Relationship = cluster.Relationship,
                        Confidence = cluster.Confidence,
                        Evidence = cluster.Evidence,
                        SuggestedKeeperItemId = keeper?.Id,
                        ReviewState = "Pending",
                        DetectedAt = DateTime.UtcNow,
                    });
                }
                foreach (var m in cluster.Members)
                {
                    var role = keeper == null ? "Member" : m.Id == keeper.Id ? "Keeper" : "Duplicate";
                    if (apply)
                        db.DuplicateMembers.Add(new DuplicateMember
                        {
                            Id = nextMemberId++,
                            DuplicateGroupId = nextGroupId,
                            ItemId = m.Id,
                            Role = role,
                            SoleFileInFolder = folderCounts.GetValueOrDefault(m.FolderId) == 1,
                        });
                    if (role == "Duplicate") duplicates++;
                    csv?.WriteLine(string.Join(",", new[]
                    {
                        nextGroupId.ToString(CultureInfo.InvariantCulture),
                        cluster.Relationship.ToString(CultureInfo.InvariantCulture),
                        cluster.Confidence, role,
                        m.Id.ToString(CultureInfo.InvariantCulture),
                        Csv(m.FileName), Csv(m.Path),
                        folderCounts.GetValueOrDefault(m.FolderId) == 1 ? "true" : "false",
                        m.FileSize.ToString(CultureInfo.InvariantCulture),
                        m.PageCount.ToString(CultureInfo.InvariantCulture),
                        keeper != null && m.Id == keeper.Id ? "true" : "false",
                        m.HasUserState ? "true" : "false",
                        Csv(cluster.Evidence),
                    }));
                }
                nextGroupId++;
                groups++;
            }

            var nextCursor = pageLast;
            // The cursor is persisted only under --apply (with the groups, in one commit); a dry run hands it
            // back and the verb carries it — a dry run that advanced the store would make the next real run skip.
            if (apply) { await WriteCursorAsync(db, nextCursor, ct); await db.SaveChangesAsync(ct); }

            var remaining = await db.Items.AsNoTracking().CountAsync(i => i.Id > nextCursor && !i.IsExcluded, ct);
            logger.LogInformation("dedup batch: processed {N}, groups {Groups}, duplicates {Dupes}, remaining {Remaining}",
                page.Count, groups, duplicates, remaining);
            return new DedupBatchResult(page.Count, remaining, nextCursor, groups, duplicates);
        }

        private static async Task<Dictionary<int, ItemSignature>> ReadSignaturesAsync(BooksDb db, List<int> ids, CancellationToken ct)
        {
            var result = new Dictionary<int, ItemSignature>();
            foreach (var chunk in Chunk(ids, 400))
                foreach (var s in await db.ItemSignatures.AsNoTracking().Where(s => chunk.Contains(s.ItemId)).ToListAsync(ct))
                    result[s.ItemId] = s;
            return result;
        }

        /// <summary>Every item id in the table sharing one of these rows' present signature values.</summary>
        private static async Task<HashSet<int>> PartnerIdsAsync(BooksDb db, IEnumerable<ItemSignature> rows, CancellationToken ct)
        {
            var list = rows.ToList();
            var partners = new HashSet<int>();
            var contents = list.Select(s => s.ContentFingerprint).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
            var pages = list.Select(s => s.PageSignature).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
            var covers = list.Where(s => s.CoverPHash != null).Select(s => s.CoverPHash!.Value).Distinct().ToList();
            foreach (var chunk in Chunk(contents, 400))
                partners.UnionWith(await db.ItemSignatures.AsNoTracking().Where(s => s.ContentFingerprint != null && chunk.Contains(s.ContentFingerprint)).Select(s => s.ItemId).ToListAsync(ct));
            foreach (var chunk in Chunk(pages, 400))
                partners.UnionWith(await db.ItemSignatures.AsNoTracking().Where(s => s.PageSignature != null && chunk.Contains(s.PageSignature)).Select(s => s.ItemId).ToListAsync(ct));
            foreach (var chunk in Chunk(covers, 400))
                partners.UnionWith(await db.ItemSignatures.AsNoTracking().Where(s => s.CoverPHash != null && chunk.Contains(s.CoverPHash.Value)).Select(s => s.ItemId).ToListAsync(ct));
            return partners;
        }

        private static IEnumerable<List<T>> Chunk<T>(List<T> items, int size)
        {
            for (var i = 0; i < items.Count; i += size) yield return items.GetRange(i, Math.Min(size, items.Count - i));
        }

        public const string CsvHeader =
            "group_id,relationship,confidence,role,item_id,file_name,file_path,sole_file_in_folder,file_size,page_count,suggested_keeper,has_user_state,evidence";

        public sealed record Cluster(int Relationship, string Confidence, string Evidence, List<Candidate> Members);

        /// <summary>
        /// The pure grouping. A signature groups only when it is PRESENT — a null fingerprint is not a match
        /// with every other null, which is the classic way a dedup pass invents thousands of false groups.
        /// </summary>
        public static List<Cluster> BuildClusters(IReadOnlyList<Candidate> candidates)
        {
            var clusters = new List<Cluster>();
            var claimed = new HashSet<int>();

            void Group(Func<Candidate, string?> key, int relationship, string confidence, string evidence)
            {
                foreach (var g in candidates.Where(c => !claimed.Contains(c.Id))
                             .GroupBy(key, StringComparer.Ordinal)
                             .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1))
                {
                    var members = g.ToList();
                    foreach (var m in members) claimed.Add(m.Id);
                    clusters.Add(new Cluster(relationship, confidence, evidence + " " + g.Key, members));
                }
            }

            Group(c => c.ContentFingerprint, IdenticalFile, "High", "identical content fingerprint");
            Group(c => c.PageSignature, IdenticalContents, "High", "identical page signature");
            Group(c => c.CoverPHash?.ToString(CultureInfo.InvariantCulture), SameComicDifferentScan, "Medium", "identical cover hash");
            return clusters;
        }

        /// <summary>
        /// The keeper. <b>Reader state wins first</b> — a copy someone has opened, marked or rated must not be
        /// the one that disappears. After that the folder tells the story: a canonical series folder beats an
        /// event/chronology tree beats an unsorted holding folder; then depth, cover area, a ComicVine match,
        /// file size, and the id as the stable tiebreaker.
        /// </summary>
        public static Candidate? PickKeeper(List<Candidate> members, int relationship)
        {
            if (relationship == ContainedIn) return null;   // flag-only: keeping both is legitimate
            if (relationship == SameComicDifferentScan)
                return members
                    .OrderByDescending(m => m.HasUserState)
                    .ThenByDescending(m => m.CoverArea)
                    .ThenByDescending(m => m.FileSize)
                    .ThenByDescending(m => m.HasCv)
                    .ThenByDescending(m => !LooksUnsorted(m.Path))
                    .ThenBy(m => m.Id)
                    .First();
            return members
                .OrderByDescending(m => m.HasUserState)
                .ThenByDescending(m => !LooksLikeEventTree(m.Path))
                .ThenByDescending(m => !LooksUnsorted(m.Path))
                .ThenByDescending(m => FolderDepth(m.Path))
                .ThenByDescending(m => m.CoverArea)
                .ThenByDescending(m => m.HasCv)
                .ThenByDescending(m => m.FileSize)
                .ThenBy(m => m.Id)
                .First();
        }

        public static bool LooksUnsorted(string path) =>
            string.IsNullOrEmpty(path) || UnsortedMarkers.Any(path.ToLowerInvariant().Contains);

        public static bool LooksLikeEventTree(string path) =>
            !string.IsNullOrEmpty(path) && EventTreeMarkers.Any(path.ToLowerInvariant().Contains);

        public static int FolderDepth(string path) => string.IsNullOrEmpty(path) ? 0 : path.Count(c => c is '\\' or '/');

        /// <summary>
        /// Resolve a group: its Duplicate-role members become `IsExcluded` (HIDDEN, never deleted, and still
        /// listed by the Directory drill), and the group flips to Resolved. The keeper is never touched, and no
        /// file on the share is touched at all.
        /// </summary>
        public async Task<int> ResolveAsync(BooksDb db, int groupId, int? keeperItemId = null, CancellationToken ct = default)
        {
            var group = await db.DuplicateGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct)
                ?? throw new InvalidOperationException($"Duplicate group {groupId} not found.");
            if (group.Relationship == ContainedIn)
                throw new InvalidOperationException("A containment group is flag-only; owning both editions is legitimate.");

            var members = await db.DuplicateMembers.Where(m => m.DuplicateGroupId == groupId).ToListAsync(ct);
            var keeper = keeperItemId ?? group.SuggestedKeeperItemId
                ?? throw new InvalidOperationException("No keeper suggested and none supplied.");
            if (members.All(m => m.ItemId != keeper)) throw new InvalidOperationException($"Item {keeper} is not in group {groupId}.");

            var hidden = 0;
            foreach (var m in members)
            {
                m.Role = m.ItemId == keeper ? "Keeper" : "Duplicate";
                if (m.ItemId == keeper) continue;
                var item = await db.Items.FirstOrDefaultAsync(i => i.Id == m.ItemId, ct);
                if (item == null || item.IsExcluded) continue;
                item.IsExcluded = true;
                item.KeepInDirectory = true;   // the file is still there; the Directory drill still lists it
                var state = await db.ItemStates.FirstOrDefaultAsync(s => s.ItemId == m.ItemId, ct);
                if (state == null) { state = new ItemState { ItemId = m.ItemId }; db.ItemStates.Add(state); }
                state.ExclusionReason = $"duplicate of {keeper} (group {groupId})";
                state.ExcludedAt = DateTime.UtcNow;
                hidden++;
            }
            group.ReviewState = "Resolved";
            group.SuggestedKeeperItemId = keeper;
            await db.SaveChangesAsync(ct);
            return hidden;
        }

        private static string Csv(string? s) =>
            s == null ? "" : s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        private static async Task<int> ReadCursorAsync(BooksDb db, CancellationToken ct)
        {
            var row = await db.SystemStates.AsNoTracking().FirstOrDefaultAsync(s => s.Key == CursorKey, ct);
            return int.TryParse(row?.Value, out var v) ? v : 0;
        }

        private static async Task WriteCursorAsync(BooksDb db, int value, CancellationToken ct)
        {
            var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == CursorKey, ct);
            var text = value.ToString(CultureInfo.InvariantCulture);
            if (row == null) db.SystemStates.Add(new SystemState { Key = CursorKey, Value = text });
            else row.Value = text;
        }
    }
}
