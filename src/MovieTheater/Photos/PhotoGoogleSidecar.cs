using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MovieTheater.Photos
{
    /// <summary>
    /// One Takeout item as the archive presents it: a media file, and the per-item JSON sidecar that
    /// belongs to it (docs/photos-plan.md §2.10).
    /// </summary>
    public sealed class PhotoGoogleArchiveItem
    {
        /// <summary>Absolute path to the media file inside the extracted archive.</summary>
        public string FullPath = "";

        /// <summary>Archive-relative path with forward slashes — context for the review list. NOT part
        /// of the identity triple: Takeout reshuffles its own album layout between exports.</summary>
        public string RelativePath = "";

        /// <summary>
        /// The identity name (§2.10's first third). The sidecar's <c>title</c> when this item OWNS its
        /// sidecar, otherwise the name on disk — see <see cref="OwnsSidecar"/> for why that distinction
        /// is load-bearing.
        /// </summary>
        public string FileName = "";

        /// <summary>The name Takeout actually wrote on disk, which may be TRUNCATED.</summary>
        public string DiskFileName = "";

        public long SizeBytes;

        /// <summary>
        /// False when the sidecar was reached through a fallback and therefore describes a DIFFERENT
        /// file as well — an <c>-edited</c> export, or the second half of a live-photo pair. Such an
        /// item must not take the sidecar's <c>title</c> as its own name: the title names the original,
        /// and two rows claiming it would collide on the identity triple whenever the sizes happened to
        /// agree.
        /// </summary>
        public bool OwnsSidecar;

        /// <summary>Null when no sidecar could be found or the one found would not parse. The item is
        /// still an item: it has a name and a size, which is enough to match on.</summary>
        public PhotoGoogleSidecarData? Sidecar;

        /// <summary>How the sidecar was reached — "exact", "title", "paren", "edited", "stem" — or
        /// "none". Counted per run so a quirk that stops working is visible rather than silent.</summary>
        public string SidecarMatch = "none";
    }

    /// <summary>The fields of a Takeout sidecar this pipeline reads (§2.10). Everything else stays in
    /// the verbatim JSON on the row: a second pass must never need the archive back.</summary>
    public sealed class PhotoGoogleSidecarData
    {
        /// <summary>The AUTHORITATIVE original file name. Takeout truncates long names on disk (and
        /// truncates the sidecar's own file name differently again), so this — not the directory entry —
        /// is what an item is called.</summary>
        public string? Title;

        public string? Description;

        /// <summary>photoTakenTime as the TRUE UTC instant it is. Converted to wall clock only when it
        /// is written onto an asset (§2.7); the raw instant is what the identity triple carries.</summary>
        public DateTime? PhotoTakenUtc;

        public double? Latitude;

        public double? Longitude;

        /// <summary>The sidecar exactly as it was on disk. Kept verbatim: the description, the people
        /// list and Google's own url are all in here, and the parsed columns above are re-derivable
        /// from it.</summary>
        public string Json = "";

        /// <summary>Absolute path of the sidecar file, for diagnostics only.</summary>
        public string FullPath = "";
    }

    /// <summary>
    /// Takeout's naming, decoded (docs/photos-plan.md §2.10).
    ///
    /// <para>Google's export is not a clean media tree, and every quirk below is a real one that a naive
    /// <c>&lt;file&gt;.json</c> lookup gets wrong in a way that ends with the pipeline offering to
    /// download photographs the family already owns:</para>
    /// <list type="bullet">
    /// <item><b>The JSON <c>title</c> is the real name.</b> Long file names are truncated on disk (and
    /// the sidecar's own name is truncated by a different rule), so the directory entry is not a stable
    /// identity across two exports of the same library. The title is.</item>
    /// <item><b><c>*.supplemental-metadata.json</c></b> — the newer suffix, itself truncated to fit the
    /// same length budget (<c>.supplemental-met.json</c> and friends are all real).</item>
    /// <item><b><c>-edited</c> variants carry NO sidecar of their own</b> and share the original's.</item>
    /// <item><b>Duplicate names</b> arrive as <c>IMG_0001(1).jpg</c> whose sidecar is
    /// <c>IMG_0001.jpg(1).json</c> — the counter moves to the END, after the extension.</item>
    /// <item><b>A live-photo pair shares one sidecar</b>: <c>IMG_0001.HEIC</c> and <c>IMG_0001.MP4</c>
    /// with a single <c>IMG_0001.HEIC.json</c> between them.</item>
    /// </list>
    ///
    /// <para><b>Nothing here is fatal.</b> A sidecar that will not parse is counted and skipped, and its
    /// media file still becomes an item — an archive is tens of thousands of files written by somebody
    /// else's exporter, and one bad JSON must not end a pass.</para>
    /// </summary>
    public static class PhotoGoogleSidecar
    {
        /// <summary>The newer sidecar suffix, and every truncation of it Takeout emits. Matched by
        /// PREFIX rather than by an exhaustive list, because the length budget that produces the
        /// truncation depends on the media file's own name.</summary>
        private const string SupplementalPrefix = "supplemental";

        /// <summary>
        /// Reads one directory of an extracted archive and pairs its media files with their sidecars.
        /// Directory-scoped on purpose: Takeout keeps an item and its sidecar side by side, so the
        /// pairing never needs an index of the whole archive — which is what keeps the scan pass
        /// bounded per call.
        /// </summary>
        /// <param name="unparseable">Incremented per sidecar that would not parse. Counted, never thrown.</param>
        public static List<PhotoGoogleArchiveItem> ReadDirectory(string directory, string archiveRoot,
            out int unparseable)
        {
            unparseable = 0;
            var items = new List<PhotoGoogleArchiveItem>();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (DirectoryNotFoundException)
            {
                return items;
            }
            catch (UnauthorizedAccessException)
            {
                return items;
            }

            // ── Sidecars first: everything else is matched against what they claim ──
            var sidecars = new List<ParsedSidecar>();
            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase)) continue;
                var parsed = Parse(file, out var malformed);
                if (parsed == null)
                {
                    // Only BROKEN json is counted. An archive is also full of perfectly valid JSON that
                    // is not an item — album manifests, print subscriptions, shared-album comments — and
                    // counting those as failures would make the number useless as a health signal.
                    if (malformed) unparseable++;
                    continue;
                }
                sidecars.Add(parsed);
            }

            var byTarget = new Dictionary<string, ParsedSidecar>(StringComparer.OrdinalIgnoreCase);
            var byTitle = new Dictionary<string, ParsedSidecar>(StringComparer.OrdinalIgnoreCase);
            var byStem = new Dictionary<string, ParsedSidecar>(StringComparer.OrdinalIgnoreCase);
            foreach (var sidecar in sidecars)
            {
                // First writer wins in every map: the directory listing is ordinal-sorted above, so two
                // sidecars claiming one name resolve the same way on every run and on every host.
                if (!byTarget.ContainsKey(sidecar.TargetName)) byTarget[sidecar.TargetName] = sidecar;
                if (sidecar.Data.Title is string title && title.Length > 0 && !byTitle.ContainsKey(title))
                    byTitle[title] = sidecar;
                var stem = Path.GetFileNameWithoutExtension(sidecar.TargetName);
                if (stem.Length > 0 && !byStem.ContainsKey(stem)) byStem[stem] = sidecar;
            }

            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var extension = Path.GetExtension(file);
                if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)) continue;
                // Album metadata, print orders, the archive's own README — never media.
                if (!PhotoFileKinds.TryClassify(extension, out _)) continue;

                var item = new PhotoGoogleArchiveItem
                {
                    FullPath = file,
                    RelativePath = Path.GetRelativePath(archiveRoot, file).Replace('\\', '/'),
                    DiskFileName = name,
                    FileName = name,
                };
                try { item.SizeBytes = new FileInfo(file).Length; }
                catch (IOException) { /* a size we cannot read is 0; the sidecar still identifies it */ }

                var resolved = Resolve(name, byTarget, byTitle, byStem);
                if (resolved.Sidecar != null)
                {
                    item.Sidecar = resolved.Sidecar.Data;
                    item.SidecarMatch = resolved.How;
                    item.OwnsSidecar = resolved.Owns;
                    // The title is the identity ONLY for the file the sidecar is actually about. An
                    // -edited export or a live-photo's video half borrows the sidecar's metadata and
                    // keeps its own name, or the two would collide on §2.10's identity triple.
                    if (resolved.Owns && resolved.Sidecar.Data.Title is string t && t.Length > 0)
                        item.FileName = t;
                }

                items.Add(item);
            }

            return items;
        }

        private readonly struct Resolution
        {
            public Resolution(ParsedSidecar? sidecar, string how, bool owns)
            {
                Sidecar = sidecar;
                How = how;
                Owns = owns;
            }

            public ParsedSidecar? Sidecar { get; }

            public string How { get; }

            public bool Owns { get; }
        }

        /// <summary>
        /// The lookup cascade, cheapest and most certain first. Ownership is granted only by the first
        /// three rungs; the last two reach a sidecar that describes a different file.
        /// </summary>
        private static Resolution Resolve(string name,
            Dictionary<string, ParsedSidecar> byTarget,
            Dictionary<string, ParsedSidecar> byTitle,
            Dictionary<string, ParsedSidecar> byStem)
        {
            if (byTarget.TryGetValue(name, out var exact)) return new Resolution(exact, "exact", true);
            if (byTitle.TryGetValue(name, out var titled)) return new Resolution(titled, "title", true);

            // IMG_0001(1).jpg ↔ IMG_0001.jpg(1).json — the counter moves past the extension.
            var moved = MoveDuplicateCounter(name);
            if (moved != null)
            {
                if (byTarget.TryGetValue(moved, out var paren)) return new Resolution(paren, "paren", true);
                if (byTitle.TryGetValue(moved, out var parenTitle)) return new Resolution(parenTitle, "paren", true);
            }

            // An -edited export shares the original's sidecar and is NOT the original.
            var unedited = StripEditedSuffix(name);
            if (unedited != null)
            {
                if (byTarget.TryGetValue(unedited, out var edited)) return new Resolution(edited, "edited", false);
                if (byTitle.TryGetValue(unedited, out var editedTitle)) return new Resolution(editedTitle, "edited", false);
            }

            // The live-photo pair: same stem, different extension, one sidecar between them.
            var stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length > 0 && byStem.TryGetValue(stem, out var paired)) return new Resolution(paired, "stem", false);

            return new Resolution(null, "none", false);
        }

        /// <summary>
        /// The media file a sidecar's own FILE NAME points at: <c>X.jpg.json</c> → <c>X.jpg</c>,
        /// <c>X.jpg.supplemental-metadata.json</c> → <c>X.jpg</c>, <c>X.jpg(1).json</c> →
        /// <c>X(1).jpg</c>. Public because the pairing rules are worth asserting directly.
        /// </summary>
        public static string TargetFileName(string sidecarFileName)
        {
            var name = sidecarFileName;
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 5);

            // ".supplemental-metadata" and every truncation of it. Stripped by prefix because the
            // truncation point depends on how long the media file's own name was.
            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
            {
                var tail = name.Substring(lastDot + 1);
                var counter = "";
                // A counter may sit after the suffix too: X.jpg.supplemental-metadata(1).json.
                var open = tail.IndexOf('(');
                if (open > 0 && tail.EndsWith(")", StringComparison.Ordinal))
                {
                    counter = tail.Substring(open);
                    tail = tail.Substring(0, open);
                }
                if (tail.StartsWith(SupplementalPrefix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, lastDot) + counter;
            }

            return MoveDuplicateCounter(name) ?? name;
        }

        /// <summary>
        /// <c>X.jpg(1)</c> → <c>X(1).jpg</c> and back again — the transform is its own inverse, which is
        /// why one method serves both directions. Null when the name carries no counter.
        /// </summary>
        private static string? MoveDuplicateCounter(string name)
        {
            // Trailing form: name.ext(1)
            if (name.EndsWith(")", StringComparison.Ordinal))
            {
                var open = name.LastIndexOf('(');
                if (open > 0 && IsCounter(name, open))
                {
                    var head = name.Substring(0, open);
                    var counter = name.Substring(open);
                    var dot = head.LastIndexOf('.');
                    if (dot > 0) return head.Substring(0, dot) + counter + head.Substring(dot);
                }
                return null;
            }

            // Interior form: name(1).ext
            var extDot = name.LastIndexOf('.');
            if (extDot <= 0) return null;
            var stem = name.Substring(0, extDot);
            if (!stem.EndsWith(")", StringComparison.Ordinal)) return null;
            var stemOpen = stem.LastIndexOf('(');
            if (stemOpen <= 0 || !IsCounter(stem, stemOpen)) return null;
            return stem.Substring(0, stemOpen) + name.Substring(extDot) + stem.Substring(stemOpen);
        }

        /// <summary>Only DIGITS in the parentheses count. "Wedding (Copy).jpg" is a file name, not a
        /// Takeout duplicate marker, and rewriting it would invent a pairing.</summary>
        private static bool IsCounter(string value, int openIndex)
        {
            if (value.Length - openIndex < 3) return false;
            for (var i = openIndex + 1; i < value.Length - 1; i++)
                if (!char.IsDigit(value[i])) return false;
            return true;
        }

        /// <summary>The name an <c>-edited</c> export was made from, or null when there is no such
        /// suffix. Only the English marker: a localized archive's marker differs per account language,
        /// and guessing at one would pair unrelated files.</summary>
        private static string? StripEditedSuffix(string name)
        {
            var extension = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            const string marker = "-edited";
            if (!stem.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return null;
            return stem.Substring(0, stem.Length - marker.Length) + extension;
        }

        // ── Parsing ──────────────────────────────────────────────────────────────────────────────

        private sealed class ParsedSidecar
        {
            public string TargetName = "";

            public PhotoGoogleSidecarData Data = new PhotoGoogleSidecarData();
        }

        /// <summary>Null on anything that will not parse as a Takeout item sidecar — album manifests
        /// (<c>metadata.json</c>) included, which are JSON and are not items. <paramref name="malformed"/>
        /// separates "this file is broken" from "this file is not an item".</summary>
        private static ParsedSidecar? Parse(string file, out bool malformed)
        {
            malformed = false;
            string json;
            try { json = File.ReadAllText(file); }
            catch (IOException) { malformed = true; return null; }
            catch (UnauthorizedAccessException) { malformed = true; return null; }

            try { using var probe = JsonDocument.Parse(json); }
            catch (JsonException) { malformed = true; return null; }

            var data = ParseJson(json);
            if (data == null) return null;
            data.FullPath = file;
            return new ParsedSidecar { TargetName = TargetFileName(Path.GetFileName(file)), Data = data };
        }

        /// <summary>
        /// The sidecar body, parsed. Public because the row keeps the JSON VERBATIM (§2.10) and every
        /// later reader — the asset-detail endpoint's description, the review list's dates — re-derives
        /// its fields from that stored string rather than from a second set of columns.
        ///
        /// <para>Null on anything that is not an item sidecar, which deliberately includes an album
        /// manifest: <c>metadata.json</c> is valid JSON with a title and is not a photograph.</para>
        /// </summary>
        public static PhotoGoogleSidecarData? ParseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var rootElement = document.RootElement;
                if (rootElement.ValueKind != JsonValueKind.Object) return null;

                var data = new PhotoGoogleSidecarData { Json = json };
                if (rootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    data.Title = title.GetString();
                if (rootElement.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                {
                    var text = description.GetString();
                    data.Description = string.IsNullOrWhiteSpace(text) ? null : text;
                }

                data.PhotoTakenUtc = ReadTimestamp(rootElement, "photoTakenTime")
                                     ?? ReadTimestamp(rootElement, "creationTime");

                // geoData is what the user (or the phone) set; geoDataExif is what the file carried.
                // Google writes 0/0 for "no location", which is a real point in the Atlantic and must
                // not become one on a family map.
                if (!TryReadGeo(rootElement, "geoData", out var lat, out var lon))
                    TryReadGeo(rootElement, "geoDataExif", out lat, out lon);
                data.Latitude = lat;
                data.Longitude = lon;

                // An album manifest has a title and nothing else that makes it an item. Requiring one of
                // the item-shaped fields keeps those out of the row set entirely.
                var isItem = data.PhotoTakenUtc != null
                             || rootElement.TryGetProperty("photoTakenTime", out _)
                             || rootElement.TryGetProperty("creationTime", out _)
                             || rootElement.TryGetProperty("imageViews", out _);
                if (!isItem) return null;

                return data;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static DateTime? ReadTimestamp(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Object) return null;
            if (!node.TryGetProperty("timestamp", out var stamp)) return null;

            // Takeout writes the epoch seconds as a STRING; a numeric one has been seen too.
            long seconds;
            if (stamp.ValueKind == JsonValueKind.String)
            {
                if (!long.TryParse(stamp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                    return null;
            }
            else if (stamp.ValueKind == JsonValueKind.Number)
            {
                if (!stamp.TryGetInt64(out seconds)) return null;
            }
            else return null;

            // ⚠ Deliberately NOT the video pass's 1990 floor (Phase 5 addendum). That floor exists
            // because a container's UNSET creation_time surfaces as the QuickTime 1904 epoch, and a wall
            // of confidently-dated 1904 clips is a worse lie than an undated shelf. A Takeout sidecar
            // has no such sentinel: photoTakenTime is whatever date the account holds, and a family
            // uploading scanned prints holds real 1950s and 1980s dates — the very dates §2.7's whole
            // scanned-album problem is about. Applying the video floor here would silently discard the
            // most valuable metadata in the archive.
            //
            // What IS refused: a non-positive stamp, the Unix epoch DAY itself (the shape a zeroed
            // field takes), and anything in the future.
            if (seconds <= 0) return null;
            var value = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            if (value.Date == new DateTime(1970, 1, 1)) return null;
            if (value > DateTime.UtcNow.AddDays(2)) return null;
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static bool TryReadGeo(JsonElement root, string property, out double? latitude, out double? longitude)
        {
            latitude = null;
            longitude = null;
            if (!root.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Object) return false;
            if (!node.TryGetProperty("latitude", out var lat) || lat.ValueKind != JsonValueKind.Number) return false;
            if (!node.TryGetProperty("longitude", out var lon) || lon.ValueKind != JsonValueKind.Number) return false;

            var latitudeValue = lat.GetDouble();
            var longitudeValue = lon.GetDouble();
            // Null Island: Google's own "no location" sentinel.
            if (Math.Abs(latitudeValue) < 1e-9 && Math.Abs(longitudeValue) < 1e-9) return false;
            if (latitudeValue < -90 || latitudeValue > 90 || longitudeValue < -180 || longitudeValue > 180) return false;

            latitude = latitudeValue;
            longitude = longitudeValue;
            return true;
        }
    }
}
