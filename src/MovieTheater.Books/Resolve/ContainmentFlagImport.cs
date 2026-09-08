using System.Globalization;
using System.Text;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// The pure half of <c>books-containment-flags-import</c>: one row of the containment pass's flags CSV
    /// becomes (or fails to become) a <c>ContainmentFlag</c>.
    ///
    /// <para>The model pass answers 20,498 collected editions, and 708 of those answers are "a person should
    /// look at this": a shelf whose relaunch ladders all number from #1, a 24-page single issue wearing a
    /// <c>tpb</c> format, two rips of one volume, a provider row whose title names a different book. Those are
    /// not failures of the pass — they are the cases where the shelf itself is ambiguous, and the file
    /// de-duplication must not act on them until someone has looked.</para>
    ///
    /// <para>A flag is keyed <c>(ItemId, Flag)</c>, so re-importing an edited sheet updates the detail in place
    /// and never duplicates a row. A flag a person has already decided keeps its verdict: re-import refreshes
    /// the evidence, not the answer.</para>
    /// </summary>
    public static class ContainmentFlagImport
    {
        public const string Pending = "Pending";
        public const string Accepted = "Accepted";
        public const string Dismissed = "Dismissed";

        /// <summary>The flag names the pass emits. An unknown name is not an error — the sheet may grow.</summary>
        public static readonly string[] KnownFlags =
        [
            "overlap-in-series", "conflated-series", "label-ambiguous",
            "provider-disagrees", "arithmetic-odd", "duplicate-edition", "span-retracted",
        ];

        public sealed record Row
        {
            public int LineNo { get; init; }
            public int ItemId { get; init; }
            public int? SeriesId { get; init; }
            public string Flag { get; init; } = "";
            public string Detail { get; init; } = "";
            public string? Error { get; init; }
        }

        /// <summary>
        /// Split one CSV line. Hand-rolled because the sheet is ours and tiny, and because a dependency for
        /// six columns is not worth it — quotes are doubled inside quotes, everything else is literal.
        /// </summary>
        public static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c != '"') { sb.Append(c); continue; }
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; continue; }
                    quoted = false;
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields;
        }

        /// <summary>
        /// Parse a row of <c>itemId,seriesId,series,flag,detail,file</c>. The series name and file name are in
        /// the sheet for the human reading it; the database joins them back from Item, so they are ignored here.
        /// </summary>
        public static Row Parse(string line, int lineNo)
        {
            var f = SplitCsv(line);
            if (f.Count < 4) return new Row { LineNo = lineNo, Error = $"expected at least 4 columns, got {f.Count}" };
            if (!int.TryParse(f[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
                return new Row { LineNo = lineNo, Error = $"itemId '{f[0]}' is not a number" };

            int? seriesId = null;
            if (int.TryParse(f[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid)) seriesId = sid;

            var flag = f[3].Trim();
            if (flag.Length == 0) return new Row { LineNo = lineNo, Error = "flag is empty" };

            return new Row
            {
                LineNo = lineNo,
                ItemId = itemId,
                SeriesId = seriesId,
                Flag = flag,
                Detail = (f.Count > 4 ? f[4] : "").Trim(),
            };
        }

        /// <summary>True when a line is the sheet's header rather than a flag.</summary>
        public static bool IsHeader(string line) =>
            line.TrimStart('﻿').StartsWith("itemId,", StringComparison.OrdinalIgnoreCase);
    }
}
