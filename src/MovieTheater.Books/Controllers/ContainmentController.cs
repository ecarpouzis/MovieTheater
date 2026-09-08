using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    /// <summary>
    /// The containment review surface: the questions the model pass could not answer alone, and the one edit
    /// that answers them.
    ///
    /// <para>The pass judged 20,498 collected editions and refused 17,421 of them. Most refusals need nobody —
    /// a manga volume has no issues to contain. But 718 are real questions about a real shelf: three relaunch
    /// ladders each numbered from #1, a 24-page single issue wearing a <c>tpb</c> format, two rips of one
    /// volume, a provider row whose title names a different book. Those went into a CSV, and a CSV is where
    /// review work goes to die — so they live in <c>ContainmentFlag</c> and get worked through here.</para>
    ///
    /// <para><b>Reviewing a flag can fix the span, not just file the question.</b> <c>PUT spans/{itemId}</c>
    /// writes a <c>Curated</c> span attributed to the person who typed it, at confidence 1.0 — a human reading
    /// the book outranks anything the pass or a provider inferred, and the de-duplication's trust classes treat
    /// it as gold. That is the whole point of the screen: the operator who can see the shelf is the one who
    /// should get to say what the book collects.</para>
    /// </summary>
    [ApiController]
    [Route("admin/containment")]
    [Authorize(Policy = "admin")]
    public sealed class ContainmentController : ControllerBase
    {
        private readonly BooksDb db;
        private readonly BooksOptions options;
        private readonly JobRunner jobs;

        public ContainmentController(BooksDb db, BooksOptions options, JobRunner jobs)
        {
            this.db = db;
            this.options = options;
            this.jobs = jobs;
        }

        private string? Who => BooksIdentity.Username(User);

        /// <summary>A span a person typed. Not <c>model:</c>, so the importer treats it as gold and never overwrites it.</summary>
        public const string AdminProviderRef = "admin";

        /// <summary>GET /admin/containment/summary — the shape of the queue: how many of each flag, in each state.</summary>
        [HttpGet("summary")]
        public async Task<IActionResult> Summary(CancellationToken ct)
        {
            var byFlag = await db.ContainmentFlags.AsNoTracking()
                .GroupBy(f => new { f.Flag, f.ReviewState })
                .Select(g => new { g.Key.Flag, g.Key.ReviewState, n = g.Count() })
                .ToListAsync(ct);

            var spans = await db.CollectedEditionSpans.AsNoTracking()
                .Where(s => s.Source == EditionSource.Curated)
                .GroupBy(s => s.ProviderRef == null || !s.ProviderRef.StartsWith("model:"))
                .Select(g => new { gold = g.Key, n = g.Count() })
                .ToListAsync(ct);

            return Ok(new
            {
                flags = byFlag
                    .GroupBy(x => x.Flag ?? "")
                    .Select(g => new
                    {
                        flag = g.Key,
                        total = g.Sum(x => x.n),
                        pending = g.Where(x => x.ReviewState == ContainmentFlagImport.Pending).Sum(x => x.n),
                        accepted = g.Where(x => x.ReviewState == ContainmentFlagImport.Accepted).Sum(x => x.n),
                        dismissed = g.Where(x => x.ReviewState == ContainmentFlagImport.Dismissed).Sum(x => x.n),
                    })
                    .OrderByDescending(x => x.pending).ThenBy(x => x.flag),
                curatedSpans = new
                {
                    gold = spans.Where(s => s.gold).Sum(s => s.n),
                    model = spans.Where(s => !s.gold).Sum(s => s.n),
                },
                containedGroups = await db.DuplicateGroups.AsNoTracking()
                    .CountAsync(g => g.Relationship == DuplicateRelationship.ContainedIn, ct),
            });
        }

        /// <summary>
        /// GET /admin/containment/flags — one page of the queue, newest question first within a flag.
        /// Each row carries what the reviewer needs without a second call: the file, the series, the span the
        /// item currently has, and the other collected editions of the same series so the ladder is visible.
        /// </summary>
        [HttpGet("flags")]
        public async Task<IActionResult> Flags(
            [FromQuery] string state = ContainmentFlagImport.Pending,
            [FromQuery] string? flag = null,
            [FromQuery] int? seriesId = null,
            [FromQuery] int skip = 0,
            [FromQuery] int top = 50,
            CancellationToken ct = default)
        {
            top = Math.Clamp(top, 1, 200);
            var q = db.ContainmentFlags.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(state) && state != "All") q = q.Where(f => f.ReviewState == state);
            if (!string.IsNullOrWhiteSpace(flag)) q = q.Where(f => f.Flag == flag);
            if (seriesId is { } sid) q = q.Where(f => f.SeriesId == sid);
            q = q.OrderBy(f => f.Flag).ThenBy(f => f.Id);

            var total = await q.CountAsync(ct);
            var page = await q.Skip(skip).Take(top).ToListAsync(ct);
            var itemIds = page.Select(f => f.ItemId).ToList();
            var seriesIds = page.Where(f => f.SeriesId != null).Select(f => f.SeriesId!.Value).Distinct().ToList();

            var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
                .Select(i => new { i.Id, i.FileName, i.Path, i.PageCount, i.SeriesId, i.IsExcluded })
                .ToListAsync(ct);
            var series = await db.Series.AsNoTracking().Where(s => seriesIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name, s.CanonicalKey }).ToListAsync(ct);
            var spans = await db.CollectedEditionSpans.AsNoTracking().Where(s => itemIds.Contains(s.ItemId))
                .Select(s => new { s.ItemId, s.Source, s.IssueStart, s.IssueEnd, s.EditionTitle, s.Confidence, s.ProviderRef, s.Note })
                .ToListAsync(ct);

            // The rest of the shelf: a flag about one volume is almost always a question about the ladder.
            var siblings = await (from n in db.CollectionNodes.AsNoTracking()
                                  where n.SeriesId != null && seriesIds.Contains(n.SeriesId.Value)
                                  join i in db.Items.AsNoTracking() on n.ItemId equals i.Id
                                  orderby n.SeriesId, n.ItemId
                                  select new { n.SeriesId, n.ItemId, i.FileName, i.PageCount, n.SpanLabel, n.SpanSource, n.ContainsCount, n.TrackRole })
                                 .Take(2000).ToListAsync(ct);

            return Ok(new
            {
                totalCount = total,
                skip,
                top,
                items = page.Select(f =>
                {
                    var item = items.FirstOrDefault(i => i.Id == f.ItemId);
                    return new
                    {
                        f.Id,
                        f.ItemId,
                        f.SeriesId,
                        f.Flag,
                        f.Detail,
                        f.Source,
                        f.ReviewState,
                        f.Note,
                        f.DecidedBy,
                        f.DecidedAt,
                        fileName = item?.FileName,
                        path = item?.Path,
                        pageCount = item?.PageCount,
                        isExcluded = item?.IsExcluded,
                        series = series.FirstOrDefault(s => s.Id == f.SeriesId)?.Name,
                        spans = spans.Where(s => s.ItemId == f.ItemId),
                        shelf = siblings.Where(s => s.SeriesId == f.SeriesId).Take(60),
                    };
                }),
            });
        }

        /// <summary>
        /// GET /admin/containment/overlaps — the `ContainedIn` groups on their own, because in the Duplicates
        /// tab they sit among thousands of signature groups and would never be found. Each one says: this
        /// collected edition holds these single issues, which you also own as separate files.
        /// </summary>
        [HttpGet("overlaps")]
        public async Task<IActionResult> Overlaps([FromQuery] int skip = 0, [FromQuery] int top = 25, CancellationToken ct = default)
        {
            top = Math.Clamp(top, 1, 100);
            var q = db.DuplicateGroups.AsNoTracking()
                .Where(g => g.Relationship == DuplicateRelationship.ContainedIn)
                .OrderByDescending(g => g.Confidence).ThenBy(g => g.Id);
            var total = await q.CountAsync(ct);
            var groups = await q.Skip(skip).Take(top).ToListAsync(ct);
            var ids = groups.Select(g => g.Id).ToList();

            var members = await (from m in db.DuplicateMembers.AsNoTracking()
                                 where ids.Contains(m.DuplicateGroupId)
                                 join i in db.Items.AsNoTracking() on m.ItemId equals i.Id
                                 orderby m.Role descending, i.FileName
                                 select new { m.DuplicateGroupId, m.ItemId, m.Role, i.FileName, i.Path, i.PageCount })
                                .ToListAsync(ct);

            return Ok(new
            {
                totalCount = total,
                skip,
                top,
                groups = groups.Select(g => new
                {
                    g.Id, g.Confidence, g.Evidence, g.ReviewState, g.DetectedAt,
                    members = members.Where(m => m.DuplicateGroupId == g.Id),
                }),
            });
        }

        public sealed record DecideBody(string State, string? Note);

        /// <summary>
        /// POST /admin/containment/flags/{id}/decide — record the verdict. `Accepted` means the flag was right
        /// and the item stays untrusted; `Dismissed` means it was a false alarm and the de-duplication may use
        /// the item's span again.
        /// </summary>
        [HttpPost("flags/{id:int}/decide")]
        public async Task<IActionResult> Decide(int id, [FromBody] DecideBody body, CancellationToken ct)
        {
            var state = body.State?.Trim();
            if (state is not (ContainmentFlagImport.Pending or ContainmentFlagImport.Accepted or ContainmentFlagImport.Dismissed))
                return BadRequest(new { error = $"state must be {ContainmentFlagImport.Pending}, {ContainmentFlagImport.Accepted} or {ContainmentFlagImport.Dismissed}" });

            var flag = await db.ContainmentFlags.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (flag == null) return NotFound(new { error = $"flag {id} not found" });

            flag.ReviewState = state;
            flag.Note = string.IsNullOrWhiteSpace(body.Note) ? flag.Note : body.Note.Trim();
            flag.DecidedBy = Who;
            flag.DecidedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Ok(new { flag.Id, flag.ReviewState, flag.DecidedBy, flag.DecidedAt, rebuildRequired = "dedup-contained" });
        }

        public sealed record SpanBody(double? Start, double? End, string? Title, string? Note);

        /// <summary>
        /// PUT /admin/containment/spans/{itemId} — say what this edition actually collects. Written as a
        /// `Curated` span at confidence 1.0, attributed to the person, and NOT prefixed `model:` — so a later
        /// re-import of the pass will keep it and flag any disagreement rather than overwrite it.
        /// </summary>
        [HttpPut("spans/{itemId:int}")]
        public async Task<IActionResult> SetSpan(int itemId, [FromBody] SpanBody body, CancellationToken ct)
        {
            if (body.Start is not { } start || body.End is not { } end)
                return BadRequest(new { error = "start and end are both required" });
            if (end < start) return BadRequest(new { error = $"#{start}-{end} is not ascending" });

            var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
            if (item == null) return NotFound(new { error = $"item {itemId} not found" });

            var row = await db.CollectedEditionSpans
                .FirstOrDefaultAsync(s => s.ItemId == itemId && s.Source == EditionSource.Curated, ct);
            if (row == null)
            {
                row = new CollectedEditionSpan { ItemId = itemId, Source = EditionSource.Curated };
                db.CollectedEditionSpans.Add(row);
            }
            row.SeriesId = item.SeriesId;
            row.IssueStart = start;
            row.IssueEnd = end;
            row.EditionTitle = string.IsNullOrWhiteSpace(body.Title) ? row.EditionTitle : body.Title.Trim();
            row.ProviderRef = Who is { Length: > 0 } who ? $"{AdminProviderRef}:{who}" : AdminProviderRef;
            row.Contiguous = true;
            row.Confidence = 1.0;
            row.Note = string.IsNullOrWhiteSpace(body.Note) ? "set by hand in the containment review" : body.Note.Trim();
            row.CreatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return Ok(new { itemId, start, end, source = "Curated", by = row.ProviderRef, rebuildRequired = "containment" });
        }

        /// <summary>
        /// POST /admin/containment/dedup/start — re-derive the `ContainedIn` overlap groups from the current
        /// containment. Run it after editing spans and rebuilding containment; `reset` (the default) clears the
        /// previous derivation so a corrected span cannot leave a stale group behind.
        /// </summary>
        [HttpPost("dedup/start")]
        public async Task<IActionResult> StartContainedDedup([FromQuery] bool reset = true, [FromQuery] int batchSize = 500)
        {
            var dbPath = options.DbPath;
            if (dbPath == null) return BadRequest(new { error = "No catalog is configured on this host." });
            var size = Math.Clamp(batchSize, 50, 5000);

            if (reset)
            {
                using var hot = new TargetWriter(dbPath, MappingContract.Load(), dryRun: false);
                hot.Begin();
                ContainedDuplicateJob.Reset(hot);
                hot.Commit();
            }

            try
            {
                var status = await jobs.StartAsync(DedupJobKind, (_, _) => Task.Run(() => PageContainedDedup(dbPath, size)));
                return Accepted(new { job = status, statusUrl = $"/admin/jobs/status?kind={DedupJobKind}" });
            }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        }

        public const string DedupJobKind = "dedup-contained";

        /// <summary>One bounded page of the containment de-duplication, cursor persisted in its own batch.</summary>
        private static JobProgress PageContainedDedup(string dbPath, int batchSize)
        {
            using var hot = new TargetWriter(dbPath, MappingContract.Load(), dryRun: false);
            var cursor = JobCursor.Read(hot, ContainedDuplicateJob.CursorKey);

            hot.Begin();
            var r = ContainedDuplicateJob.RunBatch(hot, cursor, batchSize, ContainedDuplicateJob.MinConfidence);
            if (r.NextCursor is long next) JobCursor.Write(hot, ContainedDuplicateJob.CursorKey, next);
            else JobCursor.Clear(hot, ContainedDuplicateJob.CursorKey);
            hot.Commit();

            return new JobProgress(r.Series, r.Remaining,
                r.NextCursor?.ToString(CultureInfo.InvariantCulture), 0,
                $"{r.Groups} overlap groups, {r.Members} members, {r.Skipped} containers skipped");
        }

        /// <summary>DELETE /admin/containment/spans/{itemId} — withdraw the Curated span; the book contains nothing known.</summary>
        [HttpDelete("spans/{itemId:int}")]
        public async Task<IActionResult> ClearSpan(int itemId, CancellationToken ct)
        {
            var row = await db.CollectedEditionSpans
                .FirstOrDefaultAsync(s => s.ItemId == itemId && s.Source == EditionSource.Curated, ct);
            if (row == null) return NotFound(new { error = $"item {itemId} carries no Curated span" });
            db.CollectedEditionSpans.Remove(row);
            await db.SaveChangesAsync(ct);
            return Ok(new { itemId, cleared = true, rebuildRequired = "containment" });
        }
    }
}
