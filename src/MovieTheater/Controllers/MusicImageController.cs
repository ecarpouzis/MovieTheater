using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Db;
using MovieTheater.Music;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Serves album art (music-plan.md §2.5). Same shape as <see cref="PosterImageController"/>: a
    /// bounded static byte cache keyed by id+variant+version, an ETag from the file's mtime, and
    /// <c>immutable</c> caching for a versioned (<c>?v=</c>) request. Unauthenticated like the other
    /// image routes — the art is not the media.
    ///
    /// <para><b>Art is fetched lazily by THIS process, exactly like <see cref="ArcadeImageController"/>.</b>
    /// That is not a style choice: the images mount is a shared volume in prod and there is no pod
    /// access from a dev box, so art written by a local CLI run can never reach prod (the same reason
    /// <c>/API/Admin/IngestReview/BackfillPosters</c> exists for movie posters). On the first view of an
    /// album with no art on the mount we run the remote lookup, downscale, cache both sizes, and flip
    /// <c>HasArt</c>. The <c>music-art</c> CLI and the admin backfill endpoint are the same fetch run
    /// ahead of time; either path works.</para>
    ///
    /// <para>Art files are written once and never regenerated (project rule), so a versioned URL really
    /// is immutable and the memory cache can never go stale.</para>
    /// </summary>
    public class MusicImageController : ControllerBase
    {
        // Album art is small (a 300px thumb is a few KB); 64 MB holds the whole catalog's thumbs.
        private static readonly MemoryCache ArtByteCache = new(new MemoryCacheOptions
        {
            SizeLimit = 64L * 1024 * 1024,
        });

        private static readonly HttpClient Http = MusicRemoteArt.CreateHttp();

        /// <summary>Albums this process already asked the internet about and got nothing for, so a
        /// grid full of art-less albums 404s fast instead of re-querying. The durable half of the
        /// negative cache is <c>MusicAlbum.ArtCheckedUtc</c>; this just saves the DB round trip.</summary>
        private static readonly ConcurrentDictionary<int, byte> NoArt = new();

        private readonly MovieDb movieDb;
        private readonly MovieTheaterConfiguration config;

        public MusicImageController(MovieDb movieDb, MovieTheaterConfiguration config)
        {
            this.movieDb = movieDb;
            this.config = config;
        }

        [HttpGet("/MusicImage/{albumId}")]
        public Task<IActionResult> Main(int albumId) => ArtResponse(albumId, thumbnail: false);

        [HttpGet("/MusicImageThumb/{albumId}")]
        public Task<IActionResult> Thumb(int albumId) => ArtResponse(albumId, thumbnail: true);

        private async Task<IActionResult> ArtResponse(int albumId, bool thumbnail)
        {
            var path = MusicArtStore.PathFor(config, albumId, thumbnail);
            if (path == null) return NotFound();

            bool versioned = Request.Query.TryGetValue("v", out var ver) && !string.IsNullOrEmpty(ver);
            string cacheKey = versioned ? $"music|{(thumbnail ? "s" : "m")}|{albumId}|{ver}" : null;
            if (cacheKey != null && ArtByteCache.TryGetValue(cacheKey, out byte[] cached) && cached != null)
            {
                Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                return File(cached, "image/png");
            }

            // Not on the mount yet — try to put it there. Costs one remote lookup on the first view.
            if (!System.IO.File.Exists(path))
            {
                if (!await TryFetchArtAsync(albumId)) return NotFound();
                if (!System.IO.File.Exists(path)) return NotFound();
            }

            var etag = $"\"{System.IO.File.GetLastWriteTimeUtc(path).Ticks}\"";
            Response.Headers["Cache-Control"] = versioned ? "public, max-age=31536000, immutable" : "public, max-age=3600";
            Response.Headers["ETag"] = etag;
            if (Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == etag)
                return StatusCode(304);

            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(path); }
            catch (IOException) { return NotFound(); }

            if (cacheKey != null)
                ArtByteCache.Set(cacheKey, bytes, new MemoryCacheEntryOptions
                {
                    Size = bytes.Length,
                    SlidingExpiration = TimeSpan.FromHours(12),
                });

            return File(bytes, "image/png");
        }

        /// <summary>One lazy remote fetch for an album whose art isn't on the mount. False means "no art
        /// now" for any reason — already known missing, gate busy, or the lookup came back empty.</summary>
        private async Task<bool> TryFetchArtAsync(int albumId)
        {
            if (NoArt.ContainsKey(albumId)) return false;

            var album = await movieDb.MusicAlbums.Include(a => a.Artist).FirstOrDefaultAsync(a => a.Id == albumId);
            if (album == null) { NoArt.TryAdd(albumId, 0); return false; }

            // Durable negative cache: the internet already declined this one. (HasArt with no file means
            // the art exists on some OTHER mount — a local CLI run — so this mount still needs to fetch.)
            if (album.ArtCheckedUtc != null && !album.HasArt) { NoArt.TryAdd(albumId, 0); return false; }

            // Don't queue behind another album's lookup (or behind a bulk warm); a web request must never
            // stack up behind the 1 req/s limit — the caller just shows the placeholder this time.
            if (!await MusicRemoteArt.Gate.WaitAsync(0)) return false;
            try
            {
                return await MusicArtFill.FetchAndStoreAsync(movieDb, config, Http, album, MusicRemoteArt.SpaceCallAsync);
            }
            finally
            {
                MusicRemoteArt.Gate.Release();
            }
        }
    }
}
