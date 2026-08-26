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
    /// <para>Chunked by `Item.Id`: each batch reads a page of signatures and groups WITHIN what it has seen so
    /// far, so a killed run leaves the groups it already wrote intact.</para>
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
        /// operator reviews outside the app.
        /// </summary>
        public async Task<DedupBatchResult> RunBatchAsync(
            BooksDb db, int batchSize, bool apply = true, TextWriter? csv = null, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 50_000);
            var cursor = await ReadCursorAsync(db, ct);

            var page = await db.Items.AsNoTracking()
                .Where(i => i.Id > cursor)
                .OrderBy(i => i.Id).Take(batchSize)
                .Select(i => new { i.Id, i.Path, i.FileName, i.FileSize, i.PageCount, i.FolderId })
                .ToListAsync(ct);
            if (page.Count == 0) return new DedupBatchResult(0, 0, null, 0, 0);

            var ids = page.Select(p => p.Id).ToList();
            var signatures = await db.ItemSignatures.AsNoTracking().Where(s => ids.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, ct);
            var states = await db.ItemStates.AsNoTracking().Where(s => ids.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, ct);
            var cvLinked = (await db.ItemProviderLinks.AsNoTracking()
                .Where(l => ids.Contains(l.ItemId) && l.Provider == Provider.Cv && l.Status == LinkStatus.Matched)
                .Select(l => l.ItemId).ToListAsync(ct)).ToHashSet();
            var userState = (await db.UserItemStates.AsNoTracking()
                .Where(s => ids.Contains(s.ItemId))
                .Select(s => s.ItemId).ToListAsync(ct)).ToHashSet();

            var candidates = page.Select(p =>
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

            var nextCursor = page[^1].Id;
            await WriteCursorAsync(db, nextCursor, ct);
            if (apply) await db.SaveChangesAsync(ct);

            var remaining = await db.Items.AsNoTracking().CountAsync(i => i.Id > nextCursor, ct);
            logger.LogInformation("dedup batch: processed {N}, groups {Groups}, duplicates {Dupes}, remaining {Remaining}",
                page.Count, groups, duplicates, remaining);
            return new DedupBatchResult(page.Count, remaining, nextCursor, groups, duplicates);
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
