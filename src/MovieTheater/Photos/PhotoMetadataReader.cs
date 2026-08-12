using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MovieTheater.Db;
using IODirectory = System.IO.Directory;
using MetaDirectory = MetadataExtractor.Directory;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The metadata pass's reader (photos-plan.md §2.5 phase 2): one open of the file, everything
    /// worth knowing taken out of it, and the RAW readout kept verbatim.
    ///
    /// <para><b>Why the raw JSON is persisted (§2.5).</b> Derived scalars can always be recomputed from
    /// the raw directories; re-reading the file off the NAS to get the raw directories back cannot be
    /// made cheap. So the measurement is stored and the derivations stay revisitable — the same rule
    /// that keeps hard-won measurements out of the bin everywhere else in this repo.</para>
    ///
    /// <para>Reads are opened <c>FileShare.ReadWrite</c> and <c>FileAccess.Read</c>: the pipeline never
    /// writes under the collection root (§6), and it must not lock a file another process is using
    /// either.</para>
    /// </summary>
    public static class PhotoMetadataReader
    {
        public sealed class Result
        {
            public int? Width;
            public int? Height;
            /// <summary>EXIF orientation flag as read, 1 when absent. 5–8 mean the stored pixels are
            /// rotated relative to how the photo should be shown.</summary>
            public int Orientation = 1;
            public DateTime? ExifTakenAt;
            /// <summary>A TRUE UTC instant, when the file carried one (GPS date+time). §2.7's only
            /// Phase-1 source of real UTC — everything else EXIF says is already wall-clock.</summary>
            public DateTime? UtcTakenAt;
            public double? GpsLat;
            public double? GpsLon;
            public string? CameraMake;
            public string? CameraModel;
            public string RawJson = "{}";
        }

        public static Result Read(string fullPath)
        {
            var result = new Result();
            IReadOnlyList<MetaDirectory> directories;
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                directories = ImageMetadataReader.ReadMetadata(stream);
            }

            result.RawJson = ToJson(directories);

            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null)
            {
                result.CameraMake = Trim(ifd0.GetDescription(ExifDirectoryBase.TagMake));
                result.CameraModel = Trim(ifd0.GetDescription(ExifDirectoryBase.TagModel));
                if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientation)
                    && orientation >= 1 && orientation <= 8)
                    result.Orientation = orientation;
            }

            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd != null)
            {
                // DateTimeOriginal is the shutter; DateTimeDigitized is when it was written. Prefer the
                // shutter, accept the other, and fall back to IFD0's plain DateTime last — which many
                // editors rewrite, so it is the weakest of the three.
                if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original))
                    result.ExifTakenAt = Naive(original);
                else if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
                    result.ExifTakenAt = Naive(digitized);
            }
            if (result.ExifTakenAt == null && ifd0 != null
                && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var plain))
                result.ExifTakenAt = Naive(plain);

            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            if (gps != null)
            {
                var location = gps.GetGeoLocation();
                // A 0,0 fix is the "no fix" sentinel a great many cameras write, not a photo in the
                // Gulf of Guinea — treating it as a location would put a wrong pin on real photos.
                if (location.HasValue && !location.Value.IsZero)
                {
                    result.GpsLat = location.Value.Latitude;
                    result.GpsLon = location.Value.Longitude;
                }
                result.UtcTakenAt = ReadGpsUtc(gps);
            }

            ReadDimensions(directories, result);
            return result;
        }

        /// <summary>Display dimensions: the stored pixel dimensions with the EXIF orientation applied
        /// (§2.2 — a naive resize ships sideways photos, and a naive Width/Height ships sideways
        /// LAYOUT, which is what the justified grid computes its rows from).</summary>
        public static void ApplyOrientation(Result result)
        {
            if (result.Orientation < 5 || result.Orientation > 8) return;
            var w = result.Width;
            result.Width = result.Height;
            result.Height = w;
        }

        /// <summary>
        /// Fills in whatever the containers reported. Deliberately name-based rather than
        /// directory-type-based: JPEG, PNG, WebP, BMP, TIFF, HEIC and QuickTime each report dimensions
        /// from their own directory type, and enumerating them by type would silently return nothing
        /// for the next format added. EXIF's own PixelXDimension wins when present because it survives
        /// re-wrapping.
        /// </summary>
        private static void ReadDimensions(IReadOnlyList<MetaDirectory> directories, Result result)
        {
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd != null
                && subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew)
                && subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh)
                && ew > 0 && eh > 0)
            {
                result.Width = ew;
                result.Height = eh;
                return;
            }

            foreach (var directory in directories)
            {
                int? w = null, h = null;
                foreach (var tag in directory.Tags)
                {
                    if (w == null && IsWidthTag(tag.Name)) w = ParseLeadingInt(directory.GetDescription(tag.Type));
                    if (h == null && IsHeightTag(tag.Name)) h = ParseLeadingInt(directory.GetDescription(tag.Type));
                }
                if (w > 0 && h > 0)
                {
                    result.Width = w;
                    result.Height = h;
                    return;
                }
            }
        }

        private static bool IsWidthTag(string name) =>
            name.Equals("Image Width", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Exif Image Width", StringComparison.OrdinalIgnoreCase);

        private static bool IsHeightTag(string name) =>
            name.Equals("Image Height", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Exif Image Height", StringComparison.OrdinalIgnoreCase);

        /// <summary>Descriptions come out as "4032 pixels"; take the leading number.</summary>
        private static int? ParseLeadingInt(string? description)
        {
            if (string.IsNullOrEmpty(description)) return null;
            var end = 0;
            while (end < description.Length && char.IsDigit(description[end])) end++;
            if (end == 0) return null;
            return int.TryParse(description.Substring(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v
                : (int?)null;
        }

        /// <summary>
        /// GPS date+time stamps are defined by the EXIF spec as UTC — the one genuinely
        /// timezone-anchored clock in a photo file. Read as UTC and handed to §2.7's conversion; the
        /// EXIF capture time beside it is wall-clock and is NOT converted.
        /// </summary>
        private static DateTime? ReadGpsUtc(GpsDirectory gps)
        {
            var stamp = gps.GetDescription(GpsDirectory.TagDateStamp);
            if (string.IsNullOrWhiteSpace(stamp)) return null;
            // "2014:03:12" is the spec's spelling.
            var date = stamp.Replace(':', '-').Trim();
            if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return null;

            var time = gps.GetDescription(GpsDirectory.TagTimeStamp);
            var hours = 0; var minutes = 0; var seconds = 0;
            if (!string.IsNullOrWhiteSpace(time))
            {
                // "10:15:30.000 UTC"
                var parts = time.Split(' ')[0].Split(':');
                if (parts.Length >= 3)
                {
                    int.TryParse(parts[0], out hours);
                    int.TryParse(parts[1], out minutes);
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s);
                    seconds = (int)s;
                }
            }
            if (hours > 23 || minutes > 59 || seconds > 59) return null;
            return new DateTime(day.Year, day.Month, day.Day, hours, minutes, seconds, DateTimeKind.Utc);
        }

        private static DateTime? Naive(DateTime value) =>
            value == default ? (DateTime?)null : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

        private static string? Trim(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// The raw readout as <c>{ "Directory Name": { "Tag Name": "description" } }</c>. Descriptions
        /// rather than raw byte arrays: a thumbnail or maker-note blob would balloon the column for
        /// nothing, and every scalar the pipeline derives comes from the description form anyway.
        /// </summary>
        private static string ToJson(IReadOnlyList<MetaDirectory> directories)
        {
            var payload = new Dictionary<string, Dictionary<string, string>>();
            foreach (var directory in directories)
            {
                var tags = new Dictionary<string, string>();
                foreach (var tag in directory.Tags)
                {
                    var description = directory.GetDescription(tag.Type);
                    if (string.IsNullOrEmpty(description)) continue;
                    // Binary payloads (thumbnails, maker notes, ICC blobs) describe as long hex/base64
                    // strings; they are not facts about the photo, only bytes about the file.
                    if (description!.Length > 512) continue;
                    tags[tag.Name] = description;
                }
                if (tags.Count == 0) continue;
                var name = directory.Name;
                // Some containers emit several directories of the same name (multiple IFDs).
                var unique = name;
                var n = 2;
                while (payload.ContainsKey(unique)) unique = name + " #" + n++;
                payload[unique] = tags;
            }
            foreach (var error in directories.SelectMany(d => d.Errors).Take(5))
            {
                if (!payload.ContainsKey("Errors")) payload["Errors"] = new Dictionary<string, string>();
                payload["Errors"]["error" + payload["Errors"].Count] = error;
            }
            return JsonSerializer.Serialize(payload);
        }
    }
}
