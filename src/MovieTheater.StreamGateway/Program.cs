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
var transformer = new GatewayTransformer(jellyfinApiKey);
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

    public GatewayTransformer(string apiKey) => this.apiKey = apiKey;

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
}
