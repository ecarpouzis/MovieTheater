using System.Linq;
using MovieTheater.Core;
using Yarp.ReverseProxy.Forwarder;

// ─────────────────────────────────────────────────────────────────────────────
// StreamGateway (streaming-plan.md §3.3): the public data plane. It runs on the
// media-server host behind a TLS reverse proxy and serves exactly one thing —
// Jellyfin's /Videos/* HLS surface — to holders of a valid signed capability URL
// minted by the site.
//
// URL shape: /s/{token}/Videos/{...}. The token rides the path so Jellyfin's
// relative segment URIs inherit it (no playlist rewriting). On each request the
// gateway: validates the HMAC + expiry, confines the path to the token's item,
// injects X-Emby-Token, and forwards to localhost Jellyfin. Everything else 403s.
// It holds no state: no DB, no sessions.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpForwarder();

var app = builder.Build();

var config = app.Configuration;
string secret = config["StreamTokenSecret"]
    ?? throw new InvalidOperationException("StreamTokenSecret is required.");
string jellyfinBase = (config["JellyfinBaseUrl"] ?? "http://localhost:8096").TrimEnd('/');
string jellyfinApiKey = config["JellyfinApiKey"]
    ?? throw new InvalidOperationException("JellyfinApiKey is required.");
string siteOrigin = config["SiteOrigin"] ?? "https://your-movie-site.example";

// One pooled client for all forwarding; no auto-decompression so byte ranges pass through.
var httpClient = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    ConnectTimeout = TimeSpan.FromSeconds(15),
});

var requestOptions = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };
var transformer = new GatewayTransformer(jellyfinApiKey, siteOrigin);
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();

// CORS preflight + headers. Auth is in the URL, never credentials, so the
// allow-origin can be the single site origin with no allow-credentials.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Access-Control-Allow-Origin"] = siteOrigin;
    headers["Vary"] = "Origin";
    headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
    headers["Access-Control-Allow-Headers"] = "Range";
    headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }
    await next();
});

// Liveness probe for Caddy / monitoring — reveals nothing.
app.MapGet("/healthz", () => Results.Text("ok"));

