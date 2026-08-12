using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MovieTheater.Services;

namespace MovieTheater.Tests
{
    /// <summary>
    /// A stand-in Immich (docs/photos-plan.md §2.4). NOTHING in this suite ever talks to a real one:
    /// the sidecar is LAN-only by design, a build must never depend on a container being up, and a test
    /// that needed one would be a test nobody could run.
    ///
    /// <para>The dataset is held here and served two ways, from the same rows: as an
    /// <see cref="IImmichApi"/> for the sync tests (which are about what the PASS does with an answer)
    /// and over real HTTP through <see cref="Serve"/> for the client tests (which are about whether
    /// <see cref="ImmichClient"/> READS an answer correctly). One dataset, so the two can never disagree
    /// about what the sidecar said.</para>
    /// </summary>
    public sealed class FakeImmich : IImmichApi
    {
        public ImmichVersion Version = new ImmichVersion(1, 120, 0);

        public readonly List<ImmichAsset> Assets = new List<ImmichAsset>();

        public readonly List<ImmichPerson> People = new List<ImmichPerson>();

        /// <summary>Faces per Immich asset id.</summary>
        public readonly Dictionary<string, List<ImmichFace>> Faces = new Dictionary<string, List<ImmichFace>>();

        public readonly List<ImmichDuplicateGroup> Duplicates = new List<ImmichDuplicateGroup>();

        /// <summary>Face-crop bytes per cluster id. Absent means "the server has no thumbnail", which
        /// is an ordinary answer the UI degrades around rather than an error.</summary>
        public readonly Dictionary<string, byte[]> Thumbnails = new Dictionary<string, byte[]>();

        /// <summary>How many times each route was called — the cheapest way to assert "a re-sync did
        /// not go back to the wire for something it had already answered".</summary>
        public readonly Dictionary<string, int> Calls = new Dictionary<string, int>();

        private void Count(string route) => Calls[route] = (Calls.TryGetValue(route, out var v) ? v : 0) + 1;

        // ── Authoring ────────────────────────────────────────────────────────────────────────────

        /// <summary>Adds an asset at the path the sidecar sees it under — its OWN container mount, which
        /// shares nothing with ours but the tail. That is the whole point of the suffix match (§2.4).</summary>
        public ImmichAsset AddAsset(string id, string containerPath, string? city = null, string? state = null)
        {
            var asset = new ImmichAsset { Id = id, OriginalPath = containerPath, City = city, State = state };
            Assets.Add(asset);
            return asset;
        }

        /// <summary>Adds a cluster. Immich's own name for it is irrelevant to us — names live in our
        /// rows (§6) — so it is not even a parameter.</summary>
        public ImmichPerson AddCluster(string id)
        {
            var person = new ImmichPerson { Id = id, Name = "" };
            People.Add(person);
            return person;
        }

        public void AddFace(string assetId, string clusterId, double confidence = 0.9,
            double x = 0.25, double y = 0.2, double w = 0.2, double h = 0.25)
        {
            if (!Faces.TryGetValue(assetId, out var list)) Faces[assetId] = list = new List<ImmichFace>();
            list.Add(new ImmichFace
            {
                Id = $"{assetId}:{clusterId}",
                PersonId = clusterId,
                Confidence = confidence,
                X = x, Y = y, W = w, H = h,
            });
        }

        public void AddDuplicate(string groupId, params string[] assetIds) =>
            Duplicates.Add(new ImmichDuplicateGroup { DuplicateId = groupId, AssetIds = assetIds.ToList() });

        // ── IImmichApi ───────────────────────────────────────────────────────────────────────────

        public Task<ImmichVersion> VersionAsync(CancellationToken cancel = default)
        {
            Count("version");
            return Task.FromResult(Version);
        }

