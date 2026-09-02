using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Providers
{
    /// <summary>What one scrape batch did — the same envelope every job here answers with.</summary>
    public sealed record ScrapeBatchResult(int Processed, long Remaining, string? NextCursor, int Matched, int NoMatch, int Multiple, int Failed)
    {
        public bool Done => Processed == 0 || NextCursor == null;
        public override string ToString() =>
            $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", failed: {Failed} }}" +
            $"  [matched: {Matched}, noMatch: {NoMatch}, multiple: {Multiple}]";
    }

    /// <summary>
    /// <c>/admin/comicvine/*</c> — resolve a parsed series key to a ComicVine VOLUME.
    ///
    /// <para><b>The link is keyed by the PARSED KEY, not by a series id</b>, because it is a resolution INPUT:
    /// `books-resolve --series` reads `SeriesKeyLink` to decide which spellings are one series. Writing it the
    /// other way round would make the identity depend on itself.</para>
    ///
    /// <para><b>Budget-aware and chunked.</b> Each batch takes a bounded number of unresolved keys; the cursor
    /// is the parsed key itself, ordered, so a resumed run continues alphabetically instead of starting over.
    /// With no API key the scraper still runs — cache-only — and simply matches what the warehouse already
    /// knows, which is how a test drives it.</para>
    ///
    /// <para><b>Scoring, and what "Multiple" means.</b> Candidates are scored by
    /// <see cref="ComicVineClient.Score"/>; a clear winner (top score at or above the accept floor, and ahead of
    /// the runner-up by the margin) is `Matched`; a tie is `Multiple`, which PARKS the decision for a human and
    /// keeps the candidate blob on the hot row. Nothing is guessed. The winner's score is also stored as
    /// `StoredTopScore` so the stale-match heuristic has a number to compare against later.</para>
    /// </summary>
    public sealed class ComicVineSeriesScraper
    {
        public const string CursorKey = "books:cvscrape:cursor";

        /// <summary>Below this a candidate is not a match at all.</summary>
        public const int AcceptFloor = 70;

        /// <summary>The winner must beat the runner-up by this much, or the decision is a human's.</summary>
        public const int WinMargin = 10;

        private readonly ComicVineClient client;
        private readonly ProviderCacheStore store;
        private readonly ILogger<ComicVineSeriesScraper> logger;

        public ComicVineSeriesScraper(ComicVineClient client, ProviderCacheStore store, ILogger<ComicVineSeriesScraper> logger)
        {
            this.client = client;
            this.store = store;
            this.logger = logger;
        }

        public async Task<ScrapeBatchResult> RunBatchAsync(BooksDb db, int batchSize, bool apply = true, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 500);
            var cursor = await ReadAsync(db, CursorKey, ct) ?? "";

            // The unresolved parsed keys: a key with no Cv link at all, or one still Pending or in Error.
            var keys = await (from d in db.ComicDetails.AsNoTracking()
                              where d.ParsedSeriesKey != null && d.ParsedSeriesKey != "" && string.Compare(d.ParsedSeriesKey, cursor) > 0
                              join l in db.SeriesKeyLinks.AsNoTracking().Where(l => l.Provider == Provider.Cv)
                                  on d.ParsedSeriesKey equals l.ParsedKey into ls
                              from l in ls.DefaultIfEmpty()
                              where l == null || l.Status == LinkStatus.Pending || l.Status == LinkStatus.Error
                              select new { Key = d.ParsedSeriesKey!, d.Publisher, d.Year })
                             .GroupBy(x => x.Key)
                             .Select(g => new { Key = g.Key, Publisher = g.Max(x => x.Publisher), Year = g.Max(x => x.Year) })
                             .OrderBy(x => x.Key).Take(batchSize)
                             .ToListAsync(ct);

            if (keys.Count == 0) return new ScrapeBatchResult(0, 0, null, 0, 0, 0, 0);

            int matched = 0, noMatch = 0, multiple = 0, failed = 0;
            foreach (var k in keys)
            {
                ct.ThrowIfCancellationRequested();
                List<CvVolumeDto> candidates;
                try { candidates = await client.SearchVolumesAsync(k.Key, k.Publisher, k.Year, ct); }
                catch (Exception ex) { failed++; if (apply) await FailAsync(db, k.Key, ex.Message, ct); continue; }

                var scored = candidates
                    .Select(c => (Candidate: c, Score: ComicVineClient.Score(k.Key, c, k.Publisher, k.Year)))
                    .Where(s => s.Score > 0)
                    .OrderByDescending(s => s.Score).ThenBy(s => s.Candidate.Id)
                    .ToList();

                var link = apply ? await LinkAsync(db, k.Key, ct) : null;
                if (link != null) { link.AttemptCount++; link.AttemptedAt = DateTime.UtcNow; link.Error = null; }

                if (scored.Count == 0)
                {
                    noMatch++;
                    if (link != null) { link.Status = LinkStatus.NoMatch; link.ProviderKey = null; link.Score = null; }
                    continue;
                }

                var top = scored[0];
                var runnerUp = scored.Count > 1 ? scored[1].Score : 0;
                if (link != null) link.StoredTopScore = top.Score;

                if (top.Score < AcceptFloor || top.Score - runnerUp < WinMargin)
                {
                    multiple++;
                    if (link != null) { link.Status = LinkStatus.Multiple; link.Score = top.Score; }
                    if (apply) SaveCandidates(k.Key, scored.Take(5).Select(s => (s.Candidate, s.Score)).ToList());
                    continue;
                }

                matched++;
                if (link != null)
                {
                    link.Status = LinkStatus.Matched;
                    link.ProviderKey = (int)top.Candidate.Id;
                    link.Score = top.Score;
                }
                if (apply) await UpsertVolumeAsync(db, top.Candidate, ct);
            }

            var next = keys[^1].Key;
            if (apply) { await WriteAsync(db, CursorKey, next, ct); await db.SaveChangesAsync(ct); }

            logger.LogInformation("cv series scrape: {N} keys, matched {Matched}, noMatch {NoMatch}, multiple {Multiple}", keys.Count, matched, noMatch, multiple);
            return new ScrapeBatchResult(keys.Count, -1, next, matched, noMatch, multiple, failed);
        }

        /// <summary>
        /// An OPEN decision's top candidates go to the legs file's <c>LinkCandidates</c> (the hot row has no
        /// column for them by contract), so the admin's link view can show what was seen and scored. Until
        /// 2026-09-01 this was a no-op and a "Multiple" verdict left nothing to choose from.
        /// </summary>
        private void SaveCandidates(string parsedKey, List<(CvVolumeDto Candidate, int Score)> scored)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(scored.Select(s => new
            {
                id = s.Candidate.Id,
                name = s.Candidate.Name,
                publisher = s.Candidate.PublisherName,
                startYear = s.Candidate.StartYear,
                issues = s.Candidate.CountOfIssues,
                score = s.Score,
            }));
            try { store.PutLinkCandidates(SubjectKind.Series, parsedKey, Provider.Cv, json); }
            catch (Exception ex) { logger.LogWarning("cv series scrape: could not store candidates for '{Key}': {Message}", parsedKey, ex.Message); }
        }

        private static async Task UpsertVolumeAsync(BooksDb db, CvVolumeDto v, CancellationToken ct)
        {
            var row = await db.CvVolumes.FirstOrDefaultAsync(x => x.Id == (int)v.Id, ct);
            if (row == null) { row = new CvVolume { Id = (int)v.Id }; db.CvVolumes.Add(row); }
            row.Name = v.Name;
            row.StartYear = v.StartYear;
            row.PublisherName = v.PublisherName;
            row.CountOfIssues = v.CountOfIssues;
            row.Deck = v.Deck;
            row.Description = v.Description;
            row.ImageUrl = v.ImageUrl;
            row.SiteDetailUrl = v.SiteDetailUrl;
            row.FetchedAt = DateTime.UtcNow;
        }

        private static async Task<SeriesKeyLink> LinkAsync(BooksDb db, string parsedKey, CancellationToken ct)
        {
            var link = await db.SeriesKeyLinks.FirstOrDefaultAsync(l => l.ParsedKey == parsedKey && l.Provider == Provider.Cv, ct);
            if (link == null) { link = new SeriesKeyLink { ParsedKey = parsedKey, Provider = Provider.Cv }; db.SeriesKeyLinks.Add(link); }
            return link;
        }

        private static async Task FailAsync(BooksDb db, string parsedKey, string error, CancellationToken ct)
        {
            var link = await LinkAsync(db, parsedKey, ct);
            link.Status = LinkStatus.Error;
            link.Error = error.Length > 500 ? error[..500] : error;
            link.AttemptCount++;
            link.AttemptedAt = DateTime.UtcNow;
        }

        internal static async Task<string?> ReadAsync(BooksDb db, string key, CancellationToken ct) =>
            (await db.SystemStates.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

        internal static async Task WriteAsync(BooksDb db, string key, string value, CancellationToken ct)
        {
            var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (row == null) db.SystemStates.Add(new SystemState { Key = key, Value = value });
            else row.Value = value;
        }
    }

    /// <summary>
    /// Fill in the ISSUES of the volumes the series scraper matched, and link each item to its issue.
    ///
    /// <para>The volume's issue list is fetched a page of 100 at a time and stored as `CvIssue` rows; each item
    /// of the series is then matched to an issue by NUMBER — a comparison of normalized issue numbers, not of
    /// titles, because a floppy's title is routinely absent and its number never is. An item whose number
    /// matches nothing is `NoMatch`, not a guess.</para>
    ///
    /// <para>Chunked by `Series.Id`: one batch does a bounded number of series, and a series' own issue pages
    /// are themselves bounded, so no single call can run away.</para>
    /// </summary>
    public sealed class ComicVineIssueScraper
    {
        public const string CursorKey = "books:cvissues:cursor";

        private readonly ComicVineClient client;
        private readonly ILogger<ComicVineIssueScraper> logger;

        public ComicVineIssueScraper(ComicVineClient client, ILogger<ComicVineIssueScraper> logger)
        {
            this.client = client;
            this.logger = logger;
        }

        public async Task<ScrapeBatchResult> RunBatchAsync(BooksDb db, int batchSize, bool apply = true, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 200);
            var cursor = int.TryParse(await ComicVineSeriesScraper.ReadAsync(db, CursorKey, ct), out var c) ? c : 0;

            var series = await db.Series.AsNoTracking()
                .Where(s => s.CvVolumeId != null && s.Id > cursor)
                .OrderBy(s => s.Id).Take(batchSize)
                .Select(s => new { s.Id, VolumeId = s.CvVolumeId!.Value })
                .ToListAsync(ct);
            if (series.Count == 0) return new ScrapeBatchResult(0, 0, null, 0, 0, 0, 0);

            int matched = 0, noMatch = 0, failed = 0;
            foreach (var s in series)
            {
                ct.ThrowIfCancellationRequested();
                List<CvIssueDto> issues;
                try { issues = await FetchAllIssuesAsync(s.VolumeId, ct); }
                catch (Exception ex) { failed++; logger.LogWarning("cv issues: volume {V} failed: {Message}", s.VolumeId, ex.Message); continue; }
                if (issues.Count == 0) continue;

                if (apply)
                    foreach (var issue in issues)
                    {
                        var row = await db.CvIssues.FirstOrDefaultAsync(x => x.Id == (int)issue.Id, ct);
                        if (row == null) { row = new CvIssue { Id = (int)issue.Id }; db.CvIssues.Add(row); }
                        row.VolumeId = (int)issue.VolumeId;
                        row.Name = issue.Name; row.IssueNumber = issue.IssueNumber;
                        row.CoverDate = issue.CoverDate; row.StoreDate = issue.StoreDate;
                        row.Deck = issue.Deck; row.Description = issue.Description;
                        row.ImageUrl = issue.ImageUrl; row.SiteDetailUrl = issue.SiteDetailUrl;
                        row.FetchedAt = DateTime.UtcNow;
                    }

                var byNumber = issues
                    .Where(i => !string.IsNullOrWhiteSpace(i.IssueNumber))
                    .GroupBy(i => Parse.ComicTitleParser.NormNumber(i.IssueNumber!), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var items = await (from i in db.Items.AsNoTracking()
                                   where i.SeriesId == s.Id && i.Kind == ItemKind.Comic
                                   join d in db.ComicDetails.AsNoTracking() on i.Id equals d.ItemId
                                   select new { i.Id, d.IssueNo }).ToListAsync(ct);

                foreach (var item in items)
                {
                    var key = item.IssueNo == null ? null : Parse.ComicTitleParser.NormNumber(item.IssueNo);
                    var hit = key != null && byNumber.TryGetValue(key, out var issue) ? issue : null;
                    if (hit == null) { noMatch++; }
                    else matched++;
                    if (!apply) continue;

                    var link = await db.ItemProviderLinks.FirstOrDefaultAsync(l => l.ItemId == item.Id && l.Provider == Provider.Cv, ct);
                    if (link == null) { link = new ItemProviderLink { ItemId = item.Id, Provider = Provider.Cv }; db.ItemProviderLinks.Add(link); }
                    link.AttemptCount++;
                    link.AttemptedAt = DateTime.UtcNow;
                    link.SecondaryKey = s.VolumeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    link.Method = "volume-issue-number";
                    if (hit == null) { link.Status = LinkStatus.NoMatch; link.ProviderKey = null; }
                    else { link.Status = LinkStatus.Matched; link.ProviderKey = hit.Id.ToString(System.Globalization.CultureInfo.InvariantCulture); link.Quality = LinkQuality.High; }
                }
                if (apply) await db.SaveChangesAsync(ct);
            }

            var next = series[^1].Id;
            if (apply) { await ComicVineSeriesScraper.WriteAsync(db, CursorKey, next.ToString(System.Globalization.CultureInfo.InvariantCulture), ct); await db.SaveChangesAsync(ct); }
            var remaining = await db.Series.AsNoTracking().CountAsync(s => s.CvVolumeId != null && s.Id > next, ct);
            return new ScrapeBatchResult(series.Count, remaining, next.ToString(System.Globalization.CultureInfo.InvariantCulture), matched, noMatch, 0, failed);
        }

        /// <summary>At most 20 pages (2,000 issues) per volume — a bound, not a limit anyone should hit.</summary>
        private async Task<List<CvIssueDto>> FetchAllIssuesAsync(int volumeId, CancellationToken ct)
        {
            var all = new List<CvIssueDto>();
            for (var page = 0; page < 20; page++)
            {
                var batch = await client.GetVolumeIssuesAsync(volumeId, page * 100, ct);
                all.AddRange(batch);
                if (batch.Count < 100) break;
            }
            return all;
        }
    }

    /// <summary>
    /// The ComicVine-miss fallback leg: Open Library first, Google Books second, into `ExternalWork` plus a
    /// `SeriesKeyLink(Provider=External)`.
    ///
    /// <para>Both are plain public JSON APIs and both are CACHE-FIRST through the same
    /// <see cref="ProviderCacheStore"/>, so the scraper is offline-safe and a test drives it with a fake
    /// handler and no key at all. The subjects the works carry are NOT stored on the hot row: they stay in the
    /// warehouse and reach the facets through <c>books-resolve --tags</c>, which is what keeps the closed
    /// whitelist in one place.</para>
    /// </summary>
    public sealed class ExternalWorkScraper
    {
        public const string CursorKey = "books:extscrape:cursor";
        public const string OpenLibraryBase = "https://openlibrary.org";
        public const string GoogleBooksBase = "https://www.googleapis.com/books/v1";

        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient http;
        private readonly ProviderCacheStore cache;
        private readonly ILogger<ExternalWorkScraper> logger;

        public ExternalWorkScraper(HttpClient http, ProviderCacheStore cache, ILogger<ExternalWorkScraper> logger)
        {
            this.http = http;
            this.cache = cache;
            this.logger = logger;
        }

        public sealed record ExternalHit(string Provider, string ProviderKey, string? Title, string? Authors,
            string? Publisher, int? FirstPublishYear, string? Description, string? CoverImageUrl, string? Isbn, string? InfoUrl,
            string? SubjectsJson = null);

        /// <summary>
        /// The pause between LIVE requests, shared across instances: Open Library asks for about one request a
        /// second and Google Books meters by key, and a 500-key batch with no gate is exactly the burst that
        /// gets a client blocked. Cache hits never wait. A test may lower it.
        /// </summary>
        public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromMilliseconds(1000);
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static DateTime lastRequestUtc = DateTime.MinValue;

        public async Task<ScrapeBatchResult> RunBatchAsync(BooksDb db, int batchSize, bool apply = true, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 500);
            var cursor = await ComicVineSeriesScraper.ReadAsync(db, CursorKey, ct) ?? "";

            // External is the FALLBACK: only keys ComicVine could not place are worth asking about.
            var keys = await (from d in db.ComicDetails.AsNoTracking()
                              where d.ParsedSeriesKey != null && d.ParsedSeriesKey != "" && string.Compare(d.ParsedSeriesKey, cursor) > 0
                              join cv in db.SeriesKeyLinks.AsNoTracking().Where(l => l.Provider == Provider.Cv)
                                  on d.ParsedSeriesKey equals cv.ParsedKey into cvs
                              from cv in cvs.DefaultIfEmpty()
                              join ex in db.SeriesKeyLinks.AsNoTracking().Where(l => l.Provider == Provider.External)
                                  on d.ParsedSeriesKey equals ex.ParsedKey into exs
                              from ex in exs.DefaultIfEmpty()
                              where (cv == null || cv.Status == LinkStatus.NoMatch) && (ex == null || ex.Status == LinkStatus.Pending)
                              select d.ParsedSeriesKey!)
                             .Distinct().OrderBy(k => k).Take(batchSize).ToListAsync(ct);
            if (keys.Count == 0) return new ScrapeBatchResult(0, 0, null, 0, 0, 0, 0);

            var nextWorkId = (await db.ExternalWorks.AsNoTracking().Select(w => (int?)w.Id).MaxAsync(ct) ?? 0) + 1;
            int matched = 0, noMatch = 0, failed = 0;

            foreach (var key in keys)
            {
                ct.ThrowIfCancellationRequested();
                ExternalHit? hit;
                try { hit = await SearchAsync(key, ct); }
                catch (Exception ex) { failed++; logger.LogWarning("external: '{Key}' failed: {Message}", key, ex.Message); continue; }

                var link = apply ? await ExternalLinkAsync(db, key, ct) : null;
                if (link != null) { link.AttemptCount++; link.AttemptedAt = DateTime.UtcNow; }

                if (hit == null)
                {
                    noMatch++;
                    if (link != null) { link.Status = LinkStatus.NoMatch; link.ProviderKey = null; }
                    continue;
                }
                matched++;
                if (!apply) continue;

                var work = await db.ExternalWorks.FirstOrDefaultAsync(w => w.Provider == hit.Provider && w.ProviderKey == hit.ProviderKey, ct);
                if (work == null)
                {
                    work = new ExternalWork { Id = nextWorkId++, Provider = hit.Provider, ProviderKey = hit.ProviderKey };
                    db.ExternalWorks.Add(work);
                }
                work.Title = hit.Title; work.Authors = hit.Authors; work.Publisher = hit.Publisher;
                work.FirstPublishYear = hit.FirstPublishYear; work.Description = hit.Description;
                work.CoverImageUrl = hit.CoverImageUrl; work.Isbn = hit.Isbn; work.InfoUrl = hit.InfoUrl;
                work.FetchedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                link!.Status = LinkStatus.Matched;
                link.ProviderKey = work.Id;
                link.Score = 80;

                // The subjects go to the legs side under the provider key — the row books-resolve --tags folds
                // from. Without it a live match folded no External tags at all.
                try { cache.PutOpenLibraryWork(hit.ProviderKey, hit.Title, hit.SubjectsJson); }
                catch (Exception ex) { logger.LogWarning("external: could not store subjects for '{Key}': {Message}", key, ex.Message); }
            }

            var next = keys[^1];
            if (apply) { await ComicVineSeriesScraper.WriteAsync(db, CursorKey, next, ct); await db.SaveChangesAsync(ct); }
            return new ScrapeBatchResult(keys.Count, -1, next, matched, noMatch, 0, failed);
        }

        /// <summary>Open Library first (richer subjects, no key); Google Books as the second opinion.</summary>
        public async Task<ExternalHit?> SearchAsync(string query, CancellationToken ct = default)
        {
            var ol = await GetAsync($"{OpenLibraryBase}/search.json?limit=5&q={Uri.EscapeDataString(query)}", "ol:" + ComicVineClient.Norm(query), ct);
            if (ol != null && ParseOpenLibrary(ol, query) is ExternalHit olHit) return olHit;
            var gb = await GetAsync($"{GoogleBooksBase}/volumes?maxResults=5&q={Uri.EscapeDataString(query)}", "gb:" + ComicVineClient.Norm(query), ct);
            return gb == null ? null : ParseGoogleBooks(gb, query);
        }

        public static ExternalHit? ParseOpenLibrary(string json, string query)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array) return null;
            var wanted = ComicVineClient.Norm(query);
            foreach (var d in docs.EnumerateArray())
            {
                var title = Str(d, "title");
                if (title == null) continue;
                // Only an unambiguous title match is taken — the fallback leg exists to fill gaps, not to guess.
                var normalized = ComicVineClient.Norm(title);
                if (normalized != wanted && !normalized.Contains(wanted, StringComparison.Ordinal)) continue;
                var key = Str(d, "key");
                if (key == null) continue;
                return new ExternalHit("openlibrary", key, title,
                    d.TryGetProperty("author_name", out var an) && an.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", an.EnumerateArray().Select(a => a.GetString()).Where(a => a != null)) : null,
                    d.TryGetProperty("publisher", out var pub) && pub.ValueKind == JsonValueKind.Array
                        ? pub.EnumerateArray().Select(p => p.GetString()).FirstOrDefault(p => p != null) : null,
                    Int(d, "first_publish_year"), null, null,
                    d.TryGetProperty("isbn", out var isbn) && isbn.ValueKind == JsonValueKind.Array
                        ? isbn.EnumerateArray().Select(i => i.GetString()).FirstOrDefault(i => i != null) : null,
                    OpenLibraryBase + key,
                    StringArrayJson(d, "subject"));
            }
            return null;
        }

        public static ExternalHit? ParseGoogleBooks(string json, string query)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
            var wanted = ComicVineClient.Norm(query);
            foreach (var it in items.EnumerateArray())
            {
                if (!it.TryGetProperty("volumeInfo", out var info)) continue;
                var title = Str(info, "title");
                if (title == null) continue;
                var normalized = ComicVineClient.Norm(title);
                if (normalized != wanted && !normalized.Contains(wanted, StringComparison.Ordinal)) continue;
                var id = Str(it, "id");
                if (id == null) continue;
                var published = Str(info, "publishedDate");
                return new ExternalHit("googlebooks", id, title,
                    info.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", au.EnumerateArray().Select(a => a.GetString()).Where(a => a != null)) : null,
                    Str(info, "publisher"),
                    published != null && int.TryParse(published.AsSpan(0, Math.Min(4, published.Length)), out var y) ? y : null,
                    Str(info, "description"),
                    info.TryGetProperty("imageLinks", out var img) ? Str(img, "thumbnail") : null,
                    null, Str(info, "infoLink"),
                    StringArrayJson(info, "categories"));
            }
            return null;
        }

        private async Task<string?> GetAsync(string url, string cacheKey, CancellationToken ct)
        {
            var cached = cache.Get(Provider.External, cacheKey);
            if (cached != null) return cached;
            await Gate.WaitAsync(ct);
            try
            {
                var wait = lastRequestUtc + MinRequestInterval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "MovieTheater-Books/1.0");
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync(ct);
                cache.Put(Provider.External, cacheKey, body);
                return body;
            }
            finally
            {
                lastRequestUtc = DateTime.UtcNow;
                Gate.Release();
            }
        }

        /// <summary>A JSON array of the string members of <paramref name="name"/> (capped), or null when absent/empty.</summary>
        private static string? StringArrayJson(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var values = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToList();
            return values.Count == 0 ? null : JsonSerializer.Serialize(values);
        }

        private static async Task<SeriesKeyLink> ExternalLinkAsync(BooksDb db, string parsedKey, CancellationToken ct)
        {
            var link = await db.SeriesKeyLinks.FirstOrDefaultAsync(l => l.ParsedKey == parsedKey && l.Provider == Provider.External, ct);
            if (link == null) { link = new SeriesKeyLink { ParsedKey = parsedKey, Provider = Provider.External }; db.SeriesKeyLinks.Add(link); }
            return link;
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    }
}