// ── Music (music-plan.md §2.1): token-gated direct file serving, no Jellyfin involved. The
// capability carries the music-root-relative path; this route only confines it to MusicRootDir
// and serves bytes with Range support. Unconfigured hosts simply 404 the route.
string? musicRoot = config["MusicRootDir"];
string? musicRootFull = musicRoot == null ? null : Path.GetFullPath(musicRoot);
app.MapMethods($"/s/{{token}}/{MusicStreamRoutes.File}", new[] { "GET", "HEAD" }, (HttpContext context, string token) =>
{
    if (musicRootFull == null)
        return Results.NotFound();
    if (!MusicCapabilityToken.TryValidate(secret, token, out var payload) || payload is null)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    // Confinement: the resolved file must sit under the music root — a token is signed, but
    // defense-in-depth costs one string compare.
    var full = Path.GetFullPath(Path.Combine(musicRootFull,
        payload.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!full.StartsWith(musicRootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!File.Exists(full))
        return Results.NotFound();

    // The token URL is stable for its lifetime, so the browser may cache/reuse it across seeks.
    context.Response.Headers["Cache-Control"] = "private, max-age=3600";
    return Results.File(full, MusicMimeTypes.FromExtension(Path.GetExtension(full)), enableRangeProcessing: true);
});

// ── Music transcode lane (music-plan.md §Phase 7) ────────────────────────────────────────────────
// For the handful of formats no browser decodes (.wma/.ape/.wv/…), pipe the file through ffmpeg as
// mp3 instead of refusing it. Same 4-field capability as MusicFile — the ROUTE picks the treatment,
// so the token format never had to change. Streaming stdout means no Range support and no
// Content-Length: the browser plays it as an endless stream, seeking only within what it buffered.
// The route 404s unless the host sets FfmpegPath, so a gateway without ffmpeg simply doesn't offer it.
string? ffmpegPath = config["FfmpegPath"];
int maxTranscodes = int.TryParse(config["MusicMaxConcurrentTranscodes"], out var mt) && mt > 0 ? mt : 2;
var transcodeSlots = new SemaphoreSlim(maxTranscodes);

app.MapMethods($"/s/{{token}}/{MusicStreamRoutes.Transcode}", new[] { "GET", "HEAD" }, async (HttpContext context, string token) =>
{
    if (musicRootFull == null || string.IsNullOrWhiteSpace(ffmpegPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (!MusicCapabilityToken.TryValidate(secret, token, out var payload) || payload is null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var full = Path.GetFullPath(Path.Combine(musicRootFull,
        payload.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!full.StartsWith(musicRootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (!File.Exists(full))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    // Each transcode is a whole ffmpeg process; cap how many can run at once so a burst of clients
    // can't take the media host down. Over the cap the client is asked to retry rather than queued.
    if (!await transcodeSlots.WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = "5";
        return;
    }

    try
    {
        context.Response.ContentType = "audio/mpeg";
        context.Response.Headers["Accept-Ranges"] = "none";
        context.Response.Headers["Cache-Control"] = "private, no-store";
        if (HttpMethods.IsHead(context.Request.Method)) return;

        var psi = new System.Diagnostics.ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", full, "-map", "a:0", "-f", "mp3", "-b:a", "192k", "pipe:1" })
            psi.ArgumentList.Add(arg);

        using var ffmpeg = System.Diagnostics.Process.Start(psi);
        if (ffmpeg == null)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }
        try
        {
            // Drain stderr so a chatty ffmpeg can't deadlock on a full pipe buffer.
            _ = ffmpeg.StandardError.ReadToEndAsync();
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(context.Response.Body, 64 * 1024, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The listener skipped/closed the tab — normal.
        }
        finally
        {
            try { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); } catch { }
        }
    }
    finally
    {
        transcodeSlots.Release();
    }
});

app.Map("/s/{token}/Videos/{**rest}", async (HttpContext context, string token, string rest) =>
{
    if (!StreamCapabilityToken.TryValidate(secret, token, out var payload) || payload is null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // Confine the capability to its own item: a token for movie A can't fetch B's
    // segments. Jellyfin's /Videos/{itemId}/... puts the item id first — but it uses the
    // dashed GUID form in the path while the token carries the dashless id, so compare
    // with separators stripped.
    var firstSegment = rest.Split('/', 2)[0];
    if (!string.Equals(NormalizeItemId(firstSegment), NormalizeItemId(payload.ItemId), StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await forwarder.SendAsync(context, jellyfinBase, httpClient, requestOptions, transformer);
});

app.Run();

// Jellyfin item ids appear both dashed (URL path) and dashless (the stored id / token);
// normalize so the confinement check matches either form.
static string NormalizeItemId(string id) => id.Replace("-", "");

/// <summary>
/// Rewrites the proxied request: drops the /s/{token} prefix so the upstream path is
/// the bare /Videos/… Jellyfin expects, and injects the server-held API key. The key
/// never reaches the browser.
/// </summary>
sealed class GatewayTransformer : HttpTransformer
{
    private readonly string apiKey;
    private readonly string siteOrigin;

    public GatewayTransformer(string apiKey, string siteOrigin)
    {
        this.apiKey = apiKey;
        this.siteOrigin = siteOrigin;
    }

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        var path = httpContext.Request.Path.Value ?? string.Empty;
        var videosIndex = path.IndexOf("/Videos/", StringComparison.OrdinalIgnoreCase);
        var upstreamPath = videosIndex >= 0 ? path[videosIndex..] : path;
        var query = httpContext.Request.QueryString.Value ?? string.Empty;

        proxyRequest.RequestUri = new Uri(destinationPrefix + upstreamPath + query);
        proxyRequest.Headers.Host = null;

        proxyRequest.Headers.Remove("X-Emby-Token");
        proxyRequest.Headers.TryAddWithoutValidation("X-Emby-Token", apiKey);
    }

    public override async ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
    {
        var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);

        // Jellyfin emits its own Access-Control-Allow-Origin: * — left alongside the
        // gateway's header that produces TWO ACAO headers, which browsers reject (so HLS
        // playback silently fails). Strip all upstream CORS headers and emit exactly one.
        var headers = httpContext.Response.Headers;
        foreach (var key in headers.Keys.Where(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)).ToList())
            headers.Remove(key);
        headers["Access-Control-Allow-Origin"] = siteOrigin;
        headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
        return result;
    }
}