        public Task<ImmichAssetPage> AssetsAsync(int page, int size, CancellationToken cancel = default)
        {
            Count("assets");
            var skip = (Math.Max(1, page) - 1) * size;
            var items = Assets.Skip(skip).Take(size).ToList();
            return Task.FromResult(new ImmichAssetPage
            {
                Items = items,
                HasNextPage = skip + items.Count < Assets.Count,
            });
        }

        public Task<ImmichPeoplePage> PeopleAsync(int page, int size, CancellationToken cancel = default)
        {
            Count("people");
            var skip = (Math.Max(1, page) - 1) * size;
            var items = People.Skip(skip).Take(size).ToList();
            return Task.FromResult(new ImmichPeoplePage
            {
                People = items,
                HasNextPage = skip + items.Count < People.Count,
            });
        }

        public Task<IReadOnlyList<ImmichFace>> FacesForAssetAsync(string assetId, CancellationToken cancel = default)
        {
            Count("faces");
            return Task.FromResult<IReadOnlyList<ImmichFace>>(
                Faces.TryGetValue(assetId, out var list) ? list : new List<ImmichFace>());
        }

        public Task<IReadOnlyList<ImmichDuplicateGroup>> DuplicatesAsync(CancellationToken cancel = default)
        {
            Count("duplicates");
            return Task.FromResult<IReadOnlyList<ImmichDuplicateGroup>>(Duplicates);
        }

        public Task<byte[]?> PersonThumbnailAsync(string personId, CancellationToken cancel = default)
        {
            Count("thumbnail");
            return Task.FromResult(Thumbnails.TryGetValue(personId, out var bytes) ? bytes : null);
        }

        // ── The same dataset over real HTTP ──────────────────────────────────────────────────────

        /// <summary>
        /// Serves this dataset on a loopback port in the shapes the tested Immich versions use, so
        /// <see cref="ImmichClient"/>'s PARSING is exercised rather than assumed. Loopback only: nothing
        /// in this suite may open a listener the network can reach.
        /// </summary>
        public FakeImmichServer Serve(int port) => new FakeImmichServer(this, port);
    }

    /// <summary>A loopback HTTP server speaking the tested Immich shapes over a <see cref="FakeImmich"/>
    /// dataset. Disposed by the test; nothing here outlives it.</summary>
    public sealed class FakeImmichServer : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly FakeImmich data;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();

        public FakeImmichServer(FakeImmich data, int port)
        {
            this.data = data;
            BaseUrl = $"http://localhost:{port}";
            listener.Prefixes.Add(BaseUrl + "/");
            listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }

        /// <summary>The api key every request is expected to carry. A request without it is answered 401,
        /// which is what proves the client actually sends it.</summary>
        public string ApiKey { get; set; } = "test-key";

        private async Task LoopAsync()
        {
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception) { return; }

                try { Handle(context); }
                catch (Exception) { /* a stand-in server's own failure must not hang the test */ }
                finally { try { context.Response.Close(); } catch (Exception) { } }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (context.Request.Headers["x-api-key"] != ApiKey)
            {
                context.Response.StatusCode = 401;
                return;
            }

            if (path == "/api/server/version")
            {
                Write(context, $"{{\"major\":{data.Version.Major},\"minor\":{data.Version.Minor},\"patch\":{data.Version.Patch}}}");
                return;
            }

            if (path == "/api/search/metadata")
            {
                var body = ReadBody(context);
                var page = ReadInt(body, "page", 1);
                var size = ReadInt(body, "size", 100);
                var skip = (page - 1) * size;
                var items = data.Assets.Skip(skip).Take(size).ToList();
                var json = new StringBuilder("{\"assets\":{\"items\":[");
                json.Append(string.Join(",", items.Select(a =>
                    $"{{\"id\":{Q(a.Id)},\"originalPath\":{Q(a.OriginalPath)},\"exifInfo\":{{"
                    + $"\"city\":{Q(a.City)},\"state\":{Q(a.State)},\"country\":{Q(a.Country)}}}}}")));
                json.Append("],\"nextPage\":");
                json.Append(skip + items.Count < data.Assets.Count ? Q((page + 1).ToString()) : "null");
                json.Append("}}");
                Write(context, json.ToString());
                return;
            }

