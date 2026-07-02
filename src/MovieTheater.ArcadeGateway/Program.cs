using MovieTheater.Core;
using Yarp.ReverseProxy.Forwarder;

// ─────────────────────────────────────────────────────────────────────────────
// ArcadeGateway (arcade-plan.md Appendix C): the public signaling plane for the
// arcade. It runs on the media-server host behind a TLS reverse proxy (Caddy:
// arcade.carpouzis.com) and does exactly one thing — forward an authorized
// browser WebSocket to the CloudRetro coordinator's /ws, which has no auth of its
// own. Everything else 403s. It holds no state: no DB, no sessions.
//
// URL shape: wss://arcade.<host>/w/{token}?room_id=…&zone=…. The token rides the
// path because the stock CloudRetro client force-overwrites the WS path to /ws off
// window.location, so the gateway must (a) validate the signed capability, (b)
// confine a joiner to exactly the room its token names, then (c) REWRITE the path
// to the bare /ws the coordinator serves while preserving the query string.
//
// Media (VP8/Opus + input DataChannels) never touches this process — it flows
// browser ↔ host over WebRTC/UDP. This gateway carries only the signaling WS.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpForwarder();

var app = builder.Build();

var config = app.Configuration;
string secret = config["ArcadeTokenSecret"]
    ?? throw new InvalidOperationException("ArcadeTokenSecret is required.");
string coordinatorBase = (config["CoordinatorBaseUrl"] ?? "http://localhost:8000").TrimEnd('/');
string siteOrigin = config["SiteOrigin"] ?? "https://your-movie-site.example";

// One pooled client for all forwarding. No cookies, no proxy, no redirects — the
// same posture as StreamGateway; the WS upgrade is carried end-to-end by YARP.
var httpClient = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    ConnectTimeout = TimeSpan.FromSeconds(15),
});

// Delta from StreamGateway (Appendix C1): the signaling socket goes quiet after
// SDP/ICE setup — inputs move to WebRTC DataChannels — so a 100 s activity timeout
// would sever a live room. Disable it and let the room TTL drive lifecycle.
var requestOptions = new ForwarderRequestConfig { ActivityTimeout = Timeout.InfiniteTimeSpan };
var transformer = new WsTransformer();
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();

// Liveness probe for Caddy / monitoring — reveals nothing.
app.MapGet("/healthz", () => Results.Text("ok"));

app.Map("/w/{token}", async (HttpContext context, string token) =>
{
    if (!ArcadeCapabilityToken.TryValidate(secret, token, out var payload) || payload is null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // Defense-in-depth on top of the coordinator's own Origin check: if a browser
    // presents an Origin, it must be the site. Non-browser clients (test scripts)
    // send none and are allowed through — the token is the real gate.
    var origin = context.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, siteOrigin, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // Confine the capability to its room. A joiner token carries the bound CloudRetro
    // room id; the connect query's room_id must match it exactly (this is the arcade
    // analog of StreamGateway confining /Videos/{itemId} to the token's item). A
    // creator token carries an empty room id and must connect with an empty room_id
    // (empty ⇒ "create a room on a free worker"). Both sides are the decoded value.
    var requestedRoomId = context.Request.Query["room_id"].ToString();
    if (!string.Equals(requestedRoomId, payload.CloudRetroRoomId ?? string.Empty, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await forwarder.SendAsync(context, coordinatorBase, httpClient, requestOptions, transformer);
});

app.Run();

/// <summary>
/// Rewrites the proxied request: swaps the /w/{token} path for the bare /ws the
/// CloudRetro coordinator serves (its client and server both hardcode /ws, so a
/// prefix-preserving proxy 404s), re-appends the original query string (room_id,
/// zone), and clears Host so the upstream sees its own. No API key to inject —
/// unlike StreamGateway, CloudRetro has no auth surface behind the gate.
/// </summary>
sealed class WsTransformer : HttpTransformer
{
    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        var query = httpContext.Request.QueryString.Value ?? string.Empty;
        proxyRequest.RequestUri = new Uri(destinationPrefix + "/ws" + query);
        proxyRequest.Headers.Host = null;
    }
}
