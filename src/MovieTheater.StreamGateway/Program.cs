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
// ConfiguredRoot, not `!= null`: the binder answers a JSON null / empty setting with "" rather than
// null, and Path.GetFullPath("") throws — at startup, taking every other lane down with it.
string? musicRootFull = ConfiguredRoot.FullPathOrNull(config["MusicRootDir"]);

// Every music lane resolves its file exactly the same way, so it is written once. The confinement
// check is the security boundary for the whole music data plane — a signed token still must not be
// able to name a path outside the root — and one copy per lane would be one place per lane for it to
// drift. Returns the absolute path plus the root-relative path the token carried (the cache key
// below is built from it), or a null path with the status the lane should answer:
// 404 = this host serves no music, or the file is gone; 403 = token refused, or the path escaped.
(string? Full, string Relative, int Status) ResolveMusicFile(string token)
{
    if (musicRootFull == null)
        return (null, "", StatusCodes.Status404NotFound);
    if (!MusicCapabilityToken.TryValidate(secret, token, out var payload) || payload is null)
        return (null, "", StatusCodes.Status403Forbidden);

    // Confinement: the resolved file must sit under the music root — a token is signed, but
    // defense-in-depth costs one string compare.
    var full = Path.GetFullPath(Path.Combine(musicRootFull,
        payload.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!full.StartsWith(musicRootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        return (null, "", StatusCodes.Status403Forbidden);
    if (!File.Exists(full))
        return (null, "", StatusCodes.Status404NotFound);
    return (full, payload.RelativePath, StatusCodes.Status200OK);
}

app.MapMethods($"/s/{{token}}/{MusicStreamRoutes.File}", new[] { "GET", "HEAD" }, (HttpContext context, string token) =>
{
    var (full, _, status) = ResolveMusicFile(token);
    if (full == null)
        return Results.StatusCode(status);

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
    var (full, _, status) = ResolveMusicFile(token);
    if (full == null)
    {
        context.Response.StatusCode = status;
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

// ── Music fMP4 lane (Media Source Extensions) ────────────────────────────────────────────────────
// The SAME audio in a fragmented-MP4 container, so the player can append tracks into one
// SourceBuffer and a track boundary stops being a JavaScript event.
//
// ⚠ This is a REMUX. ffmpeg runs `-c:a copy`: the FLAC/MP3 frames are copied into moof/mdat boxes
// without being decoded, so the audio is bit-identical to the file on disk and FLAC stays lossless.
// The container is the only thing that changes, and only because MSE cannot accept a raw .flac.
// It is I/O rather than CPU, so it is far cheaper than the transcode lane above — but it is still a
// process per request, so it shares that lane's concurrency cap.
//
// Streaming stdout means no Range and no Content-Length; MSE does not need either, because the
// player fetches the whole response and appends it.
app.MapMethods($"/s/{{token}}/{MusicStreamRoutes.Fmp4}", new[] { "GET", "HEAD" }, async (HttpContext context, string token) =>
{
    if (musicRootFull == null || string.IsNullOrWhiteSpace(ffmpegPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var (full, _, status) = ResolveMusicFile(token);
    if (full == null)
    {
        context.Response.StatusCode = status;
        return;
    }

    // ⚠ FLAC only, and this refusal is load-bearing (music-mse-plan.md §"Mixed FLAC/MP3 queues").
    // ffmpeg will cheerfully remux an mp3 into an fMP4 — and the result is MP3-in-MP4 (`mp4a.6B`),
    // which Chrome is MEASURED not to support. Those bytes append into a SourceBuffer and then fail
    // to play, i.e. a client routing bug would surface as a dead boundary mid-album rather than as a
    // failed request. Refusing at fetch time makes it loud and lands the client on the ladder
    // instead. 409 rather than 403: nothing is wrong with the capability, this lane just cannot
    // carry this file — and the text says nothing about the file or where it lives.
    if (!string.Equals(Path.GetExtension(full), ".flac", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        return;
    }

    if (!await transcodeSlots.WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = "5";
        return;
    }

    try
    {
        context.Response.ContentType = "audio/mp4";
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
        // empty_moov + default_base_moof + frag_every_frame produce an initialisation segment
        // followed by self-contained fragments — the shape MSE expects from a stream that is being
        // written as it is read. `-c:a copy` is the whole point: no decode, no re-encode.
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-i", full,
            "-map", "a:0", "-c:a", "copy", "-f", "mp4",
            "-movflags", "empty_moov+default_base_moof+frag_keyframe+delay_moov",
            "-frag_duration", "1000000",
            "pipe:1",
        })
            psi.ArgumentList.Add(arg);

        using var ffmpeg = System.Diagnostics.Process.Start(psi);
        if (ffmpeg == null)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }
        try
        {
            _ = ffmpeg.StandardError.ReadToEndAsync();
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(context.Response.Body, 64 * 1024, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The listener skipped or closed the tab — normal.
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

// ── Music universal lane (music-mse-plan.md §"What the incumbents do") ───────────────────────────
// The bottom row of the treatment matrix: the audio RE-ENCODED to AAC in a fragmented MP4
// (`mp4a.40.2`, 44.1 kHz, channels preserved) — the one shape every MSE browser accepts. It exists
// so "this format has no MSE treatment" stops being a category: .wma, .ape, odd sample rates, and
// MP3 on a Firefox whose MSE has no MP3 decoder all become appendable here, which turns the fragile
// cross-engine boundary into a rare fallback instead of a routine event in a mixed queue. It also
// gives a browser that refuses a changeType or a rate switch a homogeneous-session mode: run the
// WHOLE queue through this lane and there are zero switches left to survive.
//
// ⚠ Unlike the two `-c:a copy` lanes this DECODES AND RE-ENCODES — it is not bit-perfect, and for a
// FLAC it is lossy. That trade is the plan's, deliberately: measured against "playback that never
// stops", continuity outranks fidelity wherever the bit-perfect route is the one that can stop. The
// client only routes here when a bit-perfect treatment isn't proven.
//
// 256 kbps because that is what the incumbents ship at their top web tier and is transparent for
// almost all material; -ar 44100 normalizes the rate (the switch a SourceBuffer may refuse), and no
// -ac so channel count survives.
string? universalCacheDir = config["MusicUniversalCacheDir"];
// Generous on purpose: this is a bound to stop a runaway, not a working-set target. Unset dir = no
// caching at all, which is still CORRECT — every request just re-encodes.
long universalCacheMaxBytes =
    (long)(int.TryParse(config["MusicUniversalCacheMaxMB"], out var ucmb) && ucmb > 0 ? ucmb : 20480) * 1024 * 1024;
if (!string.IsNullOrWhiteSpace(universalCacheDir))
{
    try
    {
        Directory.CreateDirectory(universalCacheDir);
        universalCacheDir = Path.GetFullPath(universalCacheDir);
    }
    catch
    {
        // An unusable cache directory must not take the lane down with it — it just means no cache.
        universalCacheDir = null;
    }
}
else universalCacheDir = null;

// Where this file's universal encode lives, or null when caching is off. Keyed by (relative path,
// mtime, lane): mtime is what makes a re-ripped or re-tagged file miss instead of serving the old
// encode, and the lane is in the key so another lane can share the directory later without
// colliding. Hashed rather than mirrored because a flat, fixed-length name has no path length,
// character-set or layout problems of its own to solve.
string? UniversalCachePath(string relativePath, string sourceFull)
{
    if (universalCacheDir == null) return null;
    try
    {
        var key = $"{MusicStreamRoutes.Universal}|{relativePath.ToLowerInvariant()}|{File.GetLastWriteTimeUtc(sourceFull).Ticks}";
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return Path.Combine(universalCacheDir, hash[..32] + ".mp4");
    }
    catch
    {
        return null;
    }
}

// The cap is enforced by LRU eviction, not by refusing to add: a cache that silently stops adding
// at its cap turns back into an encode-per-request lane for every NEW track, forever — the exact
// failure the cache exists to remove. Recency comes from touching LastWriteTimeUtc on every hit
// (the files are write-once, so an untouched LastWrite would be the encode time and a nightly
// favorite would evict as readily as a one-off), which keeps the signal under this code's control
// instead of depending on NTFS access-time tracking being enabled. Deletion is guarded to files
// THIS code plausibly wrote: the 32-hex-digit ".mp4" naming, plus ".part" temps a killed encode
// strands once they have gone stale. A file that refuses to delete (an open reader) is skipped —
// the next miss retries. Evicts down to 90% of the cap so one pass buys headroom for many adds
// rather than re-running at the boundary on every miss. Measured per cache miss (a miss already
// costs a 1–2 s encode, so an enumeration is noise) rather than kept in a counter that would
// drift out of step with the directory.
bool UniversalCacheMakeRoom()
{
    if (universalCacheDir == null) return false;
    try
    {
        var dir = new DirectoryInfo(universalCacheDir);
        var ours = dir.EnumerateFiles("*.mp4")
            .Where(f => f.Name.Length == 36 && f.Name[..32].All(Uri.IsHexDigit))
            .ToList();
        foreach (var part in dir.EnumerateFiles("*.part"))
        {
            try { if (part.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) part.Delete(); } catch { /* still being written, or an open reader */ }
        }
        var total = ours.Sum(f => f.Length);
        if (total < universalCacheMaxBytes) return true;
        var floor = (long)(universalCacheMaxBytes * 0.9);
        foreach (var file in ours.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= floor) break;
            try { var len = file.Length; file.Delete(); total -= len; } catch { /* open reader; skip, retry next miss */ }
        }
        return total < universalCacheMaxBytes;
    }
    catch
    {
        return false;
    }
}

app.MapMethods($"/s/{{token}}/{MusicStreamRoutes.Universal}", new[] { "GET", "HEAD" }, async (HttpContext context, string token) =>
{
    if (musicRootFull == null || string.IsNullOrWhiteSpace(ffmpegPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var (full, relative, status) = ResolveMusicFile(token);
    if (full == null)
    {
        context.Response.StatusCode = status;
        return;
    }

    // Cache hit: pure file serving, and Range comes free — which the streamed encode below cannot
    // offer. This is why the cache ships WITH the lane rather than after it: unlike the remux lanes,
    // every uncached request here is a real ~1–2 s of CPU, so the second play of anything (a repeat,
    // a re-queue, a reload) must not pay it again.
    var cachePath = UniversalCachePath(relative, full);
    if (cachePath != null && File.Exists(cachePath))
    {
        // Recency for the LRU eviction: write-once files never change on their own, so the touch is
        // what separates "played every night" from "encoded once in March".
        try { File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow); } catch { /* recency is best-effort */ }
        context.Response.Headers["Cache-Control"] = "private, max-age=3600";
        await Results.File(cachePath, "audio/mp4", enableRangeProcessing: true).ExecuteAsync(context);
        return;
    }

    // Shares the transcode lane's cap: same reason (a whole ffmpeg process per request), and this
    // one is the expensive kind. Over the cap the client retries rather than queuing.
    if (!await transcodeSlots.WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = "5";
        return;
    }

    string? cacheTemp = null;
    FileStream? cacheStream = null;
    var encodeCompleted = false;
    try
    {
        context.Response.ContentType = "audio/mp4";
        context.Response.Headers["Accept-Ranges"] = "none";
        context.Response.Headers["Cache-Control"] = "private, no-store";
        if (HttpMethods.IsHead(context.Request.Method)) return;

        if (cachePath != null && UniversalCacheMakeRoom())
        {
            try
            {
                // Written to a temp name and renamed only on a clean exit. A killed encode (client
                // skipped the track, ffmpeg died, the host rebooted) must never leave a half file
                // sitting at the cache path, because nothing downstream could tell it from a whole
                // one — it would just be a track that stops early, forever.
                cacheTemp = $"{cachePath}.{Guid.NewGuid():N}.part";
                cacheStream = new FileStream(cacheTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch
            {
                cacheTemp = null;
                cacheStream = null;
            }
        }

        var psi = new System.Diagnostics.ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Same fragmented-MP4 shape as the remux lane above (init segment + self-contained
        // fragments, written as they are read) — only the codec arguments differ.
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-i", full,
            "-map", "a:0", "-c:a", "aac", "-b:a", "256k", "-ar", "44100", "-f", "mp4",
            "-movflags", "empty_moov+default_base_moof+frag_keyframe+delay_moov",
            "-frag_duration", "1000000",
            "pipe:1",
        })
            psi.ArgumentList.Add(arg);

        using var ffmpeg = System.Diagnostics.Process.Start(psi);
        if (ffmpeg == null)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }
        try
        {
            _ = ffmpeg.StandardError.ReadToEndAsync();
            // Tee'd by hand rather than CopyToAsync: the listener gets the bytes as they are encoded
            // (no waiting for the whole track) and the cache gets the same bytes on the way past.
            var buffer = new byte[64 * 1024];
            var stdout = ffmpeg.StandardOutput.BaseStream;
            int read;
            while ((read = await stdout.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                // Not cancellable: an aborted request still wants the temp file closed tidily, and
                // it is about to be deleted anyway.
                if (cacheStream != null)
                    await cacheStream.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
            }
            await ffmpeg.WaitForExitAsync(context.RequestAborted);
            encodeCompleted = ffmpeg.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            // The listener skipped or closed the tab — normal.
        }
        finally
        {
            try { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); } catch { }
        }
    }
    finally
    {
        if (cacheStream != null) await cacheStream.DisposeAsync();
        if (cacheTemp != null)
        {
            try
            {
                if (encodeCompleted && cachePath != null)
                    // Two concurrent misses for the same track each write their own temp and both
                    // rename; identical bytes, last one wins. A rename that loses a race with a
                    // reader just leaves the temp to be deleted below.
                    File.Move(cacheTemp, cachePath, overwrite: true);
                else
                    File.Delete(cacheTemp);
            }
            catch
            {
                try { File.Delete(cacheTemp); } catch { /* nothing further to try */ }
            }
        }
        transcodeSlots.Release();
    }
});

// ── Family photos (photos-plan.md §2.2): token-gated file serving from two roots, and nothing else.
// The gateway stays DUMB and DB-less — it never generates a derivative, so a missing thumb is a 404
// and therefore a VISIBLE ingest gap rather than a lazy path that quietly costs a decode per request.
// Unconfigured hosts 404 both routes, exactly like the music lanes.
//
// Two roots, and which one a request resolves against is decided by the ROUTE, never by the token:
//   PhotoOriginal → PhotoRootDir      (the read-only collection; the gateway never writes to it)
//   PhotoThumb    → PhotoThumbCacheDir (derived data the ingest wrote)
// The token's Size field is carried for the site's own bookkeeping and is deliberately NOT used to
// pick a root — one fewer way a signed token could be made to point somewhere it should not.
// ConfiguredRoot, not `is string`: a JSON null binds to "" (not null), `is string` matches it, and
// Path.GetFullPath("") THROWS. Every host that took the photos appsettings without configuring photos
// would have lost the gateway at startup — movies and music with it (§2.2's "unconfigured hosts 404").
string? photoRootFull = ConfiguredRoot.FullPathOrNull(config["PhotoRootDir"]);
string? photoThumbFull = ConfiguredRoot.FullPathOrNull(config["PhotoThumbCacheDir"]);

// Confinement is the security boundary for the whole photo data plane: a token is signed, but a
// signed token must still not be able to name a path outside its root. Written once, used by both
// routes. 404 = this host serves no photos, or the file is gone; 403 = token refused, or the path
// escaped its root.
(string? Full, int Status) ResolvePhotoFile(string token, string? rootFull)
{
    if (rootFull == null)
        return (null, StatusCodes.Status404NotFound);
    if (!PhotoCapabilityToken.TryValidate(secret, token, out var payload) || payload is null)
        return (null, StatusCodes.Status403Forbidden);

    var full = PhotoPathConfinement.Resolve(rootFull, payload.RelativePath);
    if (full == null)
        return (null, StatusCodes.Status403Forbidden);
    if (!File.Exists(full))
        return (null, StatusCodes.Status404NotFound);
    return (full, StatusCodes.Status200OK);
}

app.MapMethods($"/s/{{token}}/{PhotoStreamRoutes.Thumb}", new[] { "GET", "HEAD" }, (HttpContext context, string token) =>
{
    var (full, status) = ResolvePhotoFile(token, photoThumbFull);
    if (full == null)
        return Results.StatusCode(status);

    // Derivative names carry a content key, so a given URL's bytes never change — but the URL itself
    // expires with its token, so the cache window is bounded by the capability either way.
    context.Response.Headers["Cache-Control"] = "private, max-age=3600";
    return Results.File(full, "image/webp", enableRangeProcessing: true);
});

app.MapMethods($"/s/{{token}}/{PhotoStreamRoutes.Original}", new[] { "GET", "HEAD" }, (HttpContext context, string token) =>
{
    var (full, status) = ResolvePhotoFile(token, photoRootFull);
    if (full == null)
        return Results.StatusCode(status);

    context.Response.Headers["Cache-Control"] = "private, max-age=3600";
    return Results.File(full, PhotoContentType(Path.GetExtension(full)), enableRangeProcessing: true);
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

// Content type for an ORIGINAL (photos-plan.md §2.2). Derivatives are always WebP and are typed at
// their route. An unknown extension is served as a download rather than guessed at: the collection
// holds RAW formats browsers have no opinion about, and a wrong image/* type makes a broken <img>
// where octet-stream makes a working "Download original".
static string PhotoContentType(string extension) => extension.ToLowerInvariant() switch
{
    ".jpg" or ".jpeg" or ".jpe" => "image/jpeg",
    ".png" => "image/png",
    ".gif" => "image/gif",
    ".webp" => "image/webp",
    ".bmp" => "image/bmp",
    ".tif" or ".tiff" => "image/tiff",
    ".heic" => "image/heic",
    ".heif" => "image/heif",
    ".avif" => "image/avif",
    _ => "application/octet-stream",
};

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
