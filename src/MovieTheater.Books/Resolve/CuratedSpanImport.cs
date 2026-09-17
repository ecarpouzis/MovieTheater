using System.Globalization;
using System.Text.Json;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// The pure half of <c>books-curated-spans-import</c>: how one line of the model pass's JSONL becomes (or
    /// fails to become) a <c>CollectedEditionSpan(Source = Curated)</c>.
    ///
    /// <para><b>Why a model writes Curated rows.</b> The three provider producers can only speak when a provider
    /// HOLDS the edition — 12,543 of our 20,498 collected editions have no provider span at all, and thousands
    /// more carry a span that is a known artefact (identical ranges repeated across every volume of a shelf,
    /// GCD's degenerate #N-#N, LOCG ranges truncated to the scraped subset). For those the evidence that decides
    /// the range is on the shelf itself: the label, the explicit range in the file name, the page arithmetic, the
    /// years, and the other editions of the same series tiling the run. A model reading the series folder is what
    /// produces those answers, and `Curated` is where a human-grade judgement belongs — the precedence already
    /// puts it first.</para>
    ///
    /// <para><b>The gold rows are not overwritten.</b> 1,047 Curated rows came from v1 quoting an edition's own
    /// indicia; they carry a higher confidence than this pass is allowed to claim, so a model answer replaces a
    /// Curated row only at equal or greater confidence. When it disagrees with a gold row the gold row stays and
    /// the disagreement is FLAGGED — that is a question for Eric, not a write.</para>
    /// </summary>
    public static class CuratedSpanImport
    {
        /// <summary>The lowest confidence this pass may assert as a span: below it the answer is `unknown`.</summary>
        public const double MinConfidence = 0.55;

        /// <summary>One parsed JSONL line. `Error` non-null means the line is unusable and nothing else is set.</summary>
        public sealed record Line
        {
            public int LineNo { get; init; }
            public int ItemId { get; init; }
            public double? Start { get; init; }
            public double? End { get; init; }
            public string? EditionTitle { get; init; }
            public double Confidence { get; init; }
            public string? Rationale { get; init; }
            public bool Unknown { get; init; }
            public string? Why { get; init; }
            public string? Batch { get; init; }
            public string? Error { get; init; }
        }

        /// <summary>The Curated row an item already carries, if any.</summary>
        public sealed record Existing(double? Start, double? End, double? Confidence, string? ProviderRef,
                                      string? Note = null)
        {
            /// <summary>A row this pass did not write — what used to be called gold.</summary>
            public bool IsGold => ProviderRef is null || !ProviderRef.StartsWith("model:", StringComparison.Ordinal);

            /// <summary>
            /// The row can point at the book's own words for its range: its note quotes an indicia naming
            /// exactly the issues it claims. THIS is what makes a stored row hard to displace, and the label
            /// is not — gold confirms at 47.8% against issue-level truth. A row typed by hand in the review
            /// screen is a person's answer and outranks a file read the same way.
            ///
            /// <para>An <c>identity:</c> row joins them (Eric's ruling, 2026-09-16). It is the identity pass's
            /// answer, read off the whole provider packet for that shelf — the higher-grade read — and every
            /// landing re-runs <c>pass2</c> into this verb, so without this a model line at equal-or-greater
            /// confidence would overwrite the range a reader wrote minutes earlier, and an <c>unknown</c> line
            /// would retract it. The correct data has to persist; a disagreement is flagged, not written.</para>
            /// </summary>
            public bool IsProven =>
                (ProviderRef?.StartsWith("admin:", StringComparison.Ordinal) ?? false)
                || (ProviderRef?.StartsWith("identity:", StringComparison.Ordinal) ?? false)
                || SpanEvidence.SelfProving(Note, Start ?? 0, End ?? -1);
        }

        public enum Verdict
        {
            /// <summary>Write the span (insert, or replace an equal-or-lower-confidence Curated row).</summary>
            Write,
            /// <summary>The model declined to answer — counted, never written.</summary>
            Unknown,
            /// <summary>
            /// The model declined, AND a row this pass wrote earlier is standing there. Delete it. A span that
            /// has been withdrawn must actually go away, or an audit that corrects the pass could only ever add.
            /// Gold is never retracted this way — only rows whose ProviderRef says <c>model:</c>.
            /// </summary>
            Retract,
            /// <summary>An existing Curated row outranks the model's answer and stays.</summary>
            Kept,
            /// <summary>The line cannot be turned into a span.</summary>
            Invalid,
        }

        public static Line Parse(string json, int lineNo)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                if (o.ValueKind != JsonValueKind.Object) return new Line { LineNo = lineNo, Error = "not an object" };
                if (!o.TryGetProperty("itemId", out var idEl) || !idEl.TryGetInt32(out var itemId))
                    return new Line { LineNo = lineNo, Error = "missing itemId" };

                var unknown = o.TryGetProperty("unknown", out var u)
                    && (u.ValueKind == JsonValueKind.True || (u.ValueKind == JsonValueKind.Number && u.GetDouble() != 0));

                return new Line
                {
                    LineNo = lineNo,
                    ItemId = itemId,
                    Unknown = unknown,
                    Why = Str(o, "why"),
                    Start = Num(o, "start"),
                    End = Num(o, "end"),
                    EditionTitle = Str(o, "editionTitle"),
                    Confidence = Num(o, "confidence") ?? 0,
                    Rationale = Str(o, "rationale"),
                    Batch = Str(o, "batch"),
                };
            }
            catch (JsonException e)
            {
                return new Line { LineNo = lineNo, Error = "bad json: " + e.Message };
            }
        }

        /// <summary>
        /// What to do with one line. `flag` is non-null when Eric should look: a malformed line, or a model
        /// answer that contradicts a Curated row it is not allowed to overwrite.
        /// </summary>
        public static Verdict Decide(Line line, Existing? existing, out string? flag, out string? detail)
        {
            flag = null;
            detail = null;
            if (line.Error != null)
            {
                flag = "invalid-span";
                detail = line.Error;
                return Verdict.Invalid;
            }
            if (line.Unknown)
            {
                if (existing is { } prior && !prior.IsProven)
                {
                    flag = "span-retracted";
                    detail = $"withdrew the model's own #{Fmt(prior.Start)}-{Fmt(prior.End)}: {line.Why}";
                    return Verdict.Retract;
                }
                return Verdict.Unknown;
            }

            if (line.Start is not { } s || line.End is not { } e)
            {
                flag = "invalid-span";
                detail = "a span line needs both start and end";
                return Verdict.Invalid;
            }
            if (s < 0 || e < s)
            {
                flag = "invalid-span";
                detail = $"range {Fmt(s)}-{Fmt(e)} is not ascending";
                return Verdict.Invalid;
            }
            if (line.Confidence < MinConfidence || line.Confidence > 1)
            {
                flag = "invalid-span";
                detail = $"confidence {Fmt(line.Confidence)} outside [{Fmt(MinConfidence)}, 1] — say unknown instead";
                return Verdict.Invalid;
            }

            if (existing == null) return Verdict.Write;

            var differs = existing.Start != s || existing.End != e;
            if (line.Confidence >= (existing.Confidence ?? 0))
            {
                if (existing.IsProven && !differs)
                    // The same range, from a lesser read. Rewriting it would replace the row's ProviderRef
                    // and Note with this pass's — demoting an `admin:`/`identity:`/quoted row to `model:`
                    // and making it overwritable next time. Nothing would be gained: the range is already
                    // there. So the row stands, untouched and still proven (Eric, 2026-09-16).
                    return Verdict.Kept;
                if (differs && existing.IsProven)
                {
                    // Equal confidence never displaces a PROVEN row — one whose note quotes the book naming
                    // exactly these issues, or one a person typed. It is the quotation that holds the line,
                    // not the provenance: the four Hellboy omnibus rows that named the wrong volumes were all
                    // "gold", and none of them quotes a number at all.
                    flag = "provider-disagrees";
                    detail = $"kept curated {Fmt(existing.Start)}-{Fmt(existing.End)} (conf {Fmt(existing.Confidence)}); "
                           + $"model said {Fmt(s)}-{Fmt(e)} (conf {Fmt(line.Confidence)})";
                    return Verdict.Kept;
                }
                return Verdict.Write;
            }

            if (differs)
            {
                flag = "provider-disagrees";
                detail = $"kept curated {Fmt(existing.Start)}-{Fmt(existing.End)} (conf {Fmt(existing.Confidence)}); "
                       + $"model said {Fmt(s)}-{Fmt(e)} (conf {Fmt(line.Confidence)})";
            }
            return Verdict.Kept;
        }

        private static double? Num(JsonElement o, string name) =>
            o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;

        private static string? Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? (el.GetString() is { Length: > 0 } v ? v : null)
                : null;

        public static string Fmt(double? d) =>
            d is not { } v ? "?" : v % 1 == 0 ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
