using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Providers;
using MovieTheater.Books.Resolve;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Controllers
{
    /// <summary>
    /// The operator's surface: scans, jobs, roots, the derived-table registry, logs, reconciliation, dedup and
    /// the provider scrapes.
    ///
    /// <para><b>Every long operation goes through <see cref="JobRunner"/>.</b> A start endpoint runs ONE batch
    /// inline (so the answer carries real numbers, not a promise), hands the rest to a background loop, and
    /// returns a status URL. No endpoint in this file loops to completion — that is the rule the whole slice is
    /// built on, and it is why a scan of 141k files can be started from a browser at all.</para>
    ///
    /// <para><b>Derived data changes only through a registered job.</b> The recompute endpoints below start the
    /// job that owns each table and stamp `DerivedTable`; there is no endpoint that edits a derived row. The
    /// reconciliation endpoints edit INPUTS and say <c>rebuildRequired</c> so the caller knows what to re-run.</para>
    ///
    /// <para><b>Two deliberate removals from the standalone.</b> User administration is gone (the site owns
    /// users), and so is the ComicVine key vault — the key is plain configuration
    /// (<c>Books:ComicVineApiKey</c>, or the settings overlay), because a per-user DPAPI ring for one shared
    /// scraper key was ceremony without a threat model.</para>
    /// </summary>
    [ApiController]
    [Route("admin")]
    [Authorize(Policy = "admin")]
    public sealed class AdminController : ControllerBase
    {
        private readonly BooksDb db;
        private readonly BooksOptions options;
        private readonly JobRunner jobs;
        private readonly InMemoryLogStore logs;
        private readonly BooksSettingsOverlay settings;
        private readonly LibraryScanner scanner;
        private readonly ThumbnailJob thumbnails;
        private readonly ThumbnailService thumbnailService;
        private readonly DuplicateDetectionService dedup;
        private readonly DataNormalizationService normalization;
        private readonly SeriesMismatchService mismatch;
        private readonly SeriesNamesService seriesNames;

        public AdminController(
            BooksDb db, BooksOptions options, JobRunner jobs, InMemoryLogStore logs, BooksSettingsOverlay settings,
            LibraryScanner scanner, ThumbnailJob thumbnails, ThumbnailService thumbnailService,
            DuplicateDetectionService dedup, DataNormalizationService normalization,
            SeriesMismatchService mismatch, SeriesNamesService seriesNames)
        {
            this.db = db;
            this.options = options;
            this.jobs = jobs;
            this.logs = logs;
            this.settings = settings;
            this.scanner = scanner;
            this.thumbnails = thumbnails;
            this.thumbnailService = thumbnailService;
            this.dedup = dedup;
            this.normalization = normalization;
            this.mismatch = mismatch;
            this.seriesNames = seriesNames;
        }

        private string? Who => BooksIdentity.Username(User);

        // ── info & the derived registry ──────────────────────────────────────────────────────────────────

        /// <summary>GET /admin/info — the counts an operator checks first, plus what this host is configured with.</summary>
        [HttpGet("info")]
        public async Task<IActionResult> Info(CancellationToken ct)
        {
            var lastScan = await db.ScanRuns.AsNoTracking().OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
            return Ok(new
            {
                catalog = new
                {
                    roots = await db.LibraryRoots.CountAsync(ct),
                    folders = await db.Folders.CountAsync(ct),
                    items = await db.Items.CountAsync(ct),
                    comics = await db.Items.CountAsync(i => i.Kind == ItemKind.Comic, ct),
                    books = await db.Items.CountAsync(i => i.Kind == ItemKind.Book, ct),
                    excluded = await db.Items.CountAsync(i => i.IsExcluded, ct),
                    // Shadowed items sit out of this count the way they sit out of every browse surface —
                    // and the way the standalone's own census computed it. A file nobody can reach is not
                    // an operator's problem; `excluded` above is where it is already reported.
                    broken = await db.Items.CountAsync(i => !i.IsExcluded && i.State != null && i.State.IsBroken, ct),
                    series = await db.Series.CountAsync(ct),
                    publishers = await db.Publishers.CountAsync(ct),
                },
                derived = new
                {
                    readingOrder = await db.ReadingOrderEntries.CountAsync(ct),
                    collectionNodes = await db.CollectionNodes.CountAsync(ct),
                    collectedEditionSpans = await db.CollectedEditionSpans.CountAsync(ct),
                    libraryRatings = await db.Ratings.CountAsync(r => r.Source == RatingSource.Library, ct),
                    itemTags = await db.ItemTags.CountAsync(ct),
                    seriesTags = await db.SeriesTags.CountAsync(ct),
                },
                links = new
                {
                    seriesKeyLinks = await db.SeriesKeyLinks.CountAsync(ct),
                    itemProviderLinks = await db.ItemProviderLinks.CountAsync(ct),
                    pending = await db.SeriesKeyLinks.CountAsync(l => l.Status == LinkStatus.Pending, ct),
                    multiple = await db.SeriesKeyLinks.CountAsync(l => l.Status == LinkStatus.Multiple, ct),
                },
                dedupGroups = await db.DuplicateGroups.CountAsync(ct),
                openDedupGroups = await db.DuplicateGroups.CountAsync(g => g.ReviewState == "Pending", ct),
                lastScan,
                host = new
                {
                    cacheDir = options.CacheDir != null,
                    mediaPlane = options.MediaTokenSecret != null,
                    settingsOverlay = settings.Path,
                    comicVineConfigured = ComicVineKey() != null,
                },
                jobs = jobs.All(),
            });
        }

        /// <summary>
        /// GET /admin/derived — the registry panel: every derived table, the job that rebuilds it, its input
        /// fingerprint and when it last ran. `stale` compares the STORED fingerprint against the inputs' current
        /// one, which is how an operator sees that a scan has landed but the resolver has not run yet.
        /// </summary>
        [HttpGet("derived")]
        public IActionResult Derived()
        {
            var dbPath = options.DbPath;
            if (dbPath == null) return Ok(Array.Empty<object>());
            using var hot = new TargetWriter(dbPath, MappingContract.Load(), dryRun: true);
            var stored = hot.Pairs("SELECT rowid, Name || char(31) || coalesce(InputFingerprint,'') || char(31) || coalesce(LastRebuiltAt,'') || char(31) || RowCount FROM DerivedTable")
                .Select(p => p.Item2!.Split(TargetWriter.Sep))
                .ToDictionary(p => p[0], p => p, StringComparer.Ordinal);

            // Materialized before the writer is disposed: a deferred Select would be enumerated by the
            // serializer, long after this method's `using` closed the connection.
            return Ok(DerivedTables.All.Select(object (entry) =>
            {
                stored.TryGetValue(entry.Name, out var row);
                string? current = null;
                try { current = ResolvePipeline.Fingerprint(hot, entry.FingerprintSql); } catch (Microsoft.Data.Sqlite.SqliteException) { }
                return new
                {
                    name = entry.Name,
                    rebuildJob = entry.RebuildJob,
                    lastRebuiltAt = row != null && row[2].Length > 0 ? row[2] : null,
                    rowCount = row != null && int.TryParse(row[3], out var n) ? n : 0,
                    storedFingerprint = row?[1],
                    currentFingerprint = current,
                    stale = row == null || current == null || !string.Equals(row[1], current, StringComparison.Ordinal),
                };
            }).ToList());
        }

        // ── job control ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>GET /admin/jobs/status — every job this host has run since it started.</summary>
        [HttpGet("jobs/status")]
        public IActionResult JobsStatus([FromQuery] string? kind) =>
            kind == null ? Ok(jobs.All()) : jobs.Status(kind) is JobStatus s ? Ok(s) : NotFound();

        /// <summary>POST /admin/jobs/{kind}/stop — ask a running job to stop at its next batch boundary.</summary>
        [HttpPost("jobs/{kind}/stop")]
        public IActionResult StopJob(string kind) =>
            jobs.Stop(kind) ? Ok(jobs.Status(kind)) : NotFound(new { error = $"Job '{kind}' is not running." });

        private async Task<IActionResult> StartAsync(string kind, JobRunner.BatchStep step, int maxBatches = 0)
        {
            try
            {
                var status = await jobs.StartAsync(kind, step, maxBatches);
                return Accepted(new { job = status, statusUrl = $"/admin/jobs/status?kind={kind}" });
            }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        }

        // ── scan ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// POST /admin/scan/start — walk the share and reconcile the catalog. Without <c>?apply=true</c> this
        /// runs a PREVIEW instead: the counts a scan would add, change and remove, with nothing written. A
        /// destructive job states its damage before it does it.
        /// </summary>
        [HttpPost("scan/start")]
        public async Task<IActionResult> ScanStart([FromQuery] int? rootId, [FromQuery] bool apply = false, [FromQuery] int batchSize = LibraryScanner.DefaultBatchSize, CancellationToken ct = default)
        {
            if (!apply)
            {
                try { return Ok(new { dryRun = true, preview = await scanner.PreviewAsync(db, rootId, ct) }); }
                catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
            }
            try { await scanner.StartAsync(db, rootId, ct); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }

            return await StartAsync("scan", async (services, token) =>
            {
                var scoped = services.GetRequiredService<BooksDb>();
                var job = services.GetRequiredService<LibraryScanner>();
                var r = await job.RunBatchAsync(scoped, batchSize, apply: true, token);
                return new JobProgress(r.Processed, r.Remaining, r.NextCursor, r.Failed, r.ToString());
            });
        }

        [HttpGet("scan/status")]
        public async Task<IActionResult> ScanStatus(CancellationToken ct) =>
            Ok(new { job = jobs.Status("scan"), phase = await scanner.StatusAsync(db, ct) });

        [HttpPost("scan/stop")]
        public IActionResult ScanStop() => StopJob("scan");

        // ── thumbnails ───────────────────────────────────────────────────────────────────────────────────

        [HttpPost("thumbnails/start")]
        public async Task<IActionResult> ThumbnailsStart([FromQuery] int batchSize = ThumbnailJob.DefaultBatchSize, [FromQuery] bool reset = false, CancellationToken ct = default)
        {
            if (!thumbnailService.Configured) return BadRequest(new { error = "Books:CacheDir is not configured on this host." });
            if (reset) await thumbnails.ResetAsync(db, ct);
            return await StartAsync("thumbnails", async (services, token) =>
            {
                var scoped = services.GetRequiredService<BooksDb>();
                var job = services.GetRequiredService<ThumbnailJob>();
                var r = await job.RunBatchAsync(scoped, batchSize, token);
                return new JobProgress(r.Processed, r.Remaining, r.NextCursor?.ToString(), r.Failed,
                    $"generated {r.Generated}, skipped {r.Skipped}, failed {r.Failed}");
            });
        }

        [HttpGet("thumbnails/status")]
        public async Task<IActionResult> ThumbnailsStatus(CancellationToken ct)
        {
            var s = await thumbnails.StatusAsync(db, ct);
            return Ok(new { job = jobs.Status("thumbnails"), cursor = s.Cursor, processed = s.Processed, generated = s.Generated, skipped = s.Skipped, failed = s.Failed, remaining = s.Remaining });
        }

        [HttpPost("thumbnails/stop")]
        public IActionResult ThumbnailsStop() => StopJob("thumbnails");

        /// <summary>
        /// GET /admin/broken — the files a scan or a thumbnail pass could not read. Paged, because "every broken
        /// file" on a 141k library is a list nobody wants in one response.
        /// </summary>
        [HttpGet("broken")]
        public async Task<IActionResult> Broken([FromQuery] int skip = 0, [FromQuery] int top = 100, CancellationToken ct = default)
        {
            top = Math.Clamp(top, 1, 500);
            var q = from s in db.ItemStates.AsNoTracking()
                    where s.IsBroken || s.ThumbnailError != null
                    join i in db.Items.AsNoTracking() on s.ItemId equals i.Id
                    orderby s.ItemId
                    select new { i.Id, i.Path, i.FileName, s.IsBroken, s.BrokenReason, s.ThumbnailError, s.BrokenCheckedAt, s.ThumbnailCheckedAt };
            return Ok(new { totalCount = await q.CountAsync(ct), skip, top, items = await q.Skip(skip).Take(top).ToListAsync(ct) });
        }

        // ── library roots ────────────────────────────────────────────────────────────────────────────────

        public sealed record RootBody(string Path, ItemKind Kind, bool IsCalibre, bool Enabled);

        [HttpGet("roots")]
        public async Task<IActionResult> Roots(CancellationToken ct) =>
            Ok(await db.LibraryRoots.AsNoTracking().OrderBy(r => r.Id)
                .Select(r => new { r.Id, r.Path, r.Kind, r.IsCalibre, r.Enabled, reachable = Directory.Exists(r.Path) })
                .ToListAsync(ct));

        [HttpPost("roots")]
        public async Task<IActionResult> AddRoot([FromBody] RootBody body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.Path)) return BadRequest(new { error = "A path is required." });
            var path = body.Path.TrimEnd('\\', '/');
            if (await db.LibraryRoots.AnyAsync(r => r.Path == path, ct)) return Conflict(new { error = "That root already exists." });
            var id = (await db.LibraryRoots.Select(r => (int?)r.Id).MaxAsync(ct) ?? 0) + 1;
            var root = new LibraryRoot { Id = id, Path = path, Kind = body.Kind, IsCalibre = body.IsCalibre, Enabled = body.Enabled };
            db.LibraryRoots.Add(root);
            await db.SaveChangesAsync(ct);
            return Ok(root);
        }

        [HttpPut("roots/{id:int}")]
        public async Task<IActionResult> UpdateRoot(int id, [FromBody] RootBody body, CancellationToken ct)
        {
            var root = await db.LibraryRoots.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (root == null) return NotFound();
            if (!string.IsNullOrWhiteSpace(body.Path)) root.Path = body.Path.TrimEnd('\\', '/');
            root.Kind = body.Kind;
            root.IsCalibre = body.IsCalibre;
            root.Enabled = body.Enabled;
            await db.SaveChangesAsync(ct);
            return Ok(root);
        }

        /// <summary>
        /// DELETE /admin/roots/{id} — refuses while the root still holds items. Removing a root is not a way to
        /// delete a library; empty it with a scan first, so the removal is a bookkeeping act and not a silent
        /// cascade through 20,000 rows.
        /// </summary>
        [HttpDelete("roots/{id:int}")]
        public async Task<IActionResult> DeleteRoot(int id, CancellationToken ct)
        {
            var root = await db.LibraryRoots.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (root == null) return NotFound();
            var items = await db.Items.CountAsync(i => i.RootId == id, ct);
            if (items > 0) return Conflict(new { error = $"Root {id} still holds {items} items; scan it empty first." });
            db.LibraryRoots.Remove(root);
            await db.SaveChangesAsync(ct);
            return NoContent();
        }

        // ── Calibre import ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// POST /admin/calibre/import — fill the books' Calibre-native identity from a Calibre `metadata.db`.
        /// This is the job that finally fills `BookDetail.SeriesName`, which v1 never had a column for.
        /// </summary>
        [HttpPost("calibre/import")]
        public async Task<IActionResult> CalibreImport(
            [FromQuery] string? metadata, [FromQuery] string? link, [FromQuery] bool apply = false,
            [FromQuery] int batchSize = CalibreImportService.DefaultBatchSize, CancellationToken ct = default)
        {
            var metadataPath = metadata ?? DefaultCalibreMetadata();
            if (metadataPath == null || !System.IO.File.Exists(metadataPath))
                return BadRequest(new { error = "No Calibre metadata.db found. Pass ?metadata= or mark a library root IsCalibre." });

            return await StartAsync("calibre-import", async (services, token) =>
            {
                var scoped = services.GetRequiredService<BooksDb>();
                var job = services.GetRequiredService<CalibreImportService>();
                var r = await job.RunBatchAsync(scoped, metadataPath, link, batchSize, apply, ct: token);
                return new JobProgress(r.Processed, r.Remaining, r.NextCursor?.ToString(), r.Unmatched, r.ToString());
            });
        }

        private string? DefaultCalibreMetadata()
        {
            var root = db.LibraryRoots.AsNoTracking().FirstOrDefault(r => r.IsCalibre);
            return root == null ? null : System.IO.Path.Combine(root.Path, "metadata.db");
        }

        // ── cache clear & the folder icon ────────────────────────────────────────────────────────────────

        /// <summary>
        /// POST /admin/cache/clear — delete GENERATED cover thumbnails only.
        ///
        /// <para><b>The name guard is the whole safety property.</b> Only files matching <c>^\d+\.webp$</c> are
        /// removed: those are the ones <c>books-thumbs</c> can regenerate from the library. A hand-uploaded
        /// collection icon is <c>f_{id}.jpg</c> and can NEVER be regenerated, so it must survive a cache clear —
        /// which is exactly why the guard is a whitelist on the name and not a wildcard on the directory.</para>
        /// </summary>
        [HttpPost("cache/clear")]
        public IActionResult ClearCache([FromQuery] bool apply = false)
        {
            if (!thumbnailService.Configured) return BadRequest(new { error = "Books:CacheDir is not configured on this host." });
            var dir = options.CacheDir!;
            var doomed = Directory.EnumerateFiles(dir)
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(f), @"^\d+\.webp$"))
                .ToList();
            var kept = Directory.EnumerateFiles(dir).Count() - doomed.Count;
            if (!apply) return Ok(new { dryRun = true, wouldDelete = doomed.Count, kept });

            var deleted = 0;
            foreach (var f in doomed)
            {
                try { System.IO.File.Delete(f); deleted++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return Ok(new { deleted, kept });
        }

        /// <summary>
        /// POST /admin/folders/{id}/icon — upload a collection icon, stored as <c>f_{id}.jpg</c> beside the
        /// thumbnails. It is hand-made art and is never regenerated, which is why the cache clear above spares it.
        /// </summary>
        [HttpPost("folders/{id:int}/icon")]
        public async Task<IActionResult> UploadIcon(int id, IFormFile file, CancellationToken ct)
        {
            if (!thumbnailService.Configured) return BadRequest(new { error = "Books:CacheDir is not configured on this host." });
            if (file == null || file.Length == 0) return BadRequest(new { error = "A file is required." });
            if (file.Length > 4 * 1024 * 1024) return BadRequest(new { error = "An icon must be 4 MB or smaller." });
            var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (folder == null) return NotFound();

            var path = System.IO.Path.Combine(options.CacheDir!, $"f_{id}.jpg");
            await using (var stream = System.IO.File.Create(path)) await file.CopyToAsync(stream, ct);
            folder.HasIcon = true;
            await db.SaveChangesAsync(ct);
            return Ok(new { folderId = id, hasIcon = true });
        }

        [HttpDelete("folders/{id:int}/icon")]
        public async Task<IActionResult> DeleteIcon(int id, CancellationToken ct)
        {
            var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (folder == null) return NotFound();
            if (thumbnailService.Configured)
            {
                var path = System.IO.Path.Combine(options.CacheDir!, $"f_{id}.jpg");
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            folder.HasIcon = false;
            await db.SaveChangesAsync(ct);
            return NoContent();
        }

        // ── config overlay ───────────────────────────────────────────────────────────────────────────────

        [HttpGet("config")]
        public IActionResult GetConfig() => Ok(new
        {
            path = settings.Path,
            writable = settings.Configured,
            keys = BooksSettingsOverlay.AllowedKeys.Select(k => new { k.Name, kind = k.Kind.ToString(), k.Min, k.Max, k.Description }),
            values = settings.Read(),
        });

        /// <summary>
        /// PUT /admin/config — write the allow-listed keys. An unknown key is a 400, not a silent no-op: a typo
        /// that quietly does nothing is worse than an error.
        /// </summary>
        [HttpPut("config")]
        public IActionResult PutConfig([FromBody] Dictionary<string, object?> body)
        {
            try { return Ok(settings.Write(body)); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ── logs ─────────────────────────────────────────────────────────────────────────────────────────

        [HttpGet("logs")]
        public IActionResult Logs([FromQuery] int count = 200, [FromQuery] string? level = null, [FromQuery] long afterSeq = 0) =>
            Ok(new { capacity = InMemoryLogStore.Capacity, entries = logs.Tail(count, level, afterSeq) });

        [HttpDelete("logs")]
        public IActionResult ClearLogs() { logs.Clear(); return NoContent(); }

        // ── kids tags ────────────────────────────────────────────────────────────────────────────────────

        public sealed record KidTagBody(string Category, string Tag, string? AppliesTo);

        [HttpGet("kids-tags")]
        public async Task<IActionResult> KidsTags(CancellationToken ct) =>
            Ok(await db.KidSafeTags.AsNoTracking().OrderBy(t => t.Category).ThenBy(t => t.Tag).ToListAsync(ct));

        [HttpPut("kids-tags")]
        public async Task<IActionResult> UpsertKidsTag([FromBody] KidTagBody body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.Category) || string.IsNullOrWhiteSpace(body.Tag))
                return BadRequest(new { error = "category and tag are required." });
            var category = body.Category.Trim().ToLowerInvariant();
            var tag = body.Tag.Trim().ToLowerInvariant();
            var row = await db.KidSafeTags.FirstOrDefaultAsync(t => t.Category == category && t.Tag == tag, ct);
            if (row == null) { row = new KidSafeTag { Category = category, Tag = tag }; db.KidSafeTags.Add(row); }
            row.AppliesTo = string.IsNullOrWhiteSpace(body.AppliesTo) ? "both" : body.AppliesTo.Trim().ToLowerInvariant();
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Ok(row);
        }

        [HttpDelete("kids-tags/{category}/{tag}")]
        public async Task<IActionResult> DeleteKidsTag(string category, string tag, CancellationToken ct)
        {
            var row = await db.KidSafeTags.FirstOrDefaultAsync(t => t.Category == category && t.Tag == tag, ct);
            if (row == null) return NotFound();
            db.KidSafeTags.Remove(row);
            await db.SaveChangesAsync(ct);
            return NoContent();
        }

        // ── recompute triggers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// POST /admin/recompute/{what} — start the job that owns one derived table. <c>what</c> is one of
        /// <c>series</c>, <c>resolve</c>, <c>tags</c>, <c>reading-order</c>, <c>containment</c>,
        /// <c>collected-editions</c>, <c>ratings</c>. Each answers with its first batch and a status URL.
        /// </summary>
        [HttpPost("recompute/{what}")]
        public async Task<IActionResult> Recompute(string what, [FromQuery] int batchSize = 2_000, [FromQuery] int? seriesId = null)
        {
            var dbPath = options.DbPath;
            if (dbPath == null) return BadRequest(new { error = "No catalog is configured on this host." });
            var legsPath = LegsPath();

            var kind = what.ToLowerInvariant();

            // The two biggest loops walk 19k series apiece, so they run ONE PAGE per JobRunner call with their
            // cursor persisted between calls. That is what makes them watchable and genuinely stoppable — a
            // stop lands at a page boundary with the cursor committed, exactly like the CLI verb's loop.
            if (kind is "reading-order" or "containment")
            {
                var cursorKey = "books:recompute:" + kind;
                if (seriesId == null) await ResetCursorAsync(cursorKey);
                return await StartAsync("recompute:" + kind, (_, _) => Task.Run(() => PageSeriesJob(kind, dbPath, cursorKey, batchSize, seriesId)));
            }

            // The rest are phase machines bounded by the SERIES count or by their own paged inner loop, and each
            // commits per phase, so a kill costs one phase. They report once.
            JobRunner.BatchStep? step = kind switch
            {
                "series" => (_, _) => Task.Run(() => DrainSeries(dbPath, batchSize)),
                "resolve" => (_, _) => Task.Run(() => DrainResolve(dbPath, batchSize)),
                "tags" => legsPath == null ? null : (_, _) => Task.Run(() => DrainTags(dbPath, legsPath)),
                "collected-editions" => legsPath == null ? null : (_, _) => Task.Run(() => DrainCollectedEditions(dbPath, legsPath, batchSize)),
                "ratings" => (_, _) => Task.Run(() => DrainRatings(dbPath, batchSize)),
                _ => null,
            };
            if (step == null)
                return BadRequest(new { error = $"Unknown recompute '{what}' (or the legs file is not configured for it)." });

            return await StartAsync("recompute:" + kind, step, maxBatches: 1);
        }

        private async Task ResetCursorAsync(string key)
        {
            var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == key);
            if (row != null) { db.SystemStates.Remove(row); await db.SaveChangesAsync(); }
        }

        /// <summary>One page of a per-series job, with its cursor read and written inside the same connection
        /// that does the work — so a stop between pages loses nothing and a restart continues.</summary>
        private static JobProgress PageSeriesJob(string kind, string dbPath, string cursorKey, int batchSize, int? seriesId)
        {
            using var hot = new TargetWriter(dbPath, MappingContract.Load(), dryRun: false);
            var cursor = seriesId is int only ? only - 1 : hot.Scalar<long>("SELECT CAST(coalesce(Value, '0') AS INTEGER) FROM SystemState WHERE Key = $k", ("$k", cursorKey));

            hot.Begin();
            var (processed, remaining, next, rows, label) = kind == "reading-order"
                ? Run(ReadingOrderJob.RunBatch(hot, cursor, batchSize, seriesId), "reading-order rows")
                : Run(ContainmentJob.RunBatch(hot, cursor, batchSize), "collection nodes");
            if (next != null)
                hot.Upsert("SystemState", new { Key = cursorKey, Value = next.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            if (processed > 0 && kind == "reading-order" && remaining == 0) ReadingOrderJob.Stamp(hot);
            if (processed > 0 && kind == "containment" && remaining == 0) ContainmentJob.Stamp(hot);
            hot.Commit();

            return new JobProgress(processed, remaining, next?.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, $"{rows} {label}");

            static (int, long, long?, int, string) Run(dynamic r, string label) =>
                ((int)r.Processed, (long)r.Remaining, (long?)r.NextCursor, (int)r.Rows, label);
        }

        private static JobProgress DrainSeries(string dbPath, int batchSize)
        {
            using var hot = new TargetWriter(dbPath, MappingContract.Load(), dryRun: false);
            var counts = SeriesRebuildJob.RunAll(hot, batchSize, _ => { });
            var diff = SeriesResolver.Diff(hot);
            // Books after comics: the comic finish pass deletes rows and re-points items, and the book counts
            // have to be computed over the settled ids. Same order as `books-resolve --series`.
            var books = BookSeriesLinkJob.RunAll(hot, batchSize, _ => { });
            return new JobProgress(counts.Detail.GetValueOrDefault("items-repointed"), 0, null, 0, $"{counts}; books {books}; diff {diff.Total}");
        }

        private static JobProgress DrainResolve(string dbPath, int batchSize)
        {
            using var hot = new TargetWriter(dbPath, MappingContract.Load(), dryRun: false);
            hot.Begin();
            var items = ResolvePipeline.RunAll(hot, Math.Max(100, batchSize), _ => { });
            hot.Commit();
            hot.Begin();
            hot.Exec(ItemFts.ClearSql);
            long cursor = 0;
            var total = 0;
            while (true)
            {
                cursor = FtsBuilder.IndexBatch(hot, cursor, Math.Max(100, batchSize), out var n);
                total += n;
                if (n < Math.Max(100, batchSize)) break;
            }
            hot.Exec(ItemFts.OptimizeSql);
            hot.Commit();
            return new JobProgress(items, 0, null, 0, $"resolved {items} items, indexed {total}");
        }

        private static JobProgress DrainTags(string dbPath, string legsPath)
        {
            using var hot = new TargetWriter(dbPath, MappingContract.Load(), dryRun: false);
            var counts = LegsTagFoldJob.RunAll(hot, legsPath, _ => { });
            return new JobProgress(counts.External + counts.Mu + counts.Gcd, 0, null, 0, counts.ToString());
        }

        private static JobProgress DrainCollectedEditions(string dbPath, string legsPath, int batchSize)
        {
            using var hot = new TargetWriter(dbPath, MappingContract.Load(), dryRun: false);
            var (spans, skipped) = CollectedEditionJob.RunAll(hot, legsPath, batchSize, _ => { });
            return new JobProgress(spans, 0, null, skipped, $"{spans} spans, {skipped} skipped");
        }

        private static JobProgress DrainRatings(string dbPath, int batchSize)
        {
            using var hot = new TargetWriter(dbPath, MappingContract.Load(), dryRun: false);
            var counts = LibraryRatingJob.RunAll(hot, batchSize, _ => { });
            // The blend writes ROWS; the browse reads the materialized scalar, so the resolver runs after it.
            hot.Begin();
            ItemResolver.ResolveSeries(hot);
            long after = 0;
            while (true)
            {
                var last = ItemResolver.ResolveItems(hot, after, Math.Max(100, batchSize), out var n);
                after = last;
                if (n < Math.Max(100, batchSize)) break;
            }
            hot.Commit();
            return new JobProgress(counts.Items, 0, null, 0, counts.ToString());
        }

        // ── dedup ────────────────────────────────────────────────────────────────────────────────────────

        [HttpPost("dedup/start")]
        public async Task<IActionResult> DedupStart([FromQuery] int batchSize = DuplicateDetectionService.DefaultBatchSize, [FromQuery] bool reset = true, CancellationToken ct = default)
        {
            if (reset) await dedup.ResetAsync(db, ct);
            return await StartAsync("dedup", async (services, token) =>
            {
                var scoped = services.GetRequiredService<BooksDb>();
                var job = services.GetRequiredService<DuplicateDetectionService>();
                var r = await job.RunBatchAsync(scoped, batchSize, apply: true, csv: null, token);
                return new JobProgress(r.Processed, r.Remaining, r.NextCursor?.ToString(), 0, r.ToString());
            });
        }

        [HttpGet("dedup")]
        public async Task<IActionResult> DedupList([FromQuery] string state = "Pending", [FromQuery] int skip = 0, [FromQuery] int top = 50, CancellationToken ct = default)
        {
            top = Math.Clamp(top, 1, 200);
            var q = db.DuplicateGroups.AsNoTracking().Where(g => g.ReviewState == state).OrderBy(g => g.Id);
            var total = await q.CountAsync(ct);
            var groups = await q.Skip(skip).Take(top).ToListAsync(ct);
            var ids = groups.Select(g => g.Id).ToList();
            var members = await (from m in db.DuplicateMembers.AsNoTracking()
                                 where ids.Contains(m.DuplicateGroupId)
                                 join i in db.Items.AsNoTracking() on m.ItemId equals i.Id
                                 select new { m.DuplicateGroupId, m.ItemId, m.Role, m.SoleFileInFolder, i.Path, i.FileName, i.FileSize, i.PageCount })
                                .ToListAsync(ct);
            return Ok(new
            {
                totalCount = total, skip, top,
                groups = groups.Select(g => new { g.Id, g.Relationship, g.Confidence, g.Evidence, g.SuggestedKeeperItemId, g.ReviewState, g.DetectedAt, members = members.Where(m => m.DuplicateGroupId == g.Id) }),
            });
        }

        /// <summary>
        /// POST /admin/dedup/{id}/resolve — hide the losers. They are marked `IsExcluded` and stay in the
        /// Directory drill; no file on the share is ever touched.
        /// </summary>
        [HttpPost("dedup/{id:int}/resolve")]
        public async Task<IActionResult> DedupResolve(int id, [FromQuery] int? keeperItemId, CancellationToken ct)
        {
            try { return Ok(new { groupId = id, hidden = await dedup.ResolveAsync(db, id, keeperItemId, ct) }); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ── normalization ────────────────────────────────────────────────────────────────────────────────

        public sealed record AliasBody(string Category, string AliasTag, string CanonicalTag);

        [HttpGet("normalization/aliases")]
        public async Task<IActionResult> Aliases(CancellationToken ct) =>
            Ok(await db.TagAlias.AsNoTracking().OrderBy(a => a.Category).ThenBy(a => a.AliasTag).ToListAsync(ct));

        [HttpPut("normalization/aliases")]
        public async Task<IActionResult> UpsertAlias([FromBody] AliasBody body, CancellationToken ct)
        {
            try { return Ok(await normalization.UpsertAliasAsync(db, body.Category, body.AliasTag, body.CanonicalTag, ct)); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpDelete("normalization/aliases/{category}/{aliasTag}")]
        public async Task<IActionResult> DeleteAlias(string category, string aliasTag, CancellationToken ct) =>
            await normalization.DeleteAliasAsync(db, category, aliasTag, ct) ? NoContent() : NotFound();

        /// <summary>POST /admin/normalization/apply — clean the INPUT tags; the caller then re-runs the folds.</summary>
        [HttpPost("normalization/apply")]
        public async Task<IActionResult> ApplyNormalization([FromQuery] bool apply = false, CancellationToken ct = default)
        {
            var result = await normalization.NormalizeTagsAsync(db, apply, ct);
            return Ok(new { dryRun = !apply, result, next = "POST /admin/recompute/resolve" });
        }

        // ── series reconciliation ────────────────────────────────────────────────────────────────────────

        public sealed record LinkBody(string ParsedKey, Provider Provider, int? ProviderKey);
        public sealed record FoldBody(string FromKey, string ToKey);
        public sealed record UnifyBody(int FolderId, string ParsedKey);
        public sealed record ReviewBody(string Scope, string Key, string State, string? Note);
        public sealed record OverrideBody(string? DisplayName);

        [HttpGet("series/summary")]
        public async Task<IActionResult> SeriesSummary(CancellationToken ct) => Ok(await mismatch.SummaryAsync(db, ct));

        [HttpGet("series/{id:int}/aliases")]
        public async Task<IActionResult> SeriesAliases(int id, CancellationToken ct) => Ok(await mismatch.AliasesAsync(db, id, ct));

        [HttpGet("series/link-candidates")]
        public async Task<IActionResult> LinkCandidates([FromQuery] string parsedKey, [FromQuery] Provider provider = Provider.Cv, CancellationToken ct = default) =>
            await mismatch.LinkCandidatesAsync(db, parsedKey, provider, ct) is object o ? Ok(o) : NotFound();

        [HttpPost("series/clear-link")]
        public async Task<IActionResult> ClearLink([FromBody] LinkBody body, CancellationToken ct) =>
            Ok(await mismatch.ClearLinkAsync(db, body.ParsedKey, body.Provider, Who, ct));

        [HttpPost("series/set-link")]
        public async Task<IActionResult> SetLink([FromBody] LinkBody body, CancellationToken ct) =>
            body.ProviderKey is int key
                ? Ok(await mismatch.SetLinkAsync(db, body.ParsedKey, body.Provider, key, Who, ct))
                : BadRequest(new { error = "providerKey is required." });

        [HttpPost("series/fold")]
        public async Task<IActionResult> Fold([FromBody] FoldBody body, CancellationToken ct)
        {
            try { return Ok(await mismatch.FoldParsedKeyAsync(db, body.FromKey, body.ToKey, Who, ct)); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("series/unify-folder")]
        public async Task<IActionResult> UnifyFolder([FromBody] UnifyBody body, CancellationToken ct)
        {
            try { return Ok(await mismatch.UnifyFolderAsync(db, body.FolderId, body.ParsedKey, Who, ct)); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("series/review")]
        public async Task<IActionResult> Review([FromBody] ReviewBody body, CancellationToken ct) =>
            Ok(await mismatch.MarkReviewedAsync(db, body.Scope, body.Key, body.State, body.Note, Who, ct));

        [HttpGet("series/decisions")]
        public async Task<IActionResult> Decisions([FromQuery] string? state, [FromQuery] int skip = 0, [FromQuery] int top = 50, CancellationToken ct = default) =>
            Ok(await mismatch.DecisionsAsync(db, state, skip, top, ct));

        [HttpPost("series/decisions/{id:int}/revert")]
        public async Task<IActionResult> RevertDecision(int id, CancellationToken ct)
        {
            try { return Ok(await mismatch.RevertDecisionAsync(db, id, Who, ct)); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPut("series/{id:int}/override")]
        public async Task<IActionResult> SetOverride(int id, [FromBody] OverrideBody body, CancellationToken ct)
        {
            try { return Ok(await seriesNames.SetOverrideAsync(db, id, body.DisplayName, ct)); }
            catch (InvalidOperationException) { return NotFound(); }
        }

        [HttpGet("series/namefix")]
        public async Task<IActionResult> NameFix([FromQuery] bool apply = false, CancellationToken ct = default) =>
            Ok(new { dryRun = !apply, fixes = await seriesNames.NameFixAsync(db, apply, ct) });

        [HttpPost("series/prune")]
        public async Task<IActionResult> Prune([FromQuery] bool apply = false, CancellationToken ct = default)
        {
            var (candidates, deleted) = await seriesNames.PruneAsync(db, apply, ct);
            return Ok(new { dryRun = !apply, candidates, deleted });
        }

        [HttpGet("series/split-overmatch")]
        public async Task<IActionResult> SplitOvermatch([FromQuery] double ratio = 2.0, [FromQuery] int minIssues = 20, CancellationToken ct = default) =>
            Ok(await seriesNames.SplitOvermatchAsync(db, ratio, minIssues, ct));

        // ── provider scrapes ─────────────────────────────────────────────────────────────────────────────

        [HttpPost("comicvine/start")]
        public async Task<IActionResult> ComicVineStart([FromQuery] string mode = "series", [FromQuery] int batchSize = 25, [FromQuery] bool apply = true)
        {
            var kind = "comicvine:" + mode;
            if (mode == "series")
                return await StartAsync(kind, async (services, token) =>
                {
                    var scoped = services.GetRequiredService<BooksDb>();
                    var job = services.GetRequiredService<ComicVineSeriesScraper>();
                    var r = await job.RunBatchAsync(scoped, batchSize, apply, token);
                    return new JobProgress(r.Processed, r.Remaining, r.NextCursor, r.Failed, r.ToString());
                });
            if (mode == "issues")
                return await StartAsync(kind, async (services, token) =>
                {
                    var scoped = services.GetRequiredService<BooksDb>();
                    var job = services.GetRequiredService<ComicVineIssueScraper>();
                    var r = await job.RunBatchAsync(scoped, batchSize, apply, token);
                    return new JobProgress(r.Processed, r.Remaining, r.NextCursor, r.Failed, r.ToString());
                });
            return BadRequest(new { error = "mode must be 'series' or 'issues'." });
        }

        [HttpGet("comicvine/status")]
        public IActionResult ComicVineStatus() => Ok(new
        {
            configured = ComicVineKey() != null,
            series = jobs.Status("comicvine:series"),
            issues = jobs.Status("comicvine:issues"),
        });

        [HttpPost("comicvine/stop")]
        public IActionResult ComicVineStop([FromQuery] string mode = "series") => StopJob("comicvine:" + mode);

        [HttpPost("external/start")]
        public async Task<IActionResult> ExternalStart([FromQuery] int batchSize = 25, [FromQuery] bool apply = true) =>
            await StartAsync("external", async (services, token) =>
            {
                var scoped = services.GetRequiredService<BooksDb>();
                var job = services.GetRequiredService<ExternalWorkScraper>();
                var r = await job.RunBatchAsync(scoped, batchSize, apply, token);
                return new JobProgress(r.Processed, r.Remaining, r.NextCursor, r.Failed, r.ToString());
            });

        [HttpGet("external/status")]
        public IActionResult ExternalStatus() => Ok(jobs.Status("external"));

        [HttpPost("external/stop")]
        public IActionResult ExternalStop() => StopJob("external");

        /// <summary>
        /// GET /admin/{kind}/events — the live job feed as Server-Sent Events.
        ///
        /// <para>Two headers make this work through a reverse proxy and they are both load-bearing:
        /// <c>X-Accel-Buffering: no</c> stops nginx-family proxies buffering the stream into silence, and a
        /// <c>: keepalive</c> COMMENT every 20 s keeps idle intermediaries from closing a connection that is
        /// simply waiting for the next batch. A comment is not an event, so no client ever sees it as data.</para>
        /// </summary>
        [HttpGet("{kind}/events")]
        public async Task Events(string kind, CancellationToken ct)
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";

            var lastKeepalive = DateTime.UtcNow;
            string? lastPayload = null;
            while (!ct.IsCancellationRequested)
            {
                var status = jobs.Status(kind);
                var payload = status == null ? null : System.Text.Json.JsonSerializer.Serialize(status);
                if (payload != null && payload != lastPayload)
                {
                    await Response.WriteAsync($"event: status\ndata: {payload}\n\n", Encoding.UTF8, ct);
                    await Response.Body.FlushAsync(ct);
                    lastPayload = payload;
                    if (status!.State is "done" or "failed" or "stopped") break;
                }
                if (DateTime.UtcNow - lastKeepalive >= TimeSpan.FromSeconds(20))
                {
                    await Response.WriteAsync(": keepalive\n\n", Encoding.UTF8, ct);
                    await Response.Body.FlushAsync(ct);
                    lastKeepalive = DateTime.UtcNow;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private string? ComicVineKey() => settings.Value("ComicVineApiKey") ?? options.ComicVineApiKey;

        private string? LegsPath() => options.LegsDbPath;
    }
}
