using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Migration
{
    /// <summary>
    /// The named value transforms of docs/books/v2-mapping.json (<c>xf:*</c> and <c>enum:*</c> rules), each a
    /// pure function over v1 text/ints. The vocabularies were read off the frozen file (R4 census) — every
    /// spelling seen there is mapped explicitly; anything unseen falls to the enum's Unknown/None member and
    /// is counted by the caller as <c>unmapped</c>, never silently coerced to a real value.
    /// </summary>
    public static class Transforms
    {
        // ── dates ───────────────────────────────────────────────────────────────────────────────

        /// <summary>v1 stores ISO-ish text in three shapes ("2025-02-03 21:39:47", "2026-05-27 05:54:30.6196618",
        /// "2026-05-29T03:38:53.841755+00:00"); offsets are folded to UTC, plain stamps kept as-is.</summary>
        public static DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out var d))
                return DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
            return null;
        }

        // ── enums ───────────────────────────────────────────────────────────────────────────────

        public static ItemKind Kind(int? category) => category == 1 ? ItemKind.Book : ItemKind.Comic;

        public static ContainerFormat Container(string? ext) => (ext ?? "").ToLowerInvariant() switch
        {
            ".cbz" => ContainerFormat.Cbz,
            ".cbr" => ContainerFormat.Cbr,
            ".pdf" => ContainerFormat.Pdf,
            ".epub" => ContainerFormat.Epub,
            ".mobi" => ContainerFormat.Mobi,
            _ => ContainerFormat.Unknown,
        };

        /// <summary>The 33 v1 spellings → 14 members; the raw text is kept beside it (ComicDetail.FormatRaw).</summary>
        public static ComicFormat Format(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "single issue" or "standard" => ComicFormat.SingleIssue,
            "tpb" or "trade paper back" or "soft cover" => ComicFormat.Tpb,
            "hc" or "hard cover" => ComicFormat.Hardcover,
            "omnibus" => ComicFormat.Omnibus,
            "annual" => ComicFormat.Annual,
            "special" or "secret files & origins" or "80-page giant" or "second feature" or "ashcan" or "promo" or "prologue" or "preview" or "minus 1" or "reference" or "rpg" => ComicFormat.Special,
            "one-shot" or "one shot" => ComicFormat.OneShot,
            "graphic novel" or "ogn" => ComicFormat.GraphicNovel,
            "limited series" or "limed series" or "series" => ComicFormat.LimitedSeries,
            "weekly" or "webcomic" => ComicFormat.Weekly,
            "quarterly" => ComicFormat.Magazine,
            "reprint" => ComicFormat.Reprint,
            _ => ComicFormat.Unknown,
        };

        public static Confidence ConfidenceOf(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "high" => Confidence.High,
            "medium" => Confidence.Medium,
            "low" => Confidence.Low,
            _ => Confidence.Unknown,
        };

        public static ParseSource ParseSourceOf(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "metadata" => ParseSource.Metadata,
            "metadataalt" => ParseSource.MetadataAlt,
            "filename" => ParseSource.Filename,
            "filenameleadingindex" => ParseSource.FilenameLeadingIndex,
            "folder" => ParseSource.Folder,
            "volume" => ParseSource.Volume,
            "default" => ParseSource.Default,
            "manual" => ParseSource.Manual,
            _ => ParseSource.None,
        };

        public static ReadingOrderSource ReadingOrderSourceOf(string? s) => (s ?? "").Trim() switch
        {
            "ComicVine" => ReadingOrderSource.ComicVine,
            "Date" => ReadingOrderSource.Date,
            "IssueNo" => ReadingOrderSource.IssueNo,
            "IssueNo+Date" => ReadingOrderSource.IssueNoDate,
            "ClaudeYear" => ReadingOrderSource.ClaudeYear,
            "IssueNo+ClaudeYear" => ReadingOrderSource.IssueNoClaudeYear,
            "Containment" => ReadingOrderSource.Containment,
            _ => ReadingOrderSource.Unordered,
        };

        public static DatePrecision PrecisionOf(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "day" => DatePrecision.Day,
            "month" => DatePrecision.Month,
            "year" => DatePrecision.Year,
            _ => DatePrecision.None,
        };

        public static TrackRole TrackRoleOf(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "container" => TrackRole.Container,
            "alternate" => TrackRole.Alternate,
            _ => TrackRole.Primary,
        };

        public static SpanSource SpanSourceOf(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "inferred" => SpanSource.Inferred,
            "comicvine" => SpanSource.ComicVine,
            "gcd" => SpanSource.Gcd,
            "locg" => SpanSource.Locg,
            "curated" => SpanSource.Curated,
            _ => SpanSource.None,
        };

        /// <summary>The standalone site's ComicvineMatchStatus ints (Pending 0, Matched 1, NoResults 2, MultipleMatches 3,
        /// FetchError 4, Skipped 5, ManuallyMapped 6) — used by the CV item matches and BOTH series-link tables.</summary>
        public static LinkStatus LinkStatusOfCvInt(int? v) => v switch
        {
            1 => LinkStatus.Matched,
            2 => LinkStatus.NoMatch,
            3 => LinkStatus.Multiple,
            4 => LinkStatus.Error,
            5 => LinkStatus.Skip,
            6 => LinkStatus.Manual,
            _ => LinkStatus.Pending,
        };

        /// <summary>Text statuses of the LOCG/GCD/MU/Inducks legs. <c>cleared-*</c> reasons are Cleared; the caller keeps the raw text.</summary>
        public static LinkStatus LinkStatusOfText(string? s)
        {
            var t = (s ?? "").Trim().ToLowerInvariant();
            if (t.StartsWith("cleared", StringComparison.Ordinal)) return LinkStatus.Cleared;
            return t switch
            {
                "matched" => LinkStatus.Matched,
                "nomatch" or "no-match" or "noresults" => LinkStatus.NoMatch,
                "multiple" or "ambiguous" => LinkStatus.Multiple,
                "pending" => LinkStatus.Pending,
                "error" => LinkStatus.Error,
                "manual" => LinkStatus.Manual,
                "skip" or "skipped" => LinkStatus.Skip,
                _ => LinkStatus.Pending,
            };
        }

        public static LinkQuality QualityOf(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "high" or "span-corroborated" => LinkQuality.High,
            "medium" => LinkQuality.Medium,
            "low" => LinkQuality.Low,
            "conflict" => LinkQuality.Conflict,
            _ => LinkQuality.Unknown,
        };

        public static ReadStatus ReadStatusOf(int? v) => v switch { 1 => ReadStatus.InProgress, 2 => ReadStatus.Finished, _ => ReadStatus.Unread };

        public static SubjectKind SubjectOf(string? targetType) =>
            string.Equals(targetType?.Trim(), "series", StringComparison.OrdinalIgnoreCase) ? SubjectKind.Series : SubjectKind.Item;

        /// <summary>MODEL_RANK: file-metadata / openlibrary / calibre-tags / epub-jacket 0, haiku 1, sonnet 2, opus 3, anything else 2.</summary>
        public static int ModelRank(string? modelId)
        {
            var m = (modelId ?? "").ToLowerInvariant();
            if (m is "file-metadata" or "openlibrary" or "calibre-tags" or "epub-jacket") return 0;
            if (m.Contains("haiku")) return 1;
            if (m.Contains("sonnet")) return 2;
            if (m.Contains("opus")) return 3;
            return 2;
        }

        // ── text ────────────────────────────────────────────────────────────────────────────────

        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        public static string NormalizeName(string s) => Spaces.Replace(s.Trim().ToLowerInvariant(), " ");

        /// <summary>Creator/author lists: "A, B; C" and Calibre's "A &amp; B" → distinct trimmed names, order kept.</summary>
        public static List<string> SplitNames(string? s)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in s.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Spaces.Replace(part.Trim(), " ");
                if (name.Length == 0 || !seen.Add(name)) continue;
                list.Add(name);
            }
            return list;
        }

        /// <summary>Genre/tag lists: comma-separated, trimmed, distinct (case-insensitive), order kept.</summary>
        public static List<string> SplitTags(string? s)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = Spaces.Replace(part.Trim(), " ");
                if (tag.Length == 0 || !seen.Add(tag)) continue;
                list.Add(tag);
            }
            return list;
        }

        // ── JSON ────────────────────────────────────────────────────────────────────────────────

        public sealed record Creator(string Role, string Name, string? PeopleId);

        /// <summary>LOCG CreatorsJson: <c>[{"role","name","peopleId"}]</c>; unparseable → empty.</summary>
        public static List<Creator> ParseCreators(string? json)
        {
            var list = new List<Creator>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var name = e.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var role = e.TryGetProperty("role", out var r) ? r.GetString() : null;
                    string? pid = null;
                    if (e.TryGetProperty("peopleId", out var p))
                        pid = p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind == JsonValueKind.Number ? p.GetRawText() : null;
                    list.Add(new Creator(string.IsNullOrWhiteSpace(role) ? "Unknown" : role.Trim(), name.Trim(), pid));
                }
            }
            catch (JsonException) { }
            return list;
        }

        /// <summary>The best candidate score in a CandidatesJson array ("Score" or "score"), for StoredTopScore.</summary>
        public static int? TopScore(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
                int? best = null;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if ((e.TryGetProperty("Score", out var s) || e.TryGetProperty("score", out s)) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var d))
                        best = Math.Max(best ?? int.MinValue, (int)Math.Round(d));
                }
                return best;
            }
            catch (JsonException) { return null; }
        }

        /// <summary>The v1 SystemState fingerprint keys → the DerivedTable registry names they now belong to.</summary>
        public static string? DerivedTableForFingerprint(string key) => key switch
        {
            "series_resolution_fingerprint" or "series_yearspan_fingerprint" => "Series",
            "claude_tagfold_fingerprint" or "external_tagfold_fingerprint" or "mu_tagfold_fingerprint" or "gcd_genrefold_fingerprint" => "ItemTag/SeriesTag(folds)",
            _ => null,
        };
    }
}
