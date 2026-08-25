using Microsoft.AspNetCore.Mvc;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;

namespace MovieTheater.BooksHost.Web
{
    /// <summary>
    /// The R5 surface: liveness, the seam proof, the media token, and the thumbnail fast path. Every other
    /// media route answers 501 until R6 brings the archive readers; every catalog route is R6.
    /// </summary>
    public static class HostEndpoints
    {
        public static void MapHostEndpoints(this WebApplication app, BooksHostConfiguration config)
        {
            // liveness for Caddy / monitoring — reveals nothing, needs nothing
            app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

            // the seam proof: the identity the site stamped, echoed back
            var ping = (HttpContext ctx) => Results.Json(new
            {
                userId = BooksIdentity.UserId(ctx.User),
                username = BooksIdentity.Username(ctx.User),
                isAdmin = BooksIdentity.IsAdmin(ctx.User),
                maturity = BooksIdentity.CeilingFor(ctx.User),
                host = "books-host", // a role name, never the machine's
                utc = DateTime.UtcNow,
            });
            app.MapGet("/ping", ping);
            app.MapGet("/opds/ping", ping);

            // the session's media capability (12 h) + where to spend it
            app.MapGet("/media-token", (HttpContext ctx) =>
            {
                var secret = config.MediaTokenSecret;
                var userId = BooksIdentity.UserId(ctx.User);
                if (string.IsNullOrEmpty(secret) || userId == null || string.IsNullOrEmpty(config.PublicBaseUrl))
                    return Results.Json(new { configured = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
                var token = BooksMediaToken.MintNow(secret, userId.Value, BooksIdentity.CeilingFor(ctx.User), BooksIdentity.IsAdmin(ctx.User), out var expires);
                return Results.Json(new { configured = true, token, baseUrl = config.PublicBaseUrl.TrimEnd('/'), expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime });
            });

            // media plane — the token in the path is the credential; no identity header, no cookie
            var media = app.MapGroup(BooksMediaRoutes.Prefix + "/{token}").AllowAnonymous();
            media.MapGet("/thumbs/{id}.webp", (string token, string id, HttpContext ctx) =>
            {
                if (!Authorized(token, config, out _)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                var cacheDir = config.CacheDir;
                if (cacheDir == null) return Results.NotFound();
                var full = BooksMediaRoutes.ResolveThumb(cacheDir, id);
                if (full == null) return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (!File.Exists(full)) return Results.NotFound();
                var mtime = File.GetLastWriteTimeUtc(full);
                var etag = "\"" + mtime.Ticks.ToString("x") + "\"";
                if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
                ctx.Response.Headers.CacheControl = "private, max-age=86400";
                ctx.Response.Headers.ETag = etag;
                return Results.File(full, "image/webp", lastModified: mtime, enableRangeProcessing: false);
            });
            media.MapGet("/{**rest}", (string token, string rest) =>
                Authorized(token, config, out _) ? Results.StatusCode(StatusCodes.Status501NotImplemented) : Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        private static bool Authorized(string token, BooksHostConfiguration config, out BooksMediaToken.Payload? payload)
        {
            payload = null;
            var secret = config.MediaTokenSecret;
            return !string.IsNullOrEmpty(secret) && BooksMediaToken.TryValidate(secret, token, out payload);
        }
    }
}
