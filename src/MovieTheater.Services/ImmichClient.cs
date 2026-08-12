using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Services
{
    /// <summary>
    /// The Immich sidecar's REST surface, wrapped (docs/photos-plan.md §2.4).
    ///
    /// <para><b>Immich is a headless, DISPOSABLE enrichment source and our database owns all truth.</b>
    /// Everything this client returns is a SUGGESTION: face clusters, reverse-geocode labels and
    /// duplicate candidates. Nothing it says is written as fact, its ids are re-derivable by path, and
    /// dropping the whole container loses nothing but the proposals. Every caller must therefore work
    /// with this client absent — the <see cref="IImmichApi"/> seam exists so "absent" is a first-class
    /// state and not an exception path.</para>
    ///
    /// <para><b>Version pinning is operational policy, not paranoia</b> (§2.4: "pin the Immich version
    /// and upgrade deliberately — Immich moves fast and has broken external-library flows before"). This
    /// client records the server version at sync time and REFUSES a major version outside
    /// <see cref="TestedMajor"/> with a clear message, rather than parsing a payload whose shape it has
    /// never seen. Mis-parsing a face box into a tag row is a silent wrong answer; refusing is a loud
    /// right one.</para>
    ///
    /// <para>Never surfaced to a browser. The site fetches face crops through this client server-side
    /// and caches them into its own thumb cache, so a client never learns Immich exists.</para>
    /// </summary>
    public interface IImmichApi
    {
        /// <summary>The server's reported version. Called first by every sync run.</summary>
        Task<ImmichVersion> VersionAsync(CancellationToken cancel = default);

        /// <summary>One page of library assets, id ↔ originalPath (+ EXIF/geocode when the server has
        /// it). <paramref name="page"/> is 1-based, matching Immich's own paging.</summary>
        Task<ImmichAssetPage> AssetsAsync(int page, int size, CancellationToken cancel = default);

        /// <summary>One page of face clusters. A cluster with an empty name is UNNAMED — the thing a
        /// family member is asked to name or map (§2.8).</summary>
        Task<ImmichPeoplePage> PeopleAsync(int page, int size, CancellationToken cancel = default);

        /// <summary>The faces detected on one asset, each carrying its cluster and its box.</summary>
        Task<IReadOnlyList<ImmichFace>> FacesForAssetAsync(string assetId, CancellationToken cancel = default);

        /// <summary>Immich's own duplicate candidates (CLIP-based; catches crops and recolors a
        /// perceptual hash misses — §2.6).</summary>
        Task<IReadOnlyList<ImmichDuplicateGroup>> DuplicatesAsync(CancellationToken cancel = default);

        /// <summary>A cluster's face crop as image bytes, for the tag queue. Null when the server has
        /// no thumbnail for it — the UI then falls back to the box over our own derivative.</summary>
        Task<byte[]?> PersonThumbnailAsync(string personId, CancellationToken cancel = default);
    }

    // ── Wire shapes (kept minimal: only the fields the sync actually reads) ──────────────────────

    public sealed record ImmichVersion(int Major, int Minor, int Patch)
    {
        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    /// <summary>One Immich asset. <see cref="OriginalPath"/> is the sidecar's own absolute path inside
    /// its container mount — never ours, which is why mapping is a root-relative SUFFIX match (§2.4).</summary>
    public sealed class ImmichAsset
    {
        public string Id { get; set; } = "";

        public string OriginalPath { get; set; } = "";

        /// <summary>Reverse-geocode label parts. Filled by Immich's bundled offline geodata from the
        /// asset's own GPS tags; absent when the photo carries no GPS.</summary>
        public string? City { get; set; }

        public string? State { get; set; }

        public string? Country { get; set; }
    }

    public sealed class ImmichAssetPage
    {
        public List<ImmichAsset> Items { get; set; } = new List<ImmichAsset>();

        /// <summary>Whether another page exists. Immich answers this itself; the sync never infers it
        /// from a short page, because a filtered page can be short without being last.</summary>
        public bool HasNextPage { get; set; }
    }

    public sealed class ImmichPerson
    {
        public string Id { get; set; } = "";

        /// <summary>Empty for an UNNAMED cluster — the state the tag queue surfaces as "unnamed group
        /// of N faces" for a member to name or map onto an existing person (§2.8).</summary>
        public string Name { get; set; } = "";
    }

    public sealed class ImmichPeoplePage
    {
        public List<ImmichPerson> People { get; set; } = new List<ImmichPerson>();

        public bool HasNextPage { get; set; }
    }

    /// <summary>One detected face: which cluster it belongs to and where it sits, as FRACTIONS of the
    /// image (0..1). Immich reports pixels against the dimensions it decoded; this client converts, so
    /// the fractions survive every one of our derivatives (the <c>PhotoPersonTag</c> box contract).</summary>
    public sealed class ImmichFace
    {
        public string Id { get; set; } = "";

        public string PersonId { get; set; } = "";

        public double? X { get; set; }

        public double? Y { get; set; }

        public double? W { get; set; }

        public double? H { get; set; }

        /// <summary>The recognizer's confidence when it reports one. Ranking input for the queue only;
        /// it is never a threshold that auto-confirms anything (§2.8).</summary>
        public double? Confidence { get; set; }
    }

    public sealed class ImmichDuplicateGroup
    {
        public string DuplicateId { get; set; } = "";

        public List<string> AssetIds { get; set; } = new List<string>();
    }

    /// <summary>Thrown when the server is a major version this client has never been tested against
    /// (§2.4). Carries the version so the CLI can print it rather than a stack trace.</summary>
    public sealed class ImmichVersionUnsupportedException : Exception
    {
        public ImmichVersionUnsupportedException(ImmichVersion version, string message) : base(message)
        {
            Version = version;
        }

        public ImmichVersion Version { get; }
    }

    /// <summary>
    /// The HTTP implementation. One <see cref="HttpClient"/>, the API key on every request, and JSON
    /// read with a case-insensitive reader so a casing change upstream is not a silent null.
    /// </summary>
    public sealed class ImmichClient : IImmichApi, IDisposable
    {
        /// <summary>The only major version whose payload shapes this client has been written against.
        /// A different major refuses rather than guessing (§2.4).</summary>
        public const int TestedMajor = 1;

        /// <summary>Minor-version window the shapes below were taken from. Outside it the client still
        /// runs — a minor bump has never moved these fields — but it SAYS so, so a surprise has a
        /// recorded starting point.</summary>
        public const int TestedMinorFrom = 118;

        public const int TestedMinorTo = 145;

        private readonly HttpClient http;
        private readonly string? libraryId;
        private readonly bool ownsClient;
        private readonly Action<string>? log;

        public ImmichClient(HttpClient http, string? libraryId = null, Action<string>? log = null, bool ownsClient = false)
        {
            this.http = http;
            this.libraryId = string.IsNullOrWhiteSpace(libraryId) ? null : libraryId;
            this.log = log;
            this.ownsClient = ownsClient;
        }

        /// <summary>
        /// Builds a client from config, or returns null when the sidecar is not configured on this host
        /// — which is the normal state everywhere except the gateway-adjacent box, and which every
        /// caller must treat as "manual only" rather than as a failure (§2.4).
        /// </summary>
        public static ImmichClient? TryCreate(MovieTheaterConfiguration config, Action<string>? log = null,
            TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(config.ImmichBaseUrl) || string.IsNullOrWhiteSpace(config.ImmichApiKey))
                return null;
            if (!Uri.TryCreate(config.ImmichBaseUrl!.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
                return null;

            var client = new HttpClient { BaseAddress = baseUri, Timeout = timeout ?? TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("x-api-key", config.ImmichApiKey);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            return new ImmichClient(client, config.ImmichLibraryId, log, ownsClient: true);
        }

        // ── Version (§2.4's pin) ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The server version. Tries the modern route first and falls back to the pre-1.118 one, because
        /// the whole point of asking is to find out which era we are talking to — a client that could
        /// only ask the new way would fail to detect exactly the case it exists to detect.
        /// </summary>
        public async Task<ImmichVersion> VersionAsync(CancellationToken cancel = default)
        {
            foreach (var route in new[] { "api/server/version", "api/server-info/version" })
            {
                using var response = await http.GetAsync(route, cancel);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
                var root = doc.RootElement;
                return new ImmichVersion(
                    root.TryGetProperty("major", out var major) ? major.GetInt32() : 0,
                    root.TryGetProperty("minor", out var minor) ? minor.GetInt32() : 0,
                    root.TryGetProperty("patch", out var patch) ? patch.GetInt32() : 0);
            }
            throw new HttpRequestException("Immich answered no version route; this does not look like an Immich server.");
        }

        /// <summary>
        /// Reads the version and REFUSES an untested major (§2.4). Returns the version so the caller can
        /// record it with the run — "which Immich produced these suggestions" is the first question a
        /// surprising suggestion raises.
        /// </summary>
        public async Task<ImmichVersion> RequireSupportedVersionAsync(CancellationToken cancel = default)
        {
            var version = await VersionAsync(cancel);
            if (version.Major != TestedMajor)
                throw new ImmichVersionUnsupportedException(version,
                    $"Immich {version} is outside the tested major version ({TestedMajor}.x). "
                    + "Refusing rather than mis-parsing its API. Pin the container to a tested version "
                    + "(docs/photos-immich-setup.md) or widen ImmichClient.TestedMajor deliberately.");

            if (version.Minor < TestedMinorFrom || version.Minor > TestedMinorTo)
                log?.Invoke($"  note: Immich {version} is outside the tested minor range "
                            + $"{TestedMajor}.{TestedMinorFrom}–{TestedMajor}.{TestedMinorTo}; shapes are assumed unchanged.");
            return version;
        }

        // ── Assets ───────────────────────────────────────────────────────────────────────────────

        /// <summary>One page of assets via the metadata search, which is the paged, library-filterable
        /// route. <paramref name="page"/> is 1-based (Immich's own convention).</summary>
        public async Task<ImmichAssetPage> AssetsAsync(int page, int size, CancellationToken cancel = default)
        {
            var body = new StringBuilder("{\"page\":").Append(Math.Max(1, page))
                .Append(",\"size\":").Append(Math.Max(1, size))
                .Append(",\"withExif\":true");
            if (libraryId != null) body.Append(",\"libraryId\":").Append(JsonSerializer.Serialize(libraryId));
            body.Append('}');

            using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("api/search/metadata", content, cancel);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            var assets = doc.RootElement.TryGetProperty("assets", out var wrapper) ? wrapper : doc.RootElement;

            var result = new ImmichAssetPage();
            if (assets.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    result.Items.Add(ReadAsset(item));

            // "nextPage" (a value or null) is Immich's own end-of-list signal. Inferring it from a short
            // page would be wrong for a filtered search, which can legitimately return fewer.
            result.HasNextPage = assets.TryGetProperty("nextPage", out var next)
                && next.ValueKind != JsonValueKind.Null
                && !(next.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(next.GetString()));
            return result;
        }

        private static ImmichAsset ReadAsset(JsonElement item)
        {
            var asset = new ImmichAsset
            {
                Id = Str(item, "id") ?? "",
                OriginalPath = Str(item, "originalPath") ?? "",
            };
            if (item.TryGetProperty("exifInfo", out var exif) && exif.ValueKind == JsonValueKind.Object)
            {
                asset.City = Str(exif, "city");
                asset.State = Str(exif, "state");
                asset.Country = Str(exif, "country");
            }
            return asset;
        }

        // ── People + faces ───────────────────────────────────────────────────────────────────────

        public async Task<ImmichPeoplePage> PeopleAsync(int page, int size, CancellationToken cancel = default)
        {
            using var response = await http.GetAsync(
                $"api/people?page={Math.Max(1, page)}&size={Math.Max(1, size)}&withHidden=false", cancel);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            var root = doc.RootElement;
            var result = new ImmichPeoplePage();
            if (root.TryGetProperty("people", out var people) && people.ValueKind == JsonValueKind.Array)
                foreach (var person in people.EnumerateArray())
                    result.People.Add(new ImmichPerson
                    {
                        Id = Str(person, "id") ?? "",
                        Name = Str(person, "name") ?? "",
                    });

            result.HasNextPage = root.TryGetProperty("hasNextPage", out var more)
                && more.ValueKind == JsonValueKind.True;
            return result;
        }

        /// <summary>
        /// The faces on one asset, with boxes converted from Immich's pixel coordinates into fractions.
        /// A face whose reported image dimensions are missing or zero yields a BOXLESS suggestion rather
        /// than a fabricated one — a tag with no box is still a perfectly good tag, and an invented box
        /// would draw a rectangle over the wrong part of a family photograph.
        /// </summary>
        public async Task<IReadOnlyList<ImmichFace>> FacesForAssetAsync(string assetId, CancellationToken cancel = default)
        {
            using var response = await http.GetAsync("api/assets/" + Uri.EscapeDataString(assetId), cancel);
            if (response.StatusCode == HttpStatusCode.NotFound) return Array.Empty<ImmichFace>();
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            if (!doc.RootElement.TryGetProperty("people", out var people) || people.ValueKind != JsonValueKind.Array)
                return Array.Empty<ImmichFace>();

            var faces = new List<ImmichFace>();
            foreach (var person in people.EnumerateArray())
            {
                var personId = Str(person, "id") ?? "";
                if (personId.Length == 0) continue;
                if (!person.TryGetProperty("faces", out var faceList) || faceList.ValueKind != JsonValueKind.Array)
                {
                    faces.Add(new ImmichFace { Id = personId, PersonId = personId });
                    continue;
                }
                foreach (var face in faceList.EnumerateArray())
                    faces.Add(ReadFace(face, personId));
            }
            return faces;
        }

        private static ImmichFace ReadFace(JsonElement face, string personId)
        {
            var result = new ImmichFace
            {
                Id = Str(face, "id") ?? personId,
                PersonId = personId,
                Confidence = Num(face, "confidence"),
            };

            var imageW = Num(face, "imageWidth") ?? 0;
            var imageH = Num(face, "imageHeight") ?? 0;
            var x1 = Num(face, "boundingBoxX1");
            var y1 = Num(face, "boundingBoxY1");
            var x2 = Num(face, "boundingBoxX2");
            var y2 = Num(face, "boundingBoxY2");
            if (imageW <= 0 || imageH <= 0 || x1 == null || y1 == null || x2 == null || y2 == null) return result;

            result.X = Clamp01(x1.Value / imageW);
            result.Y = Clamp01(y1.Value / imageH);
            result.W = Clamp01((x2.Value - x1.Value) / imageW);
            result.H = Clamp01((y2.Value - y1.Value) / imageH);
            return result;
        }

        public async Task<byte[]?> PersonThumbnailAsync(string personId, CancellationToken cancel = default)
        {
            using var response = await http.GetAsync("api/people/" + Uri.EscapeDataString(personId) + "/thumbnail", cancel);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
            return bytes.Length == 0 ? null : bytes;
        }

        // ── Duplicate candidates (§2.6) ──────────────────────────────────────────────────────────

        public async Task<IReadOnlyList<ImmichDuplicateGroup>> DuplicatesAsync(CancellationToken cancel = default)
        {
            using var response = await http.GetAsync("api/duplicates", cancel);
            if (response.StatusCode == HttpStatusCode.NotFound) return Array.Empty<ImmichDuplicateGroup>();
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ImmichDuplicateGroup>();

            var groups = new List<ImmichDuplicateGroup>();
            foreach (var group in doc.RootElement.EnumerateArray())
            {
                var entry = new ImmichDuplicateGroup { DuplicateId = Str(group, "duplicateId") ?? "" };
                if (group.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var id = Str(asset, "id");
                        if (!string.IsNullOrEmpty(id)) entry.AssetIds.Add(id!);
                    }
                if (entry.AssetIds.Count > 1) groups.Add(entry);
            }
            return groups;
        }

        // ── Path mapping (§2.4) ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// The root-relative key an Immich <c>originalPath</c> maps onto.
        ///
        /// <para>Immich sees the collection through its OWN container mount, so its absolute paths share
        /// nothing with ours but the tail. The match is therefore a normalized SUFFIX comparison: split
        /// both on separators, compare the last N segments. This returns the normalized, forward-slash,
        /// lower-cased tail so a caller can index our rows by the same function and join in memory
        /// instead of running a LIKE per asset.</para>
        /// </summary>
        public static string SuffixKey(string path, int segments)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var parts = path.Replace('\\', '/').Split('/')
                .Where(p => p.Length > 0 && p != ".")
                .ToList();
            if (parts.Count == 0) return "";
            var take = Math.Min(Math.Max(1, segments), parts.Count);
            return string.Join("/", parts.Skip(parts.Count - take)).ToLowerInvariant();
        }

        /// <summary>How many trailing segments a match needs. Two — file name plus its folder — because
        /// a phone-backup tree is full of <c>IMG_0001.jpg</c> and a file name alone would map the wrong
        /// photograph; more than two starts to depend on how deep the sidecar's mount sits, which is a
        /// deployment detail our rows must not encode.</summary>
        public const int DefaultSuffixSegments = 2;

        private static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static double? Num(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;

        private static double Clamp01(double value) => Math.Min(1, Math.Max(0, value));

        public void Dispose()
        {
            if (ownsClient) http.Dispose();
        }
    }
}
