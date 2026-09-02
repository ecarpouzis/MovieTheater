using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Services
{
    /// <summary>
    /// Series reconciliation: the operator surface for "this run is linked to the wrong ComicVine volume", "these
    /// two spellings are one series", "that folder's issues scattered into six phantom series".
    ///
    /// <para><b>Every operation here is an edit to an INPUT.</b> The reconciliation golden rule (v2-model §4) is
    /// structural in v2: `Series`, `SeriesAlias` and `Item.SeriesId` are DERIVED and are rebuilt by
    /// <c>books-resolve --series</c>. So clearing a link writes `SeriesKeyLink`; renaming a series writes
    /// `Series.DisplayNameOverride`; folding a spelling writes `ComicDetail.ParsedSeriesKey`. Nothing here
    /// touches a derived row, and every method returns <c>RebuildRequired</c> so the caller knows to re-run
    /// the identity job (the admin endpoints chain it; the CLI verbs print it).</para>
    ///
    /// <para>Every decision is recorded in `SeriesInferenceDecision` with an undo payload, and every triage
    /// state in `SeriesMatchReview` — the standalone's own audit trail, kept because a reconciliation you
    /// cannot reverse is one nobody dares to run.</para>
    /// </summary>
    public sealed class SeriesMismatchService
    {
        private readonly ILogger<SeriesMismatchService> logger;
        public SeriesMismatchService(ILogger<SeriesMismatchService> logger) => this.logger = logger;

        /// <summary>The answer shape every mutating operation returns: what changed and what must be re-derived.</summary>
        public sealed record EditResult(string Action, string Target, int RowsChanged, bool RebuildRequired = true)
        {
            public override string ToString() => $"{{ action: \"{Action}\", target: \"{Target}\", rowsChanged: {RowsChanged}, rebuildRequired: {RebuildRequired} }}";
        }

        public sealed record MismatchSummary(int Series, int LinkedSeries, int UnlinkedSeries, int PendingLinks, int MultipleLinks, int OpenReviews, int SingleIssueSeries);

        /// <summary>The counters the admin panel's header shows. One pass over the small link tables.</summary>
        public async Task<MismatchSummary> SummaryAsync(BooksDb db, CancellationToken ct = default) => new(
            await db.Series.CountAsync(ct),
            await db.Series.CountAsync(s => s.CvVolumeId != null || s.ExternalWorkId != null, ct),
            await db.Series.CountAsync(s => s.CvVolumeId == null && s.ExternalWorkId == null, ct),
            await db.SeriesKeyLinks.CountAsync(l => l.Status == LinkStatus.Pending, ct),
            await db.SeriesKeyLinks.CountAsync(l => l.Status == LinkStatus.Multiple, ct),
            await db.SeriesMatchReviews.CountAsync(r => r.State == null || r.State == "Open", ct),
            await db.Series.CountAsync(s => s.IssueCount == 1, ct));

        /// <summary>
        /// The parsed spellings that resolve into one series, with how many items each carries — the view an
        /// operator uses to spot a spelling that should have been folded and one that should not have been.
        /// </summary>
        public async Task<List<object>> AliasesAsync(BooksDb db, int seriesId, CancellationToken ct = default)
        {
            var aliases = await db.SeriesAliases.AsNoTracking().Where(a => a.SeriesId == seriesId).Select(a => a.ParsedKey).ToListAsync(ct);
            var counts = await db.ComicDetails.AsNoTracking()
                .Where(d => d.ParsedSeriesKey != null && aliases.Contains(d.ParsedSeriesKey))
                .GroupBy(d => d.ParsedSeriesKey!)
                .Select(g => new { Key = g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.N, ct);
            return aliases.Select(a => (object)new { parsedKey = a, items = counts.GetValueOrDefault(a) }).ToList();
        }

        /// <summary>
        /// The stored candidates for one parsed key's provider link — what the scraper saw and scored — plus the
        /// stored top score the stale-match heuristic compares against.
        /// </summary>
        public async Task<object?> LinkCandidatesAsync(BooksDb db, string parsedKey, Provider provider, CancellationToken ct = default,
            Providers.ProviderCacheStore? store = null)
        {
            var link = await db.SeriesKeyLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.ParsedKey == parsedKey && l.Provider == provider, ct);
            if (link == null) return null;
            // The candidates live in the legs file's LinkCandidates — the migration put the settled ones there
            // and a live scrape puts an open decision's there too — so they are read back when the store is at hand.
            System.Text.Json.JsonElement? candidates = null;
            var json = store?.GetLinkCandidates(SubjectKind.Series, parsedKey, provider);
            if (json != null)
            {
                try { candidates = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(); }
                catch (System.Text.Json.JsonException) { }
            }
            return new
            {
                parsedKey,
                provider = provider.ToString(),
                status = link.Status.ToString(),
                providerKey = link.ProviderKey,
                score = link.Score,
                storedTopScore = link.StoredTopScore,
                candidatesInLegs = json != null,
                candidates,
                attemptCount = link.AttemptCount,
                attemptedAt = link.AttemptedAt,
                error = link.Error,
            };
        }

        /// <summary>
        /// Clear a wrong provider link. The link row survives as `Cleared` — deleting it would let the next
        /// scrape re-make the same wrong match, which is the failure this status exists to prevent.
        /// </summary>
        public async Task<EditResult> ClearLinkAsync(BooksDb db, string parsedKey, Provider provider, string? decidedBy, CancellationToken ct = default)
        {
            var link = await db.SeriesKeyLinks.FirstOrDefaultAsync(l => l.ParsedKey == parsedKey && l.Provider == provider, ct);
            if (link == null) return new EditResult("clear-link", parsedKey, 0, false);

            var undo = $"{{\"providerKey\":{link.ProviderKey?.ToString() ?? "null"},\"status\":\"{link.Status}\",\"score\":{link.Score?.ToString() ?? "null"}}}";
            link.ProviderKey = null;
            link.Status = LinkStatus.Cleared;
            link.Score = null;
            link.AttemptedAt = DateTime.UtcNow;
            await RecordAsync(db, parsedKey, "Link", "clear-link", provider.ToString(), undo, decidedBy, ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("series link cleared: {Key} ({Provider})", parsedKey, provider);
            return new EditResult("clear-link", parsedKey, 1);
        }

        /// <summary>Point a parsed key at a different provider id by hand. `Manual` never gets re-scraped over.</summary>
        public async Task<EditResult> SetLinkAsync(BooksDb db, string parsedKey, Provider provider, int providerKey, string? decidedBy, CancellationToken ct = default)
        {
            var link = await db.SeriesKeyLinks.FirstOrDefaultAsync(l => l.ParsedKey == parsedKey && l.Provider == provider, ct);
            if (link == null) { link = new SeriesKeyLink { ParsedKey = parsedKey, Provider = provider }; db.SeriesKeyLinks.Add(link); }

            var undo = $"{{\"providerKey\":{link.ProviderKey?.ToString() ?? "null"},\"status\":\"{link.Status}\"}}";
            link.ProviderKey = providerKey;
            link.Status = LinkStatus.Manual;
            link.Score = 100;
            link.Error = null;
            link.AttemptedAt = DateTime.UtcNow;
            await RecordAsync(db, parsedKey, "Link", "set-link", $"{provider}:{providerKey}", undo, decidedBy, ct);
            await db.SaveChangesAsync(ct);
            return new EditResult("set-link", parsedKey, 1);
        }

        /// <summary>
        /// Fold one parsed spelling into another by rewriting `ComicDetail.ParsedSeriesKey` — the resolution
        /// INPUT. The identity rebuild then merges the two series and carries their marks across; nothing here
        /// touches `Series` or `SeriesAlias` directly.
        /// </summary>
        public async Task<EditResult> FoldParsedKeyAsync(BooksDb db, string fromKey, string toKey, string? decidedBy, CancellationToken ct = default)
        {
            if (string.Equals(fromKey, toKey, StringComparison.Ordinal))
                throw new ArgumentException("A parsed key cannot fold into itself.");
            var affected = await db.ComicDetails.Where(d => d.ParsedSeriesKey == fromKey)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParsedSeriesKey, toKey), ct);
            if (affected == 0) return new EditResult("fold", fromKey, 0, false);

            await RecordAsync(db, fromKey, "Consolidation", "fold", toKey, $"{{\"parsedSeriesKey\":\"{Escape(fromKey)}\",\"items\":{affected}}}", decidedBy, ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("folded parsed key '{From}' into '{To}' across {N} items", fromKey, toKey, affected);
            return new EditResult("fold", fromKey, affected);
        }

        /// <summary>
        /// Unify a folder: every item in it gets the SAME parsed key. This is the de-shatter an operator reaches
        /// for when one physical folder produced six phantom one-issue series.
        /// </summary>
        public async Task<EditResult> UnifyFolderAsync(BooksDb db, int folderId, string parsedKey, string? decidedBy, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(parsedKey)) throw new ArgumentException("A target parsed key is required.");
            var itemIds = await db.Items.AsNoTracking().Where(i => i.FolderId == folderId && i.Kind == ItemKind.Comic).Select(i => i.Id).ToListAsync(ct);
            if (itemIds.Count == 0) return new EditResult("unify-folder", folderId.ToString(), 0, false);

            var before = await db.ComicDetails.AsNoTracking().Where(d => itemIds.Contains(d.ItemId))
                .Select(d => new { d.ItemId, d.ParsedSeriesKey }).ToListAsync(ct);
            var undo = "[" + string.Join(",", before.Select(b => $"{{\"itemId\":{b.ItemId},\"parsedSeriesKey\":\"{Escape(b.ParsedSeriesKey ?? "")}\"}}")) + "]";

            var affected = await db.ComicDetails.Where(d => itemIds.Contains(d.ItemId))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParsedSeriesKey, parsedKey.Trim()), ct);
            await RecordAsync(db, parsedKey.Trim(), "Consolidation", "unify-folder", folderId.ToString(), undo, decidedBy, ct);
            await db.SaveChangesAsync(ct);
            return new EditResult("unify-folder", folderId.ToString(), affected);
        }

        /// <summary>Mark a triage item reviewed (or reopen it). `SeriesMatchReview` is keyed by scope + key.</summary>
        public async Task<EditResult> MarkReviewedAsync(BooksDb db, string scope, string key, string state, string? note, string? decidedBy, CancellationToken ct = default)
        {
            var review = await db.SeriesMatchReviews.FirstOrDefaultAsync(r => r.Scope == scope && r.Key == key, ct);
            if (review == null)
            {
                var nextId = (await db.SeriesMatchReviews.AsNoTracking().Select(r => (int?)r.Id).MaxAsync(ct) ?? 0) + 1;
                review = new SeriesMatchReview { Id = nextId, Scope = scope, Key = key };
                db.SeriesMatchReviews.Add(review);
            }
            review.State = state;
            review.Note = note;
            review.DecidedBy = decidedBy;
            review.DecidedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            // A review state is triage, not data: nothing downstream is derived from it.
            return new EditResult("mark-reviewed", $"{scope}:{key}", 1, RebuildRequired: false);
        }

        public async Task<List<SeriesInferenceDecision>> DecisionsAsync(BooksDb db, string? state, int skip, int top, CancellationToken ct = default)
        {
            var q = db.SeriesInferenceDecisions.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(state)) q = q.Where(d => d.State == state);
            return await q.OrderByDescending(d => d.Id).Skip(skip).Take(Math.Clamp(top, 1, 200)).ToListAsync(ct);
        }

        /// <summary>
        /// Reverse one decision from its stored undo payload. Only the fold/unify actions are reversible this
        /// way; a cleared link is reversed by setting it again, which is its own audited decision.
        /// </summary>
        public async Task<EditResult> RevertDecisionAsync(BooksDb db, int decisionId, string? decidedBy, CancellationToken ct = default)
        {
            var decision = await db.SeriesInferenceDecisions.FirstOrDefaultAsync(d => d.Id == decisionId, ct)
                ?? throw new InvalidOperationException($"Decision {decisionId} not found.");
            if (decision.State == "Reverted") return new EditResult("revert", decisionId.ToString(), 0, false);
            if (string.IsNullOrWhiteSpace(decision.UndoJson)) throw new InvalidOperationException("That decision carries no undo payload.");

            var changed = 0;
            using var doc = System.Text.Json.JsonDocument.Parse(decision.UndoJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (!e.TryGetProperty("itemId", out var idEl) || !idEl.TryGetInt32(out var itemId)) continue;
                    var key = e.TryGetProperty("parsedSeriesKey", out var k) ? k.GetString() : null;
                    changed += await db.ComicDetails.Where(d => d.ItemId == itemId)
                        .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParsedSeriesKey, key), ct);
                }
            else if (doc.RootElement.TryGetProperty("parsedSeriesKey", out var oldKey) && decision.Target != null)
                changed += await db.ComicDetails.Where(d => d.ParsedSeriesKey == decision.Target)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParsedSeriesKey, oldKey.GetString()), ct);

            decision.State = "Reverted";
            decision.DecidedBy = decidedBy;
            decision.DecidedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new EditResult("revert", decisionId.ToString(), changed);
        }

        private static async Task RecordAsync(BooksDb db, string seriesKey, string @class, string action, string? target, string? undo, string? decidedBy, CancellationToken ct)
        {
            var nextId = (await db.SeriesInferenceDecisions.AsNoTracking().Select(d => (int?)d.Id).MaxAsync(ct) ?? 0) + 1;
            db.SeriesInferenceDecisions.Add(new SeriesInferenceDecision
            {
                Id = nextId,
                SeriesKey = seriesKey,
                Class = @class,
                Action = action,
                Target = target,
                Confidence = "Manual",
                State = "Applied",
                UndoJson = undo,
                DecidedBy = decidedBy,
                DecidedAt = DateTime.UtcNow,
            });
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// The series NAME surface: the display override, and the three bulk repairs the standalone shipped as
    /// verbs (`namefix`, `prune`, `split-overmatch`). Like every reconciliation operation, each writes an INPUT
    /// and asks for a rebuild.
    /// </summary>
    public sealed class SeriesNamesService
    {
        private readonly ILogger<SeriesNamesService> logger;
        public SeriesNamesService(ILogger<SeriesNamesService> logger) => this.logger = logger;

        /// <summary>
        /// Set (or clear, with a null) the hand-chosen display name. It is the TOP tier of the name chain, so it
        /// survives every re-scrape — which is the point of having it rather than editing `Series.Name`.
        /// </summary>
        public async Task<SeriesMismatchService.EditResult> SetOverrideAsync(BooksDb db, int seriesId, string? displayName, CancellationToken ct = default)
        {
            var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct)
                ?? throw new InvalidOperationException($"Series {seriesId} not found.");
            series.DisplayNameOverride = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            await db.SaveChangesAsync(ct);
            return new SeriesMismatchService.EditResult("set-override", seriesId.ToString(), 1);
        }

        /// <summary>
        /// Set (or clear, with a null) the hand-curated franchise — the Franchise facet's only producer besides
        /// <c>books-curation-import</c>. A curated dimension, never derived: no job rewrites it.
        /// </summary>
        public async Task<SeriesMismatchService.EditResult> SetFranchiseAsync(BooksDb db, int seriesId, string? franchise, CancellationToken ct = default)
        {
            var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct)
                ?? throw new InvalidOperationException($"Series {seriesId} not found.");
            series.Franchise = string.IsNullOrWhiteSpace(franchise) ? null : franchise.Trim();
            await db.SaveChangesAsync(ct);
            return new SeriesMismatchService.EditResult("set-franchise", seriesId.ToString(), 1);
        }

        /// <summary>
        /// <c>books-series-namefix</c> — the series whose NAME still carries parse noise a later rule would have
        /// stripped: a trailing volume marker, a leading sort index, a trailing bare number. Reported as
        /// PROPOSALS; nothing is applied without <paramref name="apply"/>, and applying writes the override.
        /// </summary>
        public async Task<List<NameFix>> NameFixAsync(BooksDb db, bool apply = false, CancellationToken ct = default)
        {
            var series = await db.Series.AsNoTracking()
                .Where(s => s.DisplayNameOverride == null && s.Name != null)
                .Select(s => new { s.Id, s.Name, s.IssueCount }).ToListAsync(ct);

            var fixes = new List<NameFix>();
            foreach (var s in series)
            {
                var proposed = Parse.ComicTitleParser.CleanTitle(s.Name!);
                if (string.Equals(proposed, s.Name, StringComparison.Ordinal) || proposed.Length == 0) continue;
                fixes.Add(new NameFix(s.Id, s.Name!, proposed, s.IssueCount));
            }
            if (!apply) return fixes;

            foreach (var f in fixes)
            {
                var row = await db.Series.FirstOrDefaultAsync(x => x.Id == f.SeriesId, ct);
                if (row != null) row.DisplayNameOverride = f.Proposed;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("namefix applied to {Count} series", fixes.Count);
            return fixes;
        }

        public sealed record NameFix(int SeriesId, string Current, string Proposed, int IssueCount);

        /// <summary>
        /// <c>books-series-prune</c> — series rows with NO items and NO marks. They are the residue of past
        /// re-points, and they clutter every series facet. Guarded: a series a reader has marked is never
        /// pruned, whatever its issue count, and the dry run is the default.
        /// </summary>
        public async Task<(int Candidates, int Deleted)> PruneAsync(BooksDb db, bool apply = false, CancellationToken ct = default)
        {
            var used = await db.Items.AsNoTracking().Where(i => i.SeriesId != null).Select(i => i.SeriesId!.Value).Distinct().ToListAsync(ct);
            var marked = (await db.GroupMarks.AsNoTracking().Where(m => m.GroupType == GroupType.Series).Select(m => m.GroupKey).ToListAsync(ct))
                .Select(k => int.TryParse(k, out var id) ? id : -1).Where(id => id > 0).ToHashSet();

            var candidates = await db.Series.AsNoTracking()
                .Where(s => !used.Contains(s.Id))
                .Select(s => s.Id).ToListAsync(ct);
            candidates = candidates.Where(id => !marked.Contains(id)).ToList();
            if (!apply || candidates.Count == 0) return (candidates.Count, 0);

            // Aliases and series-keyed leaf rows go with them; nothing else may reference an empty series.
            await db.SeriesAliases.Where(a => candidates.Contains(a.SeriesId)).ExecuteDeleteAsync(ct);
            await db.SeriesTags.Where(t => candidates.Contains(t.SeriesId)).ExecuteDeleteAsync(ct);
            await db.MuSeriesLinks.Where(l => candidates.Contains(l.SeriesId)).ExecuteDeleteAsync(ct);
            var deleted = await db.Series.Where(s => candidates.Contains(s.Id)).ExecuteDeleteAsync(ct);
            return (candidates.Count, deleted);
        }

        /// <summary>
        /// <c>books-series-split-overmatch</c> — a series whose issue count is wildly larger than its provider
        /// volume claims has swallowed other runs through an over-eager match. Reported with the evidence; the
        /// FIX is to clear the link and let the identity rebuild separate them, which is a decision an operator
        /// makes per row.
        /// </summary>
        public async Task<List<Overmatch>> SplitOvermatchAsync(BooksDb db, double ratio = 2.0, int minIssues = 20, CancellationToken ct = default)
        {
            var rows = await (from s in db.Series.AsNoTracking()
                              join v in db.CvVolumes.AsNoTracking() on s.CvVolumeId equals v.Id
                              where s.IssueCount >= minIssues && v.CountOfIssues != null && v.CountOfIssues > 0
                              select new { s.Id, s.Name, s.IssueCount, Claimed = v.CountOfIssues!.Value, VolumeId = v.Id })
                             .ToListAsync(ct);
            return rows.Where(r => r.IssueCount > r.Claimed * ratio)
                .Select(r => new Overmatch(r.Id, r.Name, r.IssueCount, r.Claimed, r.VolumeId))
                .OrderByDescending(r => r.Held - r.Claimed)
                .ToList();
        }

        public sealed record Overmatch(int SeriesId, string? Name, int Held, int Claimed, int CvVolumeId);
    }
}
