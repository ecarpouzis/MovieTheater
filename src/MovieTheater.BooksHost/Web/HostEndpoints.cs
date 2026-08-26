using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using MovieTheater.Books;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.Books.Services;

namespace MovieTheater.BooksHost.Web
{
    /// <summary>
    /// Liveness, the seam proof, the media token — and the MEDIA PLANE: every byte the browser fetches for the
    /// Books vertical comes from here, straight off this host, and never through the site pods.
    ///
    /// <para><b>The token in the path IS the credential.</b> These routes carry no cookie and no identity header
    /// (an <c>&lt;img src&gt;</c> or a download link cannot set one), so they are anonymous to the framework and
    /// authenticate themselves: the token is opened, and the identity it carries — the same user id, ceiling and
    /// admin flag the header established — is rebuilt into a principal and run through
    /// <c>ItemAccess.GetAuthorizedItemAsync</c>. A bad token is 403; an item this token may not see is 404, the
    /// same answer as an item that does not exist.</para>
    ///
    /// <para><b>Thumbnails are the one exception</b>, and deliberately: they hit no database at all. A leaked id
    /// there reveals at most a cover the holder was already shown in a list, and paying an indexed read per card
    /// would put ~120 queries in front of every grid page.</para>
    /// </summary>
    public static class HostEndpoints
    {
        public static void MapHostEndpoints(this WebApplication app, BooksHostConfiguration config)
        {
            // Liveness for the reverse proxy, monitoring and the deploy probe. It TOUCHES THE STORE: on
            // 2026-08-25 the host went live without its native SQLite library and every database call threw,
            // while a store-blind healthz kept saying ok and the deploy probes passed. One SELECT 1 costs
            // microseconds and turns that into a 503 with the reason. Reveals nothing about the catalog.
            app.MapGet("/healthz", async (HttpContext ctx) =>
            {
                var db = ctx.RequestServices.GetService<BooksDb>();
                if (db == null) return Results.Text("ok (no catalog configured)");
                try
                {
                    await db.Database.ExecuteSqlRawAsync("SELECT 1", ctx.RequestAborted);
                    return Results.Text("ok");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Results.Text("db: " + ex.GetBaseException().GetType().Name, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }).AllowAnonymous();

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

            var media = app.MapGroup(BooksMediaRoutes.Prefix + "/{token}").AllowAnonymous();

            // ── thumbnails: the zero-database fast path ───────────────────────────────────────────────────
            media.MapGet("/thumbs/{id}.webp", (string token, string id, HttpContext ctx, MediaAccess access) =>
            {
                if (!access.TryOpen(token, out _)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                var cacheDir = config.CacheDir;
                if (cacheDir == null) return Results.NotFound();
                var full = BooksMediaRoutes.ResolveThumb(cacheDir, id);
                if (full == null) return Results.StatusCode(StatusCodes.Status403Forbidden);
                return ServeFile(ctx, full, ThumbnailService.ContentType);
            });

            // ── folder icons: same shape, same confinement, f_{id}.jpg ────────────────────────────────────
            media.MapGet("/folders/{id}/icon", (string token, string id, HttpContext ctx, MediaAccess access) =>
            {
                if (!access.TryOpen(token, out _)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                var cacheDir = config.CacheDir;
                if (cacheDir == null) return Results.NotFound();
                var full = BooksMediaRoutes.ResolveFolderIcon(cacheDir, id);
                if (full == null) return Results.StatusCode(StatusCodes.Status403Forbidden);
                return ServeFile(ctx, full, "image/jpeg");
            });

            // ── pages ─────────────────────────────────────────────────────────────────────────────────────
            media.MapGet("/pages/{id:int}/{page:int}", async (
                string token, int id, int page, int? maxWidth, HttpContext ctx,
                MediaAccess access, BooksDb db, PageByteCache pageCache, LocalArchiveCache archiveCache,
                ImageScalingService scaling, IEnumerable<IArchiveReader> readers) =>
            {
                var (tokenValid, item) = await access.ResolveAsync(db, token, id, ctx.RequestAborted);
                if (!tokenValid) return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (item == null || page < 0) return Results.NotFound();

                var ticks = item.FileModifiedAt?.Ticks ?? 0;

                // For an EPUB, page 0 means THE COVER, not spine page 0 — the first spine document of a
                // reflowable novel is routinely a title page. The variant keeps the two apart in the byte cache
                // and in the ETag, so a browser holding the old spine image re-fetches.
                var serveCover = page == 0 && ".epub".Equals(item.Extension, StringComparison.OrdinalIgnoreCase);
                var pageKey = serveCover ? "cover" : page.ToString();

                // The ETag is fully determined by the catalog row, so a revalidation is answered BEFORE the
                // archive is opened or a pixel is decoded.
                var etag = $"\"{id}_{pageKey}_{maxWidth}_{ticks}\"";
                ctx.Response.Headers.CacheControl = "private, max-age=86400";
                ctx.Response.Headers.ETag = etag;
                if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);

                byte[] bytes;
                try
                {
                    var cacheKey = PageByteCache.Key(item.Path, ticks, page, serveCover ? "cover" : null);
                    bytes = await pageCache.GetOrExtractAsync(cacheKey, () =>
                    {
                        // warm: a request for page ≥ 1 means someone is actually READING (page 0 doubles as the
                        // cover for browse grids), so start pulling the whole archive to local disk; later cold
                        // pages and re-reads then skip the share entirely.
                        var physical = archiveCache.Resolve(item.Path, ticks, warm: page >= 1);
                        var reader = readers.ForFile(physical, item.Extension)
                            ?? throw new NotSupportedException("no reader");
                        return serveCover ? reader.GetCoverAsync(physical) : reader.GetPageAsync(physical, page);
                    });
                }
                catch (ArgumentOutOfRangeException) { return Results.NotFound(); }
                catch (NotSupportedException) { return Results.NotFound(); }
                catch (FileNotFoundException) { return Results.NotFound(); }
                catch (DirectoryNotFoundException) { return Results.NotFound(); }
                catch (IOException) { return Results.StatusCode(StatusCodes.Status502BadGateway); }

                var scaled = await scaling.ScalePageAsync(new MemoryStream(bytes, writable: false), maxWidth);
                return Results.Stream(scaled, "image/jpeg");
            });

            // ── EPUB resources ────────────────────────────────────────────────────────────────────────────
            media.MapGet("/epub/{id:int}/{**path}", async (
                string token, int id, string path, HttpContext ctx,
                MediaAccess access, BooksDb db, EpubReaderService epub) =>
            {
                var (tokenValid, item) = await access.ResolveAsync(db, token, id, ctx.RequestAborted);
                if (!tokenValid) return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (item == null) return Results.NotFound();
                if (!".epub".Equals(item.Extension, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
                if (string.IsNullOrWhiteSpace(path)) return Results.NotFound();

                // The href is normalized (…/.. resolved) before it is looked up, so it can only ever name
                // something INSIDE the container — the lookup is against the parsed package, not the file system.
                var normalized = EpubReaderService.NormalizeHref(path);
                if (normalized.Length == 0) return Results.NotFound();

                var ticks = item.FileModifiedAt?.Ticks ?? 0;
                var etag = $"\"{id}_epr_{Uri.EscapeDataString(normalized)}_{ticks}\"";
                ctx.Response.Headers.CacheControl = "private, max-age=86400";
                ctx.Response.Headers.ETag = etag;
                if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);

                EpubResource? resource;
                try { resource = await epub.GetResourceAsync(item.Path, normalized); }
                catch (FileNotFoundException) { return Results.NotFound(); }
                catch (DirectoryNotFoundException) { return Results.NotFound(); }
                catch (IOException) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
                if (resource == null) return Results.NotFound();

                return Results.Bytes(resource.Content, resource.MimeType);
            });

            // ── download the original file ────────────────────────────────────────────────────────────────
            // GET and HEAD: a reader app checks the size and the Range support before it starts a 600 MB pull,
            // and a HEAD that 405s makes it fall back to downloading the whole file to find out.
            media.MapMethods("/download/{id:int}", ["GET", "HEAD"], async (string token, int id, HttpContext ctx, MediaAccess access, BooksDb db) =>
            {
                var (tokenValid, item) = await access.ResolveAsync(db, token, id, ctx.RequestAborted);
                if (!tokenValid) return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (item == null) return Results.NotFound();
                if (!File.Exists(item.Path)) return Results.NotFound();

                // enableRangeProcessing: a reader app resuming a 600 MB omnibus must not restart the transfer.
                // The name comes from the catalog row, so the response never discloses the share path.
                var fileName = Path.GetFileName(item.FileName is { Length: > 0 } ? item.FileName : item.Path);
                return Results.File(item.Path, "application/octet-stream", fileName,
                    lastModified: item.FileModifiedAt is { } m ? new DateTimeOffset(m, TimeSpan.Zero) : null,
                    enableRangeProcessing: true);
            });

            // Anything else under a VALID token is a route that does not exist (404), not a permission problem.
            media.MapGet("/{**rest}", (string token, string rest, MediaAccess access) =>
                access.TryOpen(token, out _) ? Results.NotFound() : Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        /// <summary>
        /// A cached file off disk with an mtime ETag. Shared by thumbnails and folder icons because both are
        /// "a generated image whose only version is when it was last written".
        /// </summary>
        private static IResult ServeFile(HttpContext ctx, string full, string contentType)
        {
            if (!File.Exists(full)) return Results.NotFound();
            var mtime = File.GetLastWriteTimeUtc(full);
            var etag = "\"" + mtime.Ticks.ToString("x") + "\"";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            ctx.Response.Headers.ETag = etag;
            return Results.File(full, contentType, lastModified: mtime, enableRangeProcessing: false);
        }
    }
}
