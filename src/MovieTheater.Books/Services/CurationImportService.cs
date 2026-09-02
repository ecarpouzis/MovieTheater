using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Services
{
    /// <summary>What one curation-import batch did.</summary>
    public sealed record CurationBatchResult(int Processed, long Remaining, long? NextCursor, int Applied, int Unchanged, int Invalid)
    {
        public bool Done => Processed == 0;
        public override string ToString() =>
            $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\" }}  [curation, applied: {Applied}, unchanged: {Unchanged}, invalid: {Invalid}]";
    }

    /// <summary>
    /// <c>books-curation-import</c> — the v2 PRODUCER of the hand-curated columns that no parser or provider
    /// fills: <c>ComicDetail.EventName</c> / <c>IssueTitle</c> on an issue and <c>Series.Franchise</c> /
    /// <c>DisplayNameOverride</c> on a series. v1 set EventName and IssueTitle from a CSV import and Franchise
    /// "offline on Series"; v2 carried the migrated values and had no way to add one, so the Events and
    /// Franchise facets could only ossify at the migrated set (the 2026-09-01 port finding).
    ///
    /// <para><b>Input: CSV</b> with a header — <c>kind,id,field,value</c>. <c>kind</c> is <c>item</c> or
    /// <c>series</c>; <c>field</c> is <c>eventName</c> / <c>issueTitle</c> for an item and <c>franchise</c> /
    /// <c>displayName</c> for a series; an empty <c>value</c> CLEARS the column. Quoted values may hold commas.
    /// A wrong id or an unknown field is <i>invalid</i> and reported, never guessed.</para>
    ///
    /// <para>Dry run by default; chunked by LINE with the cursor in the verb; idempotent — a row whose value is
    /// already in place counts as <i>unchanged</i>. Facet consumers read these columns directly, so after an
    /// apply run <c>books-resolve</c> is not required, but the catalog cache is: the host's warmer expires it
    /// when the fingerprint moves, and <c>POST /admin/cache/expire</c> does it now.</para>
    /// </summary>
    public sealed class CurationImportService
    {
        public const int DefaultBatchSize = 500;

        private readonly ILogger<CurationImportService> logger;
        public CurationImportService(ILogger<CurationImportService> logger) => this.logger = logger;

        public sealed record Row(long Line, string Kind, int Id, string Field, string? Value, string? Error);

        public async Task<CurationBatchResult> RunBatchAsync(BooksDb db, string path, int batchSize, bool apply, long after,
            TextWriter? report = null, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            var lines = await File.ReadAllLinesAsync(path, ct);
            if (lines.Length == 0) return new CurationBatchResult(0, 0, null, 0, 0, 0);
            var header = ParseCsv(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
            if (!(header.Count >= 4 && header[0] == "kind" && header[1] == "id" && header[2] == "field" && header[3] == "value"))
                throw new InvalidOperationException("The CSV header must be: kind,id,field,value");

            var start = (int)Math.Max(after, 1);   // line 1 is the header; the cursor is 1-based
            var page = new List<Row>();
            for (var i = start; i < lines.Length && page.Count < batchSize; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                page.Add(ParseRow(i + 1, lines[i]));
            }
            if (page.Count == 0) return new CurationBatchResult(0, 0, null, 0, 0, 0);

            int applied = 0, unchanged = 0, invalid = 0;
            foreach (var row in page)
            {
                ct.ThrowIfCancellationRequested();
                if (row.Error != null) { invalid++; report?.WriteLine($"{row.Line},invalid,{Csv(row.Error)}"); continue; }

                string? current;
                Action<string?> set;
                if (row.Kind == "item")
                {
                    var detail = await db.ComicDetails.FirstOrDefaultAsync(d => d.ItemId == row.Id, ct);
                    if (detail == null) { invalid++; report?.WriteLine($"{row.Line},invalid,item {row.Id} has no comic detail row"); continue; }
                    if (row.Field == "eventname") { current = detail.EventName; set = v => detail.EventName = v; }
                    else { current = detail.IssueTitle; set = v => detail.IssueTitle = v; }
                }
                else
                {
                    var series = await db.Series.FirstOrDefaultAsync(s => s.Id == row.Id, ct);
                    if (series == null) { invalid++; report?.WriteLine($"{row.Line},invalid,series {row.Id} does not exist"); continue; }
                    if (row.Field == "franchise") { current = series.Franchise; set = v => series.Franchise = v; }
                    else { current = series.DisplayNameOverride; set = v => series.DisplayNameOverride = v; }
                }

                if (string.Equals(current, row.Value, StringComparison.Ordinal)) { unchanged++; continue; }
                if (apply) set(row.Value);
                applied++;
                report?.WriteLine($"{row.Line},{(apply ? "applied" : "would-apply")},{row.Kind} {row.Id} {row.Field}: {Csv(current ?? "")} → {Csv(row.Value ?? "")}");
            }

            if (apply) await db.SaveChangesAsync(ct);

            var next = page[^1].Line;
            var remaining = lines.Skip((int)next).Count(l => !string.IsNullOrWhiteSpace(l));
            logger.LogInformation("curation import batch: processed {N}, applied {Applied}, unchanged {Unchanged}, invalid {Invalid}, remaining {Remaining}",
                page.Count, applied, unchanged, invalid, remaining);
            return new CurationBatchResult(page.Count, remaining, next, applied, unchanged, invalid);
        }

        /// <summary>Parse and validate one data line. Pure.</summary>
        public static Row ParseRow(long lineNo, string line)
        {
            var cells = ParseCsv(line);
            if (cells.Count < 4) return new Row(lineNo, "", 0, "", null, "expected 4 columns: kind,id,field,value");
            var kind = cells[0].Trim().ToLowerInvariant();
            if (kind is not ("item" or "series")) return new Row(lineNo, kind, 0, "", null, "kind must be item or series");
            if (!int.TryParse(cells[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return new Row(lineNo, kind, 0, "", null, "id must be a positive integer");
            var field = cells[2].Trim().ToLowerInvariant();
            var ok = kind == "item" ? field is "eventname" or "issuetitle" : field is "franchise" or "displayname";
            if (!ok) return new Row(lineNo, kind, id, field, null, kind == "item" ? "an item field is eventName or issueTitle" : "a series field is franchise or displayName");
            var value = cells[3].Trim();
            return new Row(lineNo, kind, id, field, value.Length == 0 ? null : value, null);
        }

        /// <summary>A small RFC-4180 reader: quoted cells may hold commas and doubled quotes.</summary>
        public static List<string> ParseCsv(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else sb.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            cells.Add(sb.ToString());
            return cells;
        }

        private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
