using System;
using System.Globalization;
using System.Text.RegularExpressions;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// Honest date estimation (photos-plan.md §2.7). Everything here is either a fact or a labelled
    /// guess: <c>PhotoAsset.TakenAtSource</c> says which, and no rule ever upgrades its own confidence.
    ///
    /// <para><b>Timezone policy.</b> <c>TakenAt</c> is NAIVE LOCAL WALL-CLOCK, deliberately. EXIF has no
    /// timezone, and a family timeline must group by the clock the moment happened on — "Christmas
    /// morning" must not land on Dec 24 through UTC math. A source that supplies TRUE UTC is converted
    /// to wall-clock through the configured home zone and the raw UTC is kept beside it in
    /// <c>TakenAtUtcRaw</c>, so the conversion stays revisitable. The two representations are never
    /// mixed into one column.</para>
    ///
    /// <para><b>File mtime is never a taken-date.</b> It is recorded for change detection only: copying
    /// a folder resets it, so trusting it would re-date a whole collection on one bad copy.</para>
    /// </summary>
    public static class PhotoDates
    {
        /// <summary>Fallback when <c>PhotosHomeTimeZone</c> is unset or unknown to this host. Windows
        /// and Linux disagree on timezone id spelling; .NET 8 accepts IANA ids on Windows too, so the
        /// IANA spelling is the configured one.</summary>
        public const string DefaultHomeTimeZone = "America/New_York";

        public static TimeZoneInfo ResolveHomeZone(string? configured)
        {
            foreach (var id in new[] { configured, DefaultHomeTimeZone })
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            // Never throw over a timezone: UTC-as-wall-clock is wrong by hours, an exception is wrong by
            // the whole pass. The source label still says the date came from a UTC conversion.
            return TimeZoneInfo.Utc;
        }

        /// <summary>UTC instant → the naive wall-clock <c>TakenAt</c> is defined as (§2.7).</summary>
        public static DateTime ToWallClock(DateTime utc, TimeZoneInfo homeZone) =>
            DateTime.SpecifyKind(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), homeZone),
                DateTimeKind.Unspecified);

        /// <summary>
        /// Scanned-print heuristic (§2.7). A scanner stamps EXIF with the DATE OF THE SCAN, which is
        /// indistinguishable from a capture date by shape alone — a 1987 birthday party carrying a 2019
        /// EXIF date would sort into 2019 forever and nothing downstream could tell. So a suspected
        /// scan never takes EXIF as <see cref="TakenAtSource.Exif"/> confidence; the EXIF value stays in
        /// <c>RawMetadataJson</c> and the date cascade falls through to filename/folder evidence or to
        /// the undated shelf, where the §2.7 dating UI can set it deliberately.
        /// </summary>
        public static bool LooksLikeScan(string relativePath, string? cameraMake, string? cameraModel)
        {
            var maker = $"{cameraMake} {cameraModel}";
            if (maker.Contains("scan", StringComparison.OrdinalIgnoreCase)) return true;
            if (maker.Contains("Epson", StringComparison.OrdinalIgnoreCase)) return true;
            if (maker.Contains("Scanjet", StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var segment in relativePath.Split('/'))
            {
                if (segment.Contains("scan", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // IMG_20140312_101530 / PXL_20210101_120000123 / VID_20140312 / 20140312_101530 — the
        // camera-and-phone convention: 8 date digits, optionally 6 time digits after a separator.
        private static readonly Regex CompactStamp = new(
            @"(?<!\d)(?<y>19\d{2}|20\d{2})(?<m>0[1-9]|1[0-2])(?<d>0[1-9]|[12]\d|3[01])(?:[-_ ]?(?<hh>[01]\d|2[0-3])(?<mi>[0-5]\d)(?<ss>[0-5]\d))?(?!\d)",
            RegexOptions.Compiled);

        // 2010-07-04 / 2010.07.04
        private static readonly Regex IsoDate = new(
            @"(?<!\d)(?<y>19\d{2}|20\d{2})[-.](?<m>0?[1-9]|1[0-2])[-.](?<d>0?[1-9]|[12]\d|3[01])(?!\d)",
            RegexOptions.Compiled);

        // "Overlook 7-4-2010" — month-day-year, the hand-typed folder/file convention in this tree.
        private static readonly Regex UsDate = new(
            @"(?<!\d)(?<m>0?[1-9]|1[0-2])[-./](?<d>0?[1-9]|[12]\d|3[01])[-./](?<y>19\d{2}|20\d{2})(?!\d)",
            RegexOptions.Compiled);

        private static readonly Regex BareYear = new(@"(?<!\d)(?<y>19[3-9]\d|20[0-4]\d)(?!\d)", RegexOptions.Compiled);

        /// <summary>A full date parsed out of a file NAME (§2.7). Null when the name carries no date —
        /// which is most of the collection, and is why this is a fallback rather than a source.</summary>
        public static DateTime? ParseFromFileName(string fileName)
        {
            foreach (var rx in new[] { CompactStamp, IsoDate, UsDate })
            {
                var m = rx.Match(fileName);
                if (!m.Success) continue;
                var y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
                var mo = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
                var d = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
                int hh = 0, mi = 0, ss = 0;
                if (m.Groups["hh"].Success)
                {
                    hh = int.Parse(m.Groups["hh"].Value, CultureInfo.InvariantCulture);
                    mi = int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture);
                    ss = int.Parse(m.Groups["ss"].Value, CultureInfo.InvariantCulture);
                }
                if (d > DateTime.DaysInMonth(y, mo)) continue;
                return new DateTime(y, mo, d, hh, mi, ss, DateTimeKind.Unspecified);
            }
            return null;
        }

        /// <summary>
        /// A year hinted by a folder in the path. Returned as BOUNDS, never as a
        /// <c>TakenAt</c>: a year is not a wall clock, and writing January 1st would pile thousands of
        /// photos onto one day — the "scattered at epoch 0" failure §2.7 exists to prevent, only with a
        /// more convincing date on it. The dating UI (Phase 2) turns a hint into a real date; the item
        /// sits on the undated shelf with its hint until then.
        /// </summary>
        public static int? ParseYearFromFolders(string relativePath)
        {
            var segments = relativePath.Split('/');
            // Nearest folder first: "Vacation 2004/Day 3" should answer 2004, and a top-level bucket
            // should not outrank the folder the photo actually sits in.
            for (var i = segments.Length - 2; i >= 0; i--)
            {
                var m = BareYear.Match(segments[i]);
                if (m.Success) return int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            }
            return null;
        }
    }
}
