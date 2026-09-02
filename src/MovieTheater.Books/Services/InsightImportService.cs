using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Services
{
    /// <summary>What one insight-import batch did.</summary>
    public sealed record InsightImportBatchResult(int Processed, long Remaining, long? NextCursor, int Inserted, int Skipped, int Invalid)
    {
        public bool Done => Processed == 0;
        public override string ToString() =>
            $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\" }}  [insights, inserted: {Inserted}, skipped: {Skipped}, invalid: {Invalid}]";
    }

    /// <summary>
    /// <c>books-insight-import</c> — the v2 LANE for writing <c>Insight</c> + <c>InsightTag</c> rows. Until
    /// 2026-09-01 nothing outside the migration wrote one: the runbook was hand-rolled SQL from a Python
    /// session, with the id bands, the append-only rule and the tag vocabulary enforced by care alone. This
    /// verb enforces them in code and is chunked, resumable and idempotent like every other bulk job here.
    ///
    /// <para><b>Input: JSON Lines</b>, one insight per line:</para>
    /// <code>
    /// { "subject": "series" | "book", "id": 123, "model": "claude-opus-4-1",
    ///   "confidence": "High" | "Medium" | "Low" | "Unknown", "recognized": true,
    ///   "rating": 82, "synopsis": "…", "author": "…", "artist": "…", "yearBegin": 1986, "yearEnd": 1987,
    ///   "maturity": 0 | 1 | 2 | 3,               // books only; a series carries none (its audience tags decide)
    ///   "tags": { "genre": ["superhero"], "audience": ["all-ages"] }   // or [{ "category": "genre", "value": "superhero" }]
    ///   "sourceKey": "optional idempotency key" }
    /// </code>
    ///
    /// <para><b>The rules, enforced:</b> append-only (never an UPDATE of an existing row); ids allocated inside
    /// the bands <see cref="Migration.Units.InsightIds"/> pins (series &lt; <c>BookBase</c>, books in
    /// <c>[BookBase, CloneBase)</c>); <c>Rank</c> from <see cref="Transforms.ModelRank"/> of the model you
    /// actually ran; <c>IsCurrent = 0</c> on insert — <c>books-resolve</c> decides currency; tag categories
    /// from the closed vocabulary (<see cref="TagCategories"/>) and values lowercase-hyphenated; a rating is
    /// 0–100 or absent, never 0-for-unknown; a wrong subject id is <i>invalid</i>, not a silent orphan.</para>
    ///
    /// <para><b>Idempotent by <c>SourceKey</c></b>: the supplied one, else <c>import:{file}#{line}</c>. A row
    /// whose (subject, key) already exists is skipped, so a re-run after a kill inserts only what the kill lost.
    /// The dry run (default) validates and counts; the cursor is the LINE NUMBER and lives in the verb.</para>
    /// </summary>
    public sealed class InsightImportService
    {
        public const int DefaultBatchSize = 30;

        /// <summary>The closed tag vocabulary (categories). Values are free but normalized to lowercase-hyphenated.</summary>
        public static readonly HashSet<string> TagCategories = new(StringComparer.OrdinalIgnoreCase)
            { "genre", "theme", "tone", "setting", "era", "audience", "character-focus", "award", "publisher-context" };

        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        private readonly ILogger<InsightImportService> logger;
        public InsightImportService(ILogger<InsightImportService> logger) => this.logger = logger;

        /// <summary>One parsed line, validated. <see cref="Error"/> set means the line is invalid and is skipped with a reason.</summary>
        public sealed record Parsed(SubjectKind Kind, int SubjectId, string ModelId, Confidence Confidence, bool Recognized,
            int? Rating, string? Synopsis, string? Author, string? Artist, int? YearBegin, int? YearEnd, int? Maturity,
            string SourceKey, List<(string Category, string Value)> Tags, string? Error);

        public async Task<InsightImportBatchResult> RunBatchAsync(BooksDb db, string path, int batchSize, bool apply, long after,
            TextWriter? report = null, CancellationToken ct = default)
        {
            batchSize = Math.Clamp(batchSize, 1, 1_000);
            var lines = await File.ReadAllLinesAsync(path, ct);
            var fileName = Path.GetFileName(path);
            var page = new List<(long Line, string Text)>();
            for (var i = (int)after; i < lines.Length && page.Count < batchSize; i++)
                if (!string.IsNullOrWhiteSpace(lines[i])) page.Add((i + 1, lines[i]));
            if (page.Count == 0) return new InsightImportBatchResult(0, 0, null, 0, 0, 0);

            // Ids are allocated per batch from the band's high-water mark, so a batch never collides with a
            // migrated row or with an earlier batch; both marks are re-read on every batch.
            var seriesNext = Math.Max(1, (await db.Insights.AsNoTracking().Where(n => n.Id < Migration.Units.InsightIds.BookBase).MaxAsync(n => (int?)n.Id, ct) ?? 0) + 1);
            var bookNext = Math.Max(Migration.Units.InsightIds.BookBase,
                (await db.Insights.AsNoTracking().Where(n => n.Id >= Migration.Units.InsightIds.BookBase && n.Id < MigrationContext.CloneBase).MaxAsync(n => (int?)n.Id, ct) ?? 0) + 1);

            int inserted = 0, skipped = 0, invalid = 0;
            foreach (var (lineNo, text) in page)
            {
                ct.ThrowIfCancellationRequested();
                var p = Parse(text, $"import:{fileName}#{lineNo}");
                if (p.Error != null) { invalid++; report?.WriteLine($"{lineNo},invalid,{Csv(p.Error)}"); continue; }

                var exists = p.Kind == SubjectKind.Series
                    ? await db.Series.AsNoTracking().AnyAsync(s => s.Id == p.SubjectId, ct)
                    : await db.Items.AsNoTracking().AnyAsync(i => i.Id == p.SubjectId && i.Kind == ItemKind.Book, ct);
                if (!exists) { invalid++; report?.WriteLine($"{lineNo},invalid,{(p.Kind == SubjectKind.Series ? "series" : "book")} {p.SubjectId} does not exist"); continue; }

                if (await db.Insights.AsNoTracking().AnyAsync(n => n.SubjectKind == p.Kind && n.SubjectId == p.SubjectId && n.SourceKey == p.SourceKey, ct))
                { skipped++; report?.WriteLine($"{lineNo},skipped,already imported ({Csv(p.SourceKey)})"); continue; }

                if (bookNext >= MigrationContext.CloneBase) throw new InvalidOperationException("The book insight id band is exhausted.");
                if (seriesNext >= Migration.Units.InsightIds.BookBase) throw new InvalidOperationException("The series insight id band is exhausted.");
                var id = p.Kind == SubjectKind.Series ? seriesNext++ : bookNext++;

                if (apply)
                {
                    db.Insights.Add(new Insight
                    {
                        Id = id,
                        SubjectKind = p.Kind,
                        SubjectId = p.SubjectId,
                        ModelId = p.ModelId,
                        Rank = Transforms.ModelRank(p.ModelId),
                        Confidence = p.Confidence,
                        Recognized = p.Recognized,
                        Rating = p.Rating,
                        Synopsis = p.Synopsis,
                        Author = p.Author,
                        Artist = p.Artist,
                        YearBegin = p.YearBegin,
                        YearEnd = p.YearEnd,
                        Maturity = p.Kind == SubjectKind.Item ? p.Maturity : null,
                        ReviewFlag = null,
                        SourceKey = p.SourceKey,
                        GeneratedAt = DateTime.UtcNow,
                        IsCurrent = false,
                    });
                    foreach (var (category, value) in p.Tags)
                        db.InsightTags.Add(new InsightTag { InsightId = id, Category = category, Value = value });
                }
                inserted++;
                report?.WriteLine($"{lineNo},{(apply ? "inserted" : "would-insert")},{(p.Kind == SubjectKind.Series ? "series" : "book")} {p.SubjectId} → insight {id} ({p.Tags.Count} tags)");
            }

            if (apply) await db.SaveChangesAsync(ct);   // one commit per batch: a kill loses at most this page

            var next = page[^1].Line;
            var remaining = lines.Skip((int)next).Count(l => !string.IsNullOrWhiteSpace(l));
            logger.LogInformation("insight import batch: processed {N}, inserted {Inserted}, skipped {Skipped}, invalid {Invalid}, remaining {Remaining}",
                page.Count, inserted, skipped, invalid, remaining);
            return new InsightImportBatchResult(page.Count, remaining, next, inserted, skipped, invalid);
        }

        /// <summary>Parse and validate one line. Pure, so a test can drive it without a file.</summary>
        public static Parsed Parse(string line, string defaultSourceKey)
        {
            Parsed Fail(string why) => new(SubjectKind.Item, 0, "", Confidence.Unknown, false, null, null, null, null, null, null, null, defaultSourceKey, [], why);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException ex) { return Fail("not JSON: " + ex.Message); }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) return Fail("not a JSON object");

                var subject = Str(r, "subject")?.ToLowerInvariant();
                var kind = subject switch { "series" => SubjectKind.Series, "book" or "item" => (SubjectKind?)SubjectKind.Item, _ => null };
                if (kind == null) return Fail("subject must be \"series\" or \"book\"");
                var id = Int(r, "id") ?? Int(r, "subjectId");
                if (id is not > 0) return Fail("id must be a positive integer");
                var model = Str(r, "model") ?? Str(r, "modelId");
                if (string.IsNullOrWhiteSpace(model)) return Fail("model is required (the model you actually ran)");

                Confidence confidence;
                if (r.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci))
                    confidence = Enum.IsDefined(typeof(Confidence), ci) ? (Confidence)ci : Confidence.Unknown;
                else if (!Enum.TryParse(Str(r, "confidence") ?? "Unknown", true, out confidence)) return Fail("confidence must be High, Medium, Low or Unknown");

                var rating = Int(r, "rating");
                if (rating is < 0 or > 100) return Fail("rating must be 0–100 or absent");
                if (rating == 0) return Fail("rating 0 is not a rating; omit it when there is no basis");
                var maturity = Int(r, "maturity");
                if (maturity is < 0 or > 3) return Fail("maturity must be 0 (everyone) … 3 (adult)");
                if (kind == SubjectKind.Item && maturity == null) return Fail("a book insight needs maturity — without it the book is invisible to every gated account");
                var yearBegin = Int(r, "yearBegin");
                var yearEnd = Int(r, "yearEnd");
                if (yearBegin is int yb && (yb < 1800 || yb > 2100)) return Fail("yearBegin out of range");
                if (yearEnd is int ye && (ye < 1800 || ye > 2100)) return Fail("yearEnd out of range");

                var tags = new List<(string, string)>();
                if (r.TryGetProperty("tags", out var t))
                {
                    if (t.ValueKind == JsonValueKind.Array)
                        foreach (var e in t.EnumerateArray())
                        {
                            var cat = Str(e, "category"); var val = Str(e, "value");
                            if (cat == null || val == null) return Fail("a tag needs category and value");
                            if (Tag(cat, val, tags) is string err) return Fail(err);
                        }
                    else if (t.ValueKind == JsonValueKind.Object)
                        foreach (var prop in t.EnumerateObject())
                        {
                            if (prop.Value.ValueKind != JsonValueKind.Array) return Fail($"tags.{prop.Name} must be an array of values");
                            foreach (var v in prop.Value.EnumerateArray())
                            {
                                if (v.ValueKind != JsonValueKind.String) return Fail($"tags.{prop.Name} holds a non-string");
                                if (Tag(prop.Name, v.GetString()!, tags) is string err) return Fail(err);
                            }
                        }
                    else return Fail("tags must be an array or an object");
                }

                var sourceKey = Str(r, "sourceKey");
                return new Parsed(kind.Value, id.Value, model.Trim(), confidence, Bool(r, "recognized") ?? true,
                    rating, Blank(Str(r, "synopsis")), Blank(Str(r, "author")), Blank(Str(r, "artist")), yearBegin, yearEnd, maturity,
                    string.IsNullOrWhiteSpace(sourceKey) ? defaultSourceKey : sourceKey.Trim(), tags, null);
            }
        }

        /// <summary>Normalize + validate one tag into the list; returns an error string or null.</summary>
        private static string? Tag(string category, string value, List<(string, string)> into)
        {
            var cat = category.Trim().ToLowerInvariant();
            if (!TagCategories.Contains(cat)) return $"tag category \"{category}\" is not in the vocabulary ({string.Join(", ", TagCategories.OrderBy(x => x))})";
            var val = Normalize(value);
            if (val.Length == 0) return $"empty tag value in {cat}";
            if (!into.Contains((cat, val))) into.Add((cat, val));
            return null;
        }

        /// <summary>lowercase-hyphenated: "Slice of Life" → "slice-of-life".</summary>
        public static string Normalize(string value)
        {
            var s = value.Trim().ToLowerInvariant();
            var chars = s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
            var joined = new string(chars);
            while (joined.Contains("--")) joined = joined.Replace("--", "-");
            return joined.Trim('-');
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        private static bool? Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
