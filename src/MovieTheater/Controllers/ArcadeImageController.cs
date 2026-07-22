using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Arcade;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.Igdb;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Serves arcade box art (arcade-plan.md §5) from <c>ArcadeGame.BoxArtPath</c> (a path relative to the
    /// posters mount). At ~49k games we don't bulk-store art up front — instead this route <b>lazily</b>
    /// fetches box art from libretro-thumbnails, downscales it to a thumbnail, and caches it on the FIRST
    /// view of a game (via <see cref="ArcadeBoxArt"/>). Games whose art doesn't exist (or whose system has
    /// no repo — e.g. arcade) are negatively cached so they 404 fast and the card shows its placeholder.
    /// The bulk <c>arcade-boxart</c> CLI is the same fetch, run ahead of time; either path works.
    /// </summary>
    public class ArcadeImageController : ControllerBase
    {
        private const int ThumbPx = 300;   // fills the ~200px card width crisply (portrait boxes were soft at 220)

        // One long-lived client for the on-demand fetches; a per-game negative cache so a miss is tried once.
        private static readonly HttpClient Http = CreateHttp();
        private static readonly ConcurrentDictionary<int, byte> NoArt = new();

        // Lazy shared IGDB client (token cached across requests) for the cover fallback. Null if unconfigured.
        private static IgdbClient _igdb;
        private static IgdbClient Igdb(MovieTheaterConfiguration config) =>
            _igdb ??= (IgdbClient.IsConfigured(config) ? new IgdbClient(Http, config.IgdbClientId, config.IgdbClientSecret) : null);

        // Last cascade step: SteamGridDB community covers (searched by title) for the homebrew/obscure tail.
        private static SteamGridDbClient _sgdb;
        private static SteamGridDbClient Sgdb(MovieTheaterConfiguration config) =>
            _sgdb ??= (SteamGridDbClient.IsConfigured(config) ? new SteamGridDbClient(Http, config.SteamGridDbApiKey) : null);

        // Absolute-last cascade step: web image search for covers no game DB has.
        private static GoogleImageCoverClient _gimg;
        private static GoogleImageCoverClient Gimg(MovieTheaterConfiguration config) =>
            _gimg ??= (GoogleImageCoverClient.IsConfigured(config)
                ? new GoogleImageCoverClient(Http, config.GoogleSearchApiKey, config.BoxArtImageSearchEngineId) : null);

        // System code → a search hint for the web image query ("Doom" alone is ambiguous; "Doom PlayStation" isn't).
        private static string SystemHint(string system) => system switch
        {
            "nes" => "NES", "snes" => "SNES", "n64" => "Nintendo 64", "gc" => "GameCube", "gb" => "Game Boy",
            "gbc" => "Game Boy Color", "gba" => "Game Boy Advance", "fds" => "Famicom", "vb" => "Virtual Boy",
            "genesis" => "Sega Genesis", "sms" => "Sega Master System", "gg" => "Game Gear", "segacd" => "Sega CD",
            "sega32x" => "Sega 32X", "sg1000" => "SG-1000", "dc" => "Dreamcast", "naomi" => "arcade", "atomiswave" => "arcade",
            "ps1" => "PlayStation", "ps2" => "PlayStation 2", "psp" => "PSP",
            "pce" => "TurboGrafx-16", "ngpc" => "Neo Geo Pocket", "wsc" => "WonderSwan", "neogeo" => "Neo Geo",
            "a2600" => "Atari 2600", "a7800" => "Atari 7800", "lynx" => "Atari Lynx", "arcade" => "arcade",
            _ => system,
        };

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-boxart/1.0");
            return h;
        }

        private readonly MovieDb movieDb;
        private readonly MovieTheaterConfiguration config;

        public ArcadeImageController(MovieDb movieDb, MovieTheaterConfiguration config)
        {
            this.movieDb = movieDb;
            this.config = config;
        }

        [HttpGet("/ArcadeImage/{id}")]
        public async Task<IActionResult> BoxArt(int id)
        {
            if (string.IsNullOrEmpty(config.MoviePostersDir)) return NotFound();
            var root = Path.GetFullPath(config.MoviePostersDir);

            var game = await movieDb.ArcadeGames.FirstOrDefaultAsync(g => g.Id == id);
            if (game == null) return NotFound();

            // A card = one game across its several ROM versions (same System+CollapseKey — the same folded key
            // the lobby groups on, so cosmetically-different dumps share one card's art too). Box art is shared
            // by the whole card: any sibling's cached file serves, and a fresh fetch is stored ONCE per card
            // (keyed by the card's lowest version id) so we don't keep a near-duplicate PNG per region/revision.
            var siblings = await movieDb.ArcadeGames
                .Where(g => g.System == game.System && g.CollapseKey == game.CollapseKey)
                .Select(g => new { g.Id, g.BoxArtPath, g.Region, g.CloudRetroGameKey, g.IgdbId, g.Notes })
                .ToListAsync();
            var cardId = siblings.Count > 0 ? siblings.Min(s => s.Id) : id;
            var anchor = siblings.OrderBy(s => s.Id).FirstOrDefault();  // metadata (IgdbId/Notes) lives here

            // 1. Any existing art for this card (the requested row, then any sibling, then the canonical
            //    card file) → serve it. Reuses art already downloaded under the old per-row scheme.
            foreach (var rel in siblings.Where(s => !string.IsNullOrWhiteSpace(s.BoxArtPath)).Select(s => s.BoxArtPath))
            {
                var cached = ResolveUnderRoot(root, rel!);
                if (cached != null && System.IO.File.Exists(cached)) return ServeFile(cached);
            }
            var cardRel = $"arcade/{game.System}/{cardId}.png";
            var cardPath = ResolveUnderRoot(root, cardRel);
            if (cardPath != null && System.IO.File.Exists(cardPath)) return ServeFile(cardPath);

            // 2. Already tried everything for this card → placeholder (fast 404).
            if (NoArt.ContainsKey(cardId)) return NotFound();

            // 3. First view → libretro-thumbnails match by title across the card's versions (skipped for
            //    repo-less systems like arcade, which fall straight to IGDB below).
            byte[] thumb = null;
            if (ArcadeBoxArt.HasRepo(game.System))
            {
                var index = await ArcadeBoxArtIndex.EnsureBuiltAsync(Http, root, game.System);
                thumb = await ArcadeBoxArt.TryFetchThumbnailForCardAsync(
                    Http, game.System, game.Title,
                    siblings.Select(s => s.Region), siblings.Select(s => s.CloudRetroGameKey), ThumbPx, index);
            }

            // 3b. IGDB cover fallback — for cards libretro can't source (arcade, the PSP/homebrew tail) OR
            //     whose libretro box the audit flagged as a mis-shaped outlier (Notes = boxart-prefer-igdb).
            //     Uses the IgdbId the enrichment already resolved, so no title re-search.
            bool preferIgdb = string.Equals(anchor?.Notes, "boxart-prefer-igdb", StringComparison.Ordinal);
            if ((thumb == null || preferIgdb) && anchor?.IgdbId is long igdbId && Igdb(config) is IgdbClient igdb)
            {
                try
                {
                    var imageId = await igdb.CoverImageIdAsync(igdbId);
                    if (imageId != null)
                        thumb = ArcadeBoxArt.Thumbnail(await Http.GetByteArrayAsync(IgdbClient.CoverUrl(imageId)), ThumbPx);
                }
                catch { /* IGDB hiccup — fall through to placeholder */ }
            }

            // 3c. SteamGridDB fallback — community covers for anything libretro + IGDB missed (homebrew,
            //     multicarts, obscure/digital). Searched by title, so it works even with no IGDB match.
            if (thumb == null && Sgdb(config) is SteamGridDbClient sgdb)
            {
                try
                {
                    var url = await sgdb.FindCoverUrlAsync(game.Title);
                    if (url != null) thumb = ArcadeBoxArt.Thumbnail(await Http.GetByteArrayAsync(url), ThumbPx);
                }
                catch { /* SteamGridDB hiccup — fall through to placeholder */ }
            }

            // 3d. Web image search — the absolute last resort for a cover no game DB has. Best-effort top hit.
            if (thumb == null && Gimg(config) is GoogleImageCoverClient gimg)
            {
                try
                {
                    var url = await gimg.FindBoxArtUrlAsync(game.Title, SystemHint(game.System));
                    if (url != null) thumb = ArcadeBoxArt.Thumbnail(await Http.GetByteArrayAsync(url), ThumbPx);
                }
                catch { /* couldn't fetch/decode — placeholder */ }
            }

            if (thumb == null) { NoArt.TryAdd(cardId, 0); return NotFound(); }

            if (cardPath != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cardPath)!);
                    await System.IO.File.WriteAllBytesAsync(cardPath, thumb);
                    game.BoxArtPath = cardRel;
                    await movieDb.SaveChangesAsync();
                }
                catch { /* couldn't cache (mount read-only?) — still serve the bytes we fetched */ }
            }
            Response.Headers["Cache-Control"] = "public, max-age=86400";
            return File(thumb, "image/png");
        }

        // Resolve a stored-relative path under the posters root, rejecting traversal ("../") escapes.
        private static string? ResolveUnderRoot(string root, string rel)
        {
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return null;
            return full;
        }

        private IActionResult ServeFile(string full)
        {
            var etag = $"\"{System.IO.File.GetLastWriteTimeUtc(full).Ticks}\"";
            if (Request.Headers.TryGetValue("If-None-Match", out var inm) && inm == etag) return StatusCode(304);
            Response.Headers["Cache-Control"] = "public, max-age=86400";
            Response.Headers["ETag"] = etag;
            return PhysicalFile(full, ContentTypeFor(full));
        }

        private static string ContentTypeFor(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png",
            };
    }
}
