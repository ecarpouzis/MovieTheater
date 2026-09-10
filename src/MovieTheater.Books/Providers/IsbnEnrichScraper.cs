using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Providers
{
    /// <summary>
    /// <c>books-isbn-enrich</c> — subjects for NOVELS, fetched by ISBN.
    ///
    /// <para><b>Why a second external leg.</b> <see cref="ExternalWorkScraper"/> asks Open Library a
    /// free-text TITLE question and takes only an unambiguous match, which is the right shape for a comic
    /// series whose only identity is a parsed name. A novel is not in that position: 90,962 of them carry a
    /// real ISBN out of Calibre, and an ISBN is an exact key — there is no matching to get wrong, no
    /// normalized-title compare, and no "multiple" verdict. So this leg trades the search endpoint for the
    /// BIBKEY endpoint and asks about a hundred books per request instead of one.</para>
    ///
    /// <para><b>What it is for.</b> The library's tag coverage is the thing standing between it and any
    /// content filtering that works: 119,204 books carry no classification at all, Calibre itself has
    /// subjects for only 14,470 of 136,367, and the default "not adult-romance" chip on <c>/books/novels</c>
    /// therefore hides 204 books out of 125,262. Open Library knows the rest by ISBN.</para>
    ///
    /// <para><b>It writes the warehouse only.</b> Rows land in legs <c>OpenLibraryEdition</c>, keyed by the
    /// normalized ISBN; nothing in the hot file changes until <c>books-resolve --tags</c> folds them through
    /// <see cref="LegsTagFoldJob.FoldExternalBooksPage"/>. That separation is what lets the fetch be re-run,
    /// interrupted and resumed without any risk to the catalog.</para>
    ///
    /// <para><b>Chunked, resumable, idempotent.</b> The cursor is <c>Item.Id</c> in <c>SystemState</c>, the
    /// same ordering the batch query uses. A book whose ISBN the warehouse already holds is skipped without a
    /// request, and every ANSWER — including "Open Library does not know this ISBN" — is cached under
    /// <c>olisbn:{isbn}</c>, so a second full pass costs no network at all. The loop that repeats batches lives
    /// in the caller.</para>
    /// </summary>
    public sealed class IsbnEnrichScraper
    {
        public const string CursorKey = "books:isbnenrich:cursor";
        public const string OpenLibraryBase = "https://openlibrary.org";

        /// <summary>
        /// ISBNs per request. Open Library's bibkeys endpoint takes a list; a hundred keeps the URL well inside
        /// any sane limit and turns ~85,000 outstanding books into ~850 requests rather than 85,000.
        /// </summary>
        public const int BibkeysPerRequest = 100;

        private readonly HttpClient http;
        private readonly ProviderCacheStore cache;
        private readonly ILogger<IsbnEnrichScraper> logger;

        public IsbnEnrichScraper(HttpClient http, ProviderCacheStore cache, ILogger<IsbnEnrichScraper> logger)
        {
            this.http = http;
            this.cache = cache;
            this.logger = logger;
        }

        /// <summary>The pause between LIVE requests. Open Library asks for roughly one a second.</summary>
        public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromMilliseconds(1000);

        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static DateTime lastRequestUtc = DateTime.MinValue;

        /// <summary>What one batch did — the whole observability contract, printed by the verb per chunk.</summary>
        public sealed record IsbnBatchResult(
            int Processed, long Remaining, long? NextCursor, int Fetched, int NoMatch, int AlreadyHeld, int Failed,
            int WouldFetch = 0)
        {
            public bool Done => Processed == 0 || NextCursor == null;

            public override string ToString() =>
                $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: {NextCursor}, failed: {Failed} }}" +
                $"  [fetched: {Fetched}, noMatch: {NoMatch}, alreadyHeld: {AlreadyHeld}" +
                (WouldFetch > 0 ? $", wouldFetch: {WouldFetch}" : "") + "]";
        }

        public async Task<IsbnBatchResult> RunBatchAsync(
            BooksDb db, int batchSize, bool apply = true, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            var cursor = long.TryParse(await ReadAsync(db, CursorKey, ct), out var c) ? c : 0;

            var page = await (from i in db.Items.AsNoTracking()
                              join b in db.BookDetails.AsNoTracking() on i.Id equals b.ItemId
                              where i.Kind == ItemKind.Book && !i.IsExcluded
                                    && b.Isbn != null && b.Isbn != "" && i.Id > cursor
                              orderby i.Id
                              select new { i.Id, b.Isbn })
                             .Take(batchSize).ToListAsync(ct);

            if (page.Count == 0)
            {
                var none = await Remaining(db, cursor, ct);
                return new IsbnBatchResult(0, none, null, 0, 0, 0, 0);
            }

            var nextCursor = page[^1].Id;

            // What the warehouse already answers for. Read once per batch: it is a single indexed column scan
            // over a table that is, at its largest, one row per ISBN this library has ever looked up.
            var held = cache.OpenLibraryEditionIsbns();

            var wanted = new List<string>();
            var alreadyHeld = 0;
            foreach (var row in page)
            {
                var key = TagFolds.NormalizeIsbn(row.Isbn);
                if (key == null) continue;             // a mangled ISBN field is not an identifier
                if (held.Contains(key)) { alreadyHeld++; continue; }
                if (!wanted.Contains(key)) wanted.Add(key);
            }

            int fetched = 0, noMatch = 0, failed = 0;

            // A DRY RUN OPENS NO SOCKET. It reports what the pass would cost — how many books are in scope, how
            // many the warehouse already answers for, and how many ISBNs would actually be asked about — and it
            // does not advance the cursor, so the caller runs exactly one chunk of it.
            // Every answer this batch already has — one connection, one pass, rather than one open per key.
            var answered = cache.GetMany(Provider.External, wanted.Select(CacheKey).ToList());

            if (!apply)
            {
                var outstanding = await Remaining(db, cursor, ct);
                return new IsbnBatchResult(
                    page.Count, outstanding, nextCursor, 0, 0, alreadyHeld, 0, wanted.Count - answered.Count);
            }

            var newAnswers = new List<(string, string)>();
            var editions = new List<ProviderCacheStore.OpenLibraryEditionRow>();

            foreach (var chunk in Chunk(wanted, BibkeysPerRequest))
            {
                ct.ThrowIfCancellationRequested();

                // Anything already answered — a hit OR a miss — is served from the response cache, so only the
                // genuinely unknown ISBNs reach the network.
                var unanswered = chunk.Where(k => !answered.ContainsKey(CacheKey(k))).ToList();
                var live = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (unanswered.Count > 0)
                {
                    try
                    {
                        live = await FetchAsync(unanswered, ct);
                    }
                    catch (Exception ex)
                    {
                        failed += unanswered.Count;
                        logger.LogWarning("isbn-enrich: batch of {Count} failed: {Message}", unanswered.Count, ex.Message);
                        continue;
                    }

                    foreach (var key in unanswered)
                        // A miss is recorded as an EMPTY answer, not left blank: without it every re-run
                        // re-asks Open Library about the same ISBNs it has already said it does not know.
                        newAnswers.Add((CacheKey(key), live.TryGetValue(key, out var hit) ? hit.GetRawText() : "{}"));
                }

                foreach (var key in chunk)
                {
                    JsonElement doc;
                    JsonDocument? parsed = null;
                    if (live.TryGetValue(key, out var fresh)) doc = fresh;
                    else
                    {
                        if (!answered.TryGetValue(CacheKey(key), out var cached)
                            || string.IsNullOrWhiteSpace(cached) || cached == "{}") { noMatch++; continue; }
                        parsed = JsonDocument.Parse(cached);
                        doc = parsed.RootElement;
                    }

                    var edition = Parse(doc);
                    parsed?.Dispose();
                    if (edition == null) { noMatch++; continue; }
                    fetched++;
                    editions.Add(new ProviderCacheStore.OpenLibraryEditionRow(
                        key, edition.Title, edition.AuthorsJson, edition.Publishers, edition.PublishDate,
                        edition.Pages, edition.SubjectsJson, edition.CoverUrl, edition.OlEditionKey, edition.OlWorkKey));
                }
            }

            // Both warehouse writes land as one transaction each, AFTER the network work — so an interrupted
            // batch leaves the warehouse consistent and the cursor un-advanced, and the re-run redoes it.
            try
            {
                cache.PutMany(Provider.External, newAnswers);
                cache.PutOpenLibraryEditions(editions);
            }
            catch (Exception ex)
            {
                failed += editions.Count;
                fetched = 0;
                logger.LogWarning("isbn-enrich: could not write the warehouse batch: {Message}", ex.Message);
            }

            // The cursor commits with the batch's work, so a kill costs at most one chunk.
            await WriteAsync(db, CursorKey, nextCursor.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            await db.SaveChangesAsync(ct);

            var remaining = await Remaining(db, nextCursor, ct);
            logger.LogInformation(
                "isbn-enrich batch: processed {Processed}, fetched {Fetched}, noMatch {NoMatch}, alreadyHeld {Held}, failed {Failed}, remaining {Remaining}, nextCursor {Cursor}",
                page.Count, fetched, noMatch, alreadyHeld, failed, remaining, nextCursor);

            return new IsbnBatchResult(page.Count, remaining, nextCursor, fetched, noMatch, alreadyHeld, failed);
        }

        private static Task<long> Remaining(BooksDb db, long cursor, CancellationToken ct) =>
            (from i in db.Items.AsNoTracking()
             join b in db.BookDetails.AsNoTracking() on i.Id equals b.ItemId
             where i.Kind == ItemKind.Book && !i.IsExcluded && b.Isbn != null && b.Isbn != "" && i.Id > cursor
             select i.Id).LongCountAsync(ct);

        internal static string CacheKey(string normalizedIsbn) => "olisbn:" + normalizedIsbn;

        /// <summary>One bibkeys request, unwrapped into <c>{ normalized ISBN → its record }</c>.</summary>
        private async Task<Dictionary<string, JsonElement>> FetchAsync(IReadOnlyList<string> isbns, CancellationToken ct)
        {
            var bibkeys = string.Join(",", isbns.Select(i => "ISBN:" + i));
            var url = $"{OpenLibraryBase}/api/books?bibkeys={Uri.EscapeDataString(bibkeys)}&format=json&jscmd=data";

            await Gate.WaitAsync(ct);
            string body;
            try
            {
                var wait = lastRequestUtc + MinRequestInterval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "MovieTheater-Books/1.0");
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Open Library returned {(int)response.StatusCode}");
                body = await response.Content.ReadAsStringAsync(ct);
            }
            finally
            {
                lastRequestUtc = DateTime.UtcNow;
                Gate.Release();
            }

            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            using var parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in parsed.RootElement.EnumerateObject())
            {
                // Keys come back as "ISBN:0002246163" — exactly what was asked, so the prefix is stripped and
                // re-normalized rather than trusted.
                var key = TagFolds.NormalizeIsbn(prop.Name.StartsWith("ISBN:", StringComparison.Ordinal)
                    ? prop.Name[5..]
                    : prop.Name);
                if (key != null) result[key] = prop.Value.Clone();
            }
            return result;
        }

        public sealed record OlEdition(
            string? Title, string? AuthorsJson, string? Publishers, string? PublishDate, int? Pages,
            string? SubjectsJson, string? CoverUrl, string? OlEditionKey, string? OlWorkKey);

        /// <summary>
        /// One <c>jscmd=data</c> record → the warehouse row. Everything is optional; a record with no subjects
        /// AND no title is not worth a row, because subjects are the only thing anything downstream reads.
        /// </summary>
        public static OlEdition? Parse(JsonElement doc)
        {
            if (doc.ValueKind != JsonValueKind.Object) return null;

            var title = Str(doc, "title");
            var subjects = NamedArrayJson(doc, "subjects");
            if (title == null && subjects == null) return null;

            var authors = doc.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Array
                ? au.EnumerateArray().Select(a => Str(a, "name")).Where(a => a != null).ToList()
                : [];
            var publishers = doc.TryGetProperty("publishers", out var pu) && pu.ValueKind == JsonValueKind.Array
                ? string.Join(", ", pu.EnumerateArray().Select(p => Str(p, "name")).Where(p => p != null))
                : null;
            var cover = doc.TryGetProperty("cover", out var cv) && cv.ValueKind == JsonValueKind.Object
                ? Str(cv, "large") ?? Str(cv, "medium") ?? Str(cv, "small")
                : null;

            return new OlEdition(
                title,
                authors.Count == 0 ? null : JsonSerializer.Serialize(authors),
                string.IsNullOrWhiteSpace(publishers) ? null : publishers,
                Str(doc, "publish_date"),
                doc.TryGetProperty("number_of_pages", out var np) && np.ValueKind == JsonValueKind.Number
                    && np.TryGetInt32(out var pages) ? pages : null,
                subjects,
                cover,
                Str(doc, "key"),
                // jscmd=data does not carry the WORK key; the fold joins on the ISBN, so nothing needs it.
                null);
        }

        /// <summary>A JSON array of the <c>name</c> members of an Open Library object list, capped like the
        /// series leg's own subject list so one over-tagged edition cannot dominate the fold.</summary>
        private static string? NamedArrayJson(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var values = arr.EnumerateArray().Select(x => Str(x, "name")).Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToList();
            return values.Count == 0 ? null : JsonSerializer.Serialize(values);
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
        {
            for (var i = 0; i < source.Count; i += size)
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }

        private static async Task<string?> ReadAsync(BooksDb db, string key, CancellationToken ct) =>
            (await db.SystemStates.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

        private static async Task WriteAsync(BooksDb db, string key, string value, CancellationToken ct)
        {
            var row = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (row == null) db.SystemStates.Add(new SystemState { Key = key, Value = value });
            else row.Value = value;
        }
    }
}