            if (path == "/api/people")
            {
                var query = context.Request.QueryString;
                var page = int.TryParse(query["page"], out var p) ? p : 1;
                var size = int.TryParse(query["size"], out var s) ? s : 100;
                var skip = (page - 1) * size;
                var items = data.People.Skip(skip).Take(size).ToList();
                var json = new StringBuilder("{\"people\":[");
                json.Append(string.Join(",", items.Select(x => $"{{\"id\":{Q(x.Id)},\"name\":{Q(x.Name)}}}")));
                json.Append("],\"hasNextPage\":");
                json.Append(skip + items.Count < data.People.Count ? "true" : "false");
                json.Append('}');
                Write(context, json.ToString());
                return;
            }

            if (path.StartsWith("/api/assets/"))
            {
                var id = Uri.UnescapeDataString(path.Substring("/api/assets/".Length));
                var faces = data.Faces.TryGetValue(id, out var list) ? list : new List<ImmichFace>();
                // Immich reports boxes in PIXELS against the dimensions it decoded; the client converts
                // to fractions. Encoding them as pixels here is what makes that conversion testable.
                const int width = 1000;
                const int height = 800;
                var byCluster = faces.GroupBy(f => f.PersonId);
                var json = new StringBuilder($"{{\"id\":{Q(id)},\"people\":[");
                json.Append(string.Join(",", byCluster.Select(g =>
                    $"{{\"id\":{Q(g.Key)},\"name\":\"\",\"faces\":["
                    + string.Join(",", g.Select(f =>
                        $"{{\"id\":{Q(f.Id)},\"imageWidth\":{width},\"imageHeight\":{height},"
                        + $"\"boundingBoxX1\":{(int)((f.X ?? 0) * width)},\"boundingBoxY1\":{(int)((f.Y ?? 0) * height)},"
                        + $"\"boundingBoxX2\":{(int)(((f.X ?? 0) + (f.W ?? 0)) * width)},"
                        + $"\"boundingBoxY2\":{(int)(((f.Y ?? 0) + (f.H ?? 0)) * height)},"
                        + $"\"confidence\":{(f.Confidence ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"))
                    + "]}")));
                json.Append("]}");
                Write(context, json.ToString());
                return;
            }

            if (path == "/api/duplicates")
            {
                var json = new StringBuilder("[");
                json.Append(string.Join(",", data.Duplicates.Select(g =>
                    $"{{\"duplicateId\":{Q(g.DuplicateId)},\"assets\":["
                    + string.Join(",", g.AssetIds.Select(a => $"{{\"id\":{Q(a)}}}")) + "]}")));
                json.Append(']');
                Write(context, json.ToString());
                return;
            }

            if (path.StartsWith("/api/people/") && path.EndsWith("/thumbnail"))
            {
                var id = Uri.UnescapeDataString(path.Substring("/api/people/".Length,
                    path.Length - "/api/people/".Length - "/thumbnail".Length));
                if (!data.Thumbnails.TryGetValue(id, out var bytes))
                {
                    context.Response.StatusCode = 404;
                    return;
                }
                context.Response.ContentType = "image/jpeg";
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                return;
            }

            context.Response.StatusCode = 404;
        }

        private static string ReadBody(HttpListenerContext context)
        {
            using var reader = new System.IO.StreamReader(context.Request.InputStream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static int ReadInt(string json, string name, int fallback)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                    ? value.GetInt32()
                    : fallback;
            }
            catch (JsonException)
            {
                return fallback;
            }
        }

        private static string Q(string? value) => value == null ? "null" : JsonSerializer.Serialize(value);

        private static void Write(HttpListenerContext context, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            stopping.Cancel();
            try { listener.Stop(); } catch (Exception) { }
            try { listener.Close(); } catch (Exception) { }
            stopping.Dispose();
        }
    }
}
