using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Core;
using MovieTheater.Db;
using MovieTheater.Music;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Music control plane (music-plan.md §2.4). Every endpoint requires a password-verified session
    /// like the other streaming verticals; there is no age gate — it's a curated music collection.
    /// The data plane is the StreamGateway's /s/{token}/MusicFile route (§2.1): Stream/Start hands
    /// the player a signed capability URL and audio bytes never touch this server.
    /// </summary>
    [Authorize(Policy = "StreamingUser")]
    public class MusicController : Controller
    {
        /// <summary>Generous because one minted URL serves the whole listening session for that track
        /// (repeat/seek included); the capability is peer-locked to a path under the music root.</summary>
        private const int TokenLifetimeSeconds = 6 * 3600;

        private readonly MovieDb movieDb;
        private readonly MovieTheaterConfiguration config;

        public MusicController(MovieDb movieDb, MovieTheaterConfiguration config)
        {
            this.movieDb = movieDb;
            this.config = config;
        }

        private int? GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
        }

        private bool MusicConfigured =>
            !string.IsNullOrEmpty(config.StreamGatewayBaseUrl) && !string.IsNullOrEmpty(config.StreamTokenSecret);

        public class StartRequest
        {
            public int TrackId { get; set; }
        }

        /// <summary>N tracks in one round trip — the rolling pre-mint window's request
        /// (music-mse-plan.md §"URLs: minted while awake, never needed while asleep").</summary>
        public class StartBatchRequest
        {
            public List<int>? TrackIds { get; set; }
        }

        /// <summary>Bounds what one request can ask for. A queue is hundreds of tracks and the window
        /// only ever needs the next few hours of it, so a cap costs the caller nothing and stops a
        /// runaway client turning one POST into a full-catalog signing job.</summary>
        private const int MaxBatchTracks = 200;

        /// <summary>
        /// Receives a playback failure report the player sends about itself.
        /// </summary>
        /// <remarks>
        /// The music failures worth chasing happen on a phone with the screen off and then RECOVER,
        /// so there is nothing left to look at by the time a person can look: the track resumed, the
        /// page reloaded, and the in-memory log went with it. Every attempt to catch one by asking
        /// the listener to be watching has failed, because the moment erases itself.
        ///
        /// <para>So the player reports unprompted. It arrives as a <c>sendBeacon</c> with
        /// <c>text/plain</c> — deliberately a CORS-simple request, because the page making it is
        /// being frozen and will not survive a preflight — which is why the body is read and parsed
        /// here rather than model-bound.</para>
        ///
        /// <para>Anyone logged in may post: a report is only useful if the failing session can send
        /// it, and the failing session is by definition an ordinary listener. The payload is capped
        /// and the client rate-limits itself to one a minute.</para>
        /// </remarks>
        [HttpPost("/API/Music/Incident")]
        public async Task<IActionResult> Incident()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            using var reader = new StreamReader(Request.Body);
            var raw = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw)) return BadRequest(new { message = "Empty report." });
            // A runaway client must not be able to write unbounded rows.
            if (raw.Length > 256 * 1024) raw = raw.Substring(0, 256 * 1024);

            string kind = "unknown", summary = null, userAgent = null;
            int? trackId = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("kind", out var k)) kind = k.GetString() ?? "unknown";
                if (root.TryGetProperty("summary", out var s)) summary = s.GetString();
                if (root.TryGetProperty("userAgent", out var ua)) userAgent = ua.GetString();
                if (root.TryGetProperty("trackId", out var t) && t.ValueKind == JsonValueKind.Number)
                    trackId = t.GetInt32();
            }
            catch (JsonException)
            {
                // Keep it anyway: a report we can't parse is still evidence that something fired,
                // and the raw payload is the part worth reading.
                kind = "unparseable";
            }

            // Diagnostics, not history: rows expire. The prune rides the only path that grows the
            // table — a capped batch per report — so a quiet table costs nothing and a noisy one
            // trims itself. (Tracked RemoveRange, not ExecuteDelete+Take: the SQLite test provider
            // can't translate the latter.)
            var pruneCutoff = DateTime.UtcNow - IncidentRetention;
            var expired = await movieDb.MusicPlaybackIncidents
                .Where(i => i.CreatedUtc < pruneCutoff).Take(50).ToListAsync();
            if (expired.Count > 0) movieDb.MusicPlaybackIncidents.RemoveRange(expired);

            movieDb.MusicPlaybackIncidents.Add(new MusicPlaybackIncident
            {
                CreatedUtc = DateTime.UtcNow,
                UserId = userId,
                Kind = Truncate(kind, 40),
                Summary = Truncate(summary, 400),
                TrackId = trackId,
                UserAgent = Truncate(userAgent, 400),
                Payload = raw,
            });
            await movieDb.SaveChangesAsync();
            return Ok(new { recorded = true });
        }

        // Incident reports older than this stopped being diagnostics; the quiet post-fix table
        // stays near-empty either way, this just makes that a bound instead of luck.
        private static readonly TimeSpan IncidentRetention = TimeSpan.FromDays(180);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));

        /// <summary>What this server can actually play, so the UI stops greying out formats it
        /// could stream.</summary>
        /// <remarks>
        /// The gateway has always had the ffmpeg route and Start has always chosen it for a
        /// transcode-only track — but the client had no way to know whether this server would honour
        /// it, so it disabled every .wma/.aif track outright (92 of them here). Rather than have the
        /// UI guess, or click-and-hope into a 409, it asks once and decides.
        /// </remarks>
        [HttpGet("/API/Music/Capabilities")]
        public IActionResult Capabilities() => Ok(new
        {
            streamingConfigured = MusicConfigured,
            transcodeEnabled = config.MusicTranscodeEnabled,
            // The MSE lanes (fMP4 remux + universal AAC-fMP4), advertised so the player can route
            // without discovering them through a 404 (music-mse-plan.md §"Server work the phases
            // need"). Same server-side condition as transcodeEnabled because it is the same
            // prerequisite: both lanes are ffmpeg on the gateway.
            //
            // ⚠ A "yes" here is a statement about CONFIGURATION, not about the deployed gateway. The
            // site deploys on push and the gateway does not, so a site that is ahead of its gateway
            // will advertise a lane that 404s — which is exactly rung 4 of the plan's fallback
            // ladder, and why the client must keep degrading quietly on a 404 regardless.
            fmp4Enabled = config.MusicTranscodeEnabled,
        });

        /// <summary>
        /// The ONE spelling of a Stream/Start payload: minted for a single track and reused verbatim
        /// by the batch endpoint.
        /// </summary>
        /// <remarks>
        /// Two spellings of this object is how the batch response and the single response drift into
        /// disagreeing about a field the player routes on — and the player would then behave
        /// differently depending on which endpoint happened to mint the track.
        ///
        /// <para>All three lane URLs come from the SAME token (music-mse-plan.md §"Server work"):
        /// the capability is lane-agnostic by design and the ROUTE picks the treatment, so offering
        /// another lane costs one more string and no version skew with the gateway.</para>
        /// </remarks>
        private object BuildStartPayload(MusicTrack track, int userId)
        {
            var token = MusicCapabilityToken.Mint(config.StreamTokenSecret!, new MusicCapabilityToken.Payload(
                userId, track.Id, track.RelativePath,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TokenLifetimeSeconds));

            // The ROUTE decides how the gateway serves the bytes, so the capability itself is
            // unchanged (still 4 fields) — a browser-native format streams as a file with Range,
            // an exotic one is piped through ffmpeg as mp3 (§Phase 7). Keeping the token identical
            // is deliberate: no version skew between the site and the gateway.
            var transcode = track.RequiresTranscode;

            // fMP4 is minted for .flac and NOTHING else. MP3-in-MP4 (mp4a.6B) is measured
            // unsupported in Chrome (music-mse-plan.md §"What is measured"), so an fMP4 URL for an
            // mp3 is a URL whose bytes no browser can play — the plan's central trap. Withholding
            // the URL is the site's half of that guard; the gateway rejects the route as well, so a
            // routing bug fails loud at fetch time instead of as a SourceBuffer error at a boundary.
            var isFlac = string.Equals(track.Extension, ".flac", StringComparison.OrdinalIgnoreCase);

            return new
            {
                trackId = track.Id,
                url = MusicStreamRoutes.Url(config.StreamGatewayBaseUrl!, token, transcode),
                // Bit-perfect MSE: a container change only (-c:a copy), so FLAC stays lossless.
                fmp4Url = isFlac ? MusicStreamRoutes.Fmp4Url(config.StreamGatewayBaseUrl!, token) : null,
                // The bottom row of the treatment matrix, offered for EVERY track: the AAC-fMP4
                // re-encode any MSE browser accepts. Minting it always is the point of the
                // lane-agnostic token — the client picks a lane from its own capability probes, and
                // a client that discovers mid-queue that it needs the universal treatment must not
                // have to make a round trip (least of all on a sleeping phone) to get the URL.
                universalUrl = MusicStreamRoutes.UniversalUrl(config.StreamGatewayBaseUrl!, token),
                transcoded = transcode,
                mimeType = transcode ? "audio/mpeg" : MusicMimeTypes.FromExtension(track.Extension),
                durationSec = track.DurationSec,
                // How big the file is, so the player can decide whether it dares STREAM it.
                // Chrome's media buffer caps at 16 MiB - 32 KiB: anything larger is guaranteed to be
                // evicted and re-requested part-way through the song (proved in Caddy's log as
                // `Range: bytes=16744448-`), and a phone whose screen has gone off cannot service
                // that re-request. The player downloads those in full before playing them instead.
                sizeBytes = track.SizeBytes,
                // How many channels the browser will actually decode, so the player can size its Web
                // Audio destination to match. The transcode lane re-encodes to mp3, which tops out at
                // stereo, so a surround source served that way really is 2 by the time it lands.
                // 0/null (never backfilled, or a file that wouldn't say) means "don't know" — the
                // player leaves the destination at its stereo default.
                channels = transcode ? 2 : track.Channels,
                // The residual risk in a mixed SourceBuffer is the sample RATE, not the codec
                // (music-mse-plan.md §"Mixed FLAC/MP3 queues"): 94% of the library is 44.1 kHz, and
                // a 96 kHz track is the switch a buffer may refuse. Shipped so a route decision — and
                // Phase 1's rate-switch probe — can be about a known number rather than a guess.
                sampleRateHz = track.SampleRateHz,
                title = track.Title,
                artist = track.Artist.Name,
                album = track.Album?.Title,
            };
        }

        /// <summary>
        /// Tracks for the MSE probe page to test itself with, chosen by the server
        /// (music-mse-plan.md §Phase 1).
        /// </summary>
        /// <remarks>
        /// The probe page's first version made a person pick the tracks, which was the wrong shape
        /// twice over: the listener does not care WHICH track proves the browser can cross a
        /// changeType, and picking a 96 kHz file by hand means knowing which of 40,000 tracks is one.
        /// The gate has to be "open the page, press one thing, put the phone down" — so the choosing
        /// happens here, where the columns that describe a file already are.
        ///
        /// <para>Each slot is nullable and the page skips that sub-probe with a reason when the
        /// library has nothing to fill it: a collection with no mono file simply cannot answer the
        /// mono question, and saying so is a result, not an error.</para>
        ///
        /// <para>Files are picked SMALL-ISH: the probe fetches them whole over a phone's connection,
        /// so a 300 MB live set would measure the network rather than the browser. But not tiny —
        /// a duration floor keeps out the 4-second interstitials and fragments, which would cross a
        /// join before the measurement had begun.</para>
        /// </remarks>
        [HttpGet("/API/Music/Probe/Candidates")]
        public async Task<IActionResult> ProbeCandidates()
        {
            // Only tracks that stream as-is: the probe is about MSE treatments, and a format that
            // needs the mp3 transcode lane has no bit-perfect treatment to be testing.
            var pool = movieDb.MusicTracks.AsNoTracking()
                .Include(t => t.Artist)
                .Where(t => t.MissingSinceUtc == null && !t.RequiresTranscode);

            // Stereo first. 94% of the library is 44.1 kHz stereo, so a stereo mp3 makes the first
            // join a CODEC switch and nothing else — pairing a mono mp3 with a stereo flac would
            // silently make probe 1 a channel-count test too, and a failure would not say which of
            // the two switches the buffer refused.
            var mp3 = await PickProbeTrack(pool.Where(t => t.Extension == ".mp3" && t.Channels == 2), MinProbeBytes, 12_000_000)
                      ?? await PickProbeTrack(pool.Where(t => t.Extension == ".mp3"), MinProbeBytes, 12_000_000);
            // The bit-perfect FLAC row, deliberately at the library's ordinary rate (94% of it) so
            // probe 1 measures a CODEC switch and nothing else.
            var flac = await PickProbeTrack(
                pool.Where(t => t.Extension == ".flac" && t.SampleRateHz == 44100 && t.Channels == 2),
                5_000_000, 60_000_000);
            // The rate switch, which is the residual risk: take the HIGHEST rate the library has,
            // then the smallest file at it — a higher rate is a harder question, so a lower one
            // passing would prove less.
            var hires = await pool
                .Where(t => t.SampleRateHz > 48000 && t.DurationSec >= MinProbeSeconds)
                .OrderByDescending(t => t.SampleRateHz).ThenBy(t => t.SizeBytes)
                .FirstOrDefaultAsync();
            // A different file from the mp3 slot where the library allows it: the mono join has to be
            // a join, and the same file on both sides tests nothing.
            var monoPool = mp3 == null ? pool.Where(t => t.Channels == 1)
                : pool.Where(t => t.Channels == 1 && t.Id != mp3.Id);
            var mono = await PickProbeTrack(monoPool, MinProbeBytes, 60_000_000);

            return Ok(new
            {
                mp3 = DescribeProbeTrack(mp3),
                flac = DescribeProbeTrack(flac),
                hires = DescribeProbeTrack(hires),
                mono = DescribeProbeTrack(mono),
            });
        }

        /// <summary>Long enough to be a track rather than a fragment — a join has to be crossed
        /// mid-playback, and a 6-second file is over before the measurement starts.</summary>
        private const double MinProbeSeconds = 45;

        /// <summary>Below this a "track" is an intro, a silence file or a rip artefact.</summary>
        private const long MinProbeBytes = 1_000_000;

        /// <summary>Smallest file inside the preferred window; if the library has nothing in it, the
        /// smallest that still clears the duration floor; else nothing at all.</summary>
        private static async Task<MusicTrack?> PickProbeTrack(IQueryable<MusicTrack> pool, long minBytes, long maxBytes)
        {
            var longEnough = pool.Where(t => t.DurationSec >= MinProbeSeconds);
            return await longEnough
                       .Where(t => t.SizeBytes >= minBytes && t.SizeBytes <= maxBytes)
                       .OrderBy(t => t.SizeBytes)
                       .FirstOrDefaultAsync()
                   ?? await longEnough.OrderBy(t => t.SizeBytes).FirstOrDefaultAsync();
        }

        /// <summary>What the page needs to name a candidate on screen and to judge whether it really
        /// was the thing being tested — the rate and channel count are the labels on probe 2's
        /// measurement, not decoration.</summary>
        private static object? DescribeProbeTrack(MusicTrack? track) => track == null ? null : new
        {
            id = track.Id,
            title = track.Title,
            artist = track.Artist?.Name,
            extension = track.Extension,
            sampleRateHz = track.SampleRateHz,
            channels = track.Channels,
            sizeBytes = track.SizeBytes,
            durationSec = track.DurationSec,
        };

        [HttpPost("/API/Music/Stream/Start")]
        public async Task<IActionResult> Start([FromBody] StartRequest request)
        {
            // Without [ApiController], an unbindable body yields a null model rather than an auto-400.
            if (request == null)
                return BadRequest(new { message = "Invalid request." });
            if (!MusicConfigured)
                return StatusCode(501, new { message = "Music streaming is not configured on this server." });

            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var track = await movieDb.MusicTracks
                .Include(t => t.Artist)
                .Include(t => t.Album)
                .FirstOrDefaultAsync(t => t.Id == request.TrackId);
            if (track == null || track.MissingSinceUtc != null)
                return NotFound(new { message = "This track has no playable file." });
            if (track.RequiresTranscode && !config.MusicTranscodeEnabled)
                return StatusCode(409, new { message = "This format can't be streamed yet." });

            return Ok(BuildStartPayload(track, userId.Value));
        }

        /// <summary>
        /// N track ids → N Stream/Start payloads, one round trip. The rolling pre-mint window's
        /// request (music-mse-plan.md §"URLs: minted while awake, never needed while asleep").
        /// </summary>
        /// <remarks>
        /// Minting is still exactly what it has always been — sign a capability; no play count, no
        /// server-side session — which is why signing a queue's worth ahead of time is free. The
        /// reason to want them ahead of time is the sleeping phone: a mint is a JS fetch, the least
        /// reliable operation on a backgrounded renderer, so no route may be allowed to NEED one
        /// while asleep. A 500-track queue becomes a handful of requests made while awake.
        ///
        /// <para>Unknown, missing and not-yet-streamable tracks are SKIPPED rather than failing the
        /// batch: the window is filled from a queue that may contain any of those, and a queue with
        /// one dead track in it must still get URLs for the other 199. The caller matches responses
        /// to requests by <c>trackId</c> and treats an absent id as "no URL, fall back" — which is
        /// what it must do for a 404 from the single endpoint anyway.</para>
        /// </remarks>
        [HttpPost("/API/Music/Stream/StartBatch")]
        public async Task<IActionResult> StartBatch([FromBody] StartBatchRequest request)
        {
            if (request?.TrackIds == null)
                return BadRequest(new { message = "Invalid request." });
            if (!MusicConfigured)
                return StatusCode(501, new { message = "Music streaming is not configured on this server." });

            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            // Distinct, in the order asked for: the caller's order IS the queue order, and the window
            // is filled front-first, so preserving it lets a truncated response still be useful.
            var ids = request.TrackIds.Distinct().Take(MaxBatchTracks).ToList();
            if (ids.Count == 0)
                return Ok(new { tracks = Array.Empty<object>(), skipped = Array.Empty<int>() });

            var tracks = await movieDb.MusicTracks
                .Include(t => t.Artist)
                .Include(t => t.Album)
                .Where(t => ids.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id);

            var payloads = new List<object>(ids.Count);
            var skipped = new List<int>();
            foreach (var id in ids)
            {
                if (!tracks.TryGetValue(id, out var track)
                    || track.MissingSinceUtc != null
                    || (track.RequiresTranscode && !config.MusicTranscodeEnabled))
                {
                    skipped.Add(id);
                    continue;
                }
                payloads.Add(BuildStartPayload(track, userId.Value));
            }

            // `skipped` is not an error list — it is the answer to "why is this id absent", which the
            // client would otherwise have to infer from a hole in the response.
            return Ok(new { tracks = payloads, skipped });
        }

        // ── The kind facet (MusicArtist.Kind) ────────────────────────────────────────────────────
        // The library is ONE tree, so 22 George Carlin records and the Ender novels sat in the
        // artist grid between Garbage and Orbital. Kind is the only thing separating them, and the
        // DEFAULT — no ?kind= at all — is "music", i.e. the untagged rows. That direction matters:
        // browsing must not depend on anyone having classified anything, and a shelf you have to
        // opt out of is one nobody opts out of.

        /// <summary>Escape hatch for a caller that genuinely wants the whole tree back.</summary>
        private const string KindAll = "all";

        /// <summary>
        /// Applies the ?kind= facet: absent ⇒ music only, "all" ⇒ no filter, otherwise that kind.
        /// </summary>
        /// <remarks>
        /// Null-means-music is what makes this cheap: excluding a shelf is <c>Kind IS NULL</c>, which
        /// needs no backfill and no "music" literal written 771 times, and a kind invented tomorrow
        /// drops out of the default browse the moment it is applied, without this method changing.
        /// </remarks>
        private static IQueryable<MusicArtist> WhereKind(IQueryable<MusicArtist> artists, string? kind) =>
            kind == KindAll ? artists
            : string.IsNullOrEmpty(kind) ? artists.Where(a => a.Kind == null)
            : artists.Where(a => a.Kind == kind);

        /// <summary>True for a facet this server understands. An unknown one is a 400 rather than an
        /// empty shelf: a typo that silently returns nothing reads exactly like an empty library.</summary>
        private static bool KindIsValid(string? kind) =>
            string.IsNullOrEmpty(kind) || kind == KindAll || MusicArtistKinds.IsKnown(kind);

        [HttpGet("/API/Music/Artists")]
        public async Task<IActionResult> Artists(string? kind = null)
        {
            if (!KindIsValid(kind))
                return BadRequest(new { message = $"Unknown kind '{kind}'." });

            // Every artist card wears a cover: its alphabetically-first album that HAS art (§2.5).
            // "First album WITH art" rather than "the art of album #1" on purpose — art coverage is
            // partial and fills in lazily, so anchoring on album #1 would leave most artists blank
            // while a perfectly good cover sat one row down. One pass over the art-bearing albums
            // grouped in memory (1.3k rows), not a correlated subquery per artist.
            var faces = (await movieDb.MusicAlbums.AsNoTracking()
                    .Where(al => al.HasArt)
                    .OrderBy(al => al.ArtistId).ThenBy(al => al.Title)
                    .Select(al => new { al.ArtistId, al.Id, al.DominantColor })
                    .ToListAsync())
                .GroupBy(al => al.ArtistId)
                .ToDictionary(g => g.Key, g => g.First());

            // 333 artists — small enough to ship whole; the client groups/filters (BoardGames pattern).
            var artists = await WhereKind(movieDb.MusicArtists.AsNoTracking(), kind)
                .OrderBy(a => a.SortName)
                .Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    sortName = a.SortName,
                    folderName = a.FolderName,
                    yearRange = a.YearRange,
                    kind = a.Kind,
                    albumCount = movieDb.MusicAlbums.Count(al => al.ArtistId == a.Id),
                    trackCount = movieDb.MusicTracks.Count(t => t.ArtistId == a.Id && t.MissingSinceUtc == null),
                })
                .ToListAsync();

            return Ok(artists.Select(a =>
            {
                faces.TryGetValue(a.id, out var face);
                return new
                {
                    a.id,
                    a.name,
                    a.sortName,
                    a.folderName,
                    a.yearRange,
                    a.kind,
                    a.albumCount,
                    a.trackCount,
                    artAlbumId = face?.Id,
                    hasArt = face != null,
                    dominantColor = face?.DominantColor,
                };
            }));
        }

        [HttpGet("/API/Music/Artist/{id}")]
        public async Task<IActionResult> Artist(int id)
        {
            var artist = await movieDb.MusicArtists.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
            if (artist == null)
                return NotFound();

            var albums = await movieDb.MusicAlbums.AsNoTracking()
                .Where(a => a.ArtistId == id)
                .OrderBy(a => a.Year).ThenBy(a => a.Title)
                .Select(a => new
                {
                    id = a.Id,
                    title = a.Title,
                    year = a.Year,
                    tag = a.Tag,
                    folderPath = a.FolderPath,
                    hasArt = a.HasArt,
                    dominantColor = a.DominantColor,
                    trackCount = movieDb.MusicTracks.Count(t => t.AlbumId == a.Id),
                })
                .ToListAsync();

            // Tracks sitting directly in the artist folder — real content (1,010 across the library),
            // not an error state; surfaced as their own "Loose tracks" section.
            var looseTracks = await movieDb.MusicTracks.AsNoTracking()
                // NOT InTrackOrder: these are files that belong to no album, so a track number on one
                // of them describes some other record's running order and would jump it ahead of 25
                // untagged siblings. The file name is the only key they share. Id keeps it total.
                .Where(t => t.ArtistId == id && t.AlbumId == null)
                .OrderBy(t => t.FileName).ThenBy(t => t.Id)
                .Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    durationSec = t.DurationSec,
                    codec = t.Codec,
                    requiresTranscode = t.RequiresTranscode,
                    missing = t.MissingSinceUtc != null,
                })
                .ToListAsync();

            return Ok(new
            {
                id = artist.Id,
                name = artist.Name,
                sortName = artist.SortName,
                folderName = artist.FolderName,
                yearRange = artist.YearRange,
                // So a drilled-in page knows which shelf it belongs to and can send you back to it.
                kind = artist.Kind,
                albums,
                looseTracks,
            });
        }

        [HttpGet("/API/Music/Search")]
        public async Task<IActionResult> Search(string? q = null, string? kind = null)
        {
            if (!KindIsValid(kind))
                return BadRequest(new { message = $"Unknown kind '{kind}'." });

            var term = (q ?? "").Trim();
            if (term.Length < 2)
                return Ok(new { tracks = Array.Empty<object>() });

            // Track-title search only: the client already holds all artists/albums and matches those
            // itself; songs are the one thing it can't (20k+ rows).
            var scoped = movieDb.MusicTracks.AsNoTracking().AsQueryable();
            // Search follows the shelf you are standing on. A search from the music library that
            // returned 429 Carlin bits would be the browse-pollution problem back again, one input
            // to the left — and a search from inside Comedy that returned nothing would be worse.
            if (kind != KindAll)
                scoped = string.IsNullOrEmpty(kind)
                    ? scoped.Where(t => t.Artist.Kind == null)
                    : scoped.Where(t => t.Artist.Kind == kind);

            var tracks = await scoped
                .Where(t => t.Title.Contains(term) && t.MissingSinceUtc == null)
                .OrderBy(t => t.Title).ThenBy(t => t.Id)
                .Take(100)
                .Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    durationSec = t.DurationSec,
                    requiresTranscode = t.RequiresTranscode,
                    artistId = t.ArtistId,
                    artistName = t.Artist.Name,
                    albumId = t.AlbumId,
                    albumTitle = t.Album != null ? t.Album.Title : null,
                })
                .ToListAsync();

            return Ok(new { tracks });
        }

        [HttpGet("/API/Music/Albums")]
        public async Task<IActionResult> Albums(string? q = null, int page = 0, int pageSize = 60, string? kind = null)
        {
            if (!KindIsValid(kind))
                return BadRequest(new { message = $"Unknown kind '{kind}'." });

            // Ceiling above the whole catalog (1.3k albums): the library page loads it once and
            // filters client-side, the sanctioned pattern for a modest catalog (BoardGames).
            pageSize = Math.Clamp(pageSize, 1, 5000);
            // The kind lives on the ARTIST, so the album grid inherits it through the navigation —
            // a comedian's records are comedy records without anyone tagging each one.
            var query = movieDb.MusicAlbums.AsNoTracking().Include(a => a.Artist).AsQueryable();
            if (kind != KindAll)
                query = string.IsNullOrEmpty(kind)
                    ? query.Where(a => a.Artist.Kind == null)
                    : query.Where(a => a.Artist.Kind == kind);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(a => a.Title.Contains(term) || a.Artist.Name.Contains(term));
            }

            var total = await query.CountAsync();
            var items = await query
                .OrderBy(a => a.Artist.SortName).ThenBy(a => a.Year).ThenBy(a => a.Title)
                .Skip(page * pageSize).Take(pageSize)
                .Select(a => new
                {
                    id = a.Id,
                    title = a.Title,
                    year = a.Year,
                    tag = a.Tag,
                    artistId = a.ArtistId,
                    artistName = a.Artist.Name,
                    // The ORDER's key, shipped alongside: the album grid is sorted by artist, so the
                    // client's A–Z jump strip has to bucket on the same string the sort used —
                    // "Beatles, The" belongs under B, not T.
                    artistSortName = a.Artist.SortName,
                    // The artist's kind (null = music; comedy, audiobook, …) so the catalog package can
                    // group the client-side album list by kind without a second fetch.
                    artistKind = a.Artist.Kind,
                    hasArt = a.HasArt,
                    dominantColor = a.DominantColor,
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, items });
        }

        [HttpGet("/API/Music/Album/{id}")]
        public async Task<IActionResult> Album(int id)
        {
            var album = await movieDb.MusicAlbums.AsNoTracking()
                .Include(a => a.Artist)
                .FirstOrDefaultAsync(a => a.Id == id);
            if (album == null)
                return NotFound();

            var tracks = await movieDb.MusicTracks.AsNoTracking()
                .Where(t => t.AlbumId == id)
                .InTrackOrder()
                .Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    trackNo = t.TrackNo,
                    discNo = t.DiscNo,
                    durationSec = t.DurationSec,
                    codec = t.Codec,
                    bitrateKbps = t.BitrateKbps,
                    requiresTranscode = t.RequiresTranscode,
                    missing = t.MissingSinceUtc != null,
                })
                .ToListAsync();

            return Ok(new
            {
                id = album.Id,
                title = album.Title,
                year = album.Year,
                tag = album.Tag,
                artistId = album.ArtistId,
                artistName = album.Artist.Name,
                hasArt = album.HasArt,
                dominantColor = album.DominantColor,
                tracks,
            });
        }

        /// <summary>Lyrics for one track (music-plan.md §2.7). 404 when we have none — the pane's
        /// empty state is the normal case until the LRCLIB pass has run over the catalog.</summary>
        [HttpGet("/API/Music/Track/{id}/Lyrics")]
        public async Task<IActionResult> Lyrics(int id)
        {
            var lyrics = await movieDb.MusicTrackLyrics.AsNoTracking()
                .FirstOrDefaultAsync(l => l.TrackId == id);
            if (lyrics == null || (lyrics.PlainText == null && lyrics.SyncedLrc == null))
                return NotFound(new { message = "No lyrics for this track." });

            return Ok(new
            {
                trackId = id,
                plainText = lyrics.PlainText,
                syncedLrc = lyrics.SyncedLrc,
                source = lyrics.Source,
            });
        }

        // ── Playlists (music-plan.md §2.4 / Phase 3) ─────────────────────────────────────────────
        // Same verb set as the channel playlist API so the frontend layer is familiar, but over the
        // Music* tables (§2.2): music needs queue semantics, not TV scheduling. Every playlist is
        // private to its owner — the mutating verbs all go through LoadOwnedPlaylistAsync, which
        // returns null (⇒ 404, not 403: someone else's playlist doesn't exist as far as you know)
        // for a row you don't own. Positions are dense 0..n-1 and rewritten wholesale by SetItems.

        private const int MaxPlaylistName = 200; // matches MusicPlaylist.Name's column width

        private async Task<MusicPlaylist?> LoadOwnedPlaylistAsync(int id, int userId) =>
            await movieDb.MusicPlaylists.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

        /// <summary>The playlist if this user may READ AND EDIT it — owner, or someone it was shared
        /// with. Null (⇒ 404) otherwise, keeping the "you can't tell it exists" stance.</summary>
        /// <remarks>
        /// A share is collaborative: the point of sharing is a list several people add to, so every
        /// content verb accepts a member. What stays with the OWNER is deleting the playlist and
        /// deciding who else gets in (LoadOwnedPlaylistAsync) — both are irreversible for everyone
        /// else holding a share, so a member must not be able to do them.
        /// </remarks>
        /// <remarks>
        /// The share clause deliberately excludes a favorites list: those are one person's by
        /// definition, so their privacy is a property of the QUERY rather than of the Share verb
        /// remembering to refuse. Sharing one is refused there too, but if a grant ever existed —
        /// written by hand, or by a future verb nobody thought to guard — it still would not open
        /// this door.
        /// </remarks>
        private async Task<MusicPlaylist?> LoadAccessiblePlaylistAsync(int id, int userId) =>
            await movieDb.MusicPlaylists.FirstOrDefaultAsync(p =>
                p.Id == id && (p.UserId == userId
                    || (!p.IsFavorites
                        && movieDb.MusicPlaylistShares.Any(sh => sh.PlaylistId == p.Id && sh.UserId == userId))));

        /// <summary>Keeps the caller's order, drops ids that aren't real tracks (a stale client list
        /// must never be able to write a dangling FK).</summary>
        private async Task<List<int>> ValidateOrderedTracksAsync(IEnumerable<int>? requested)
        {
            var list = (requested ?? Enumerable.Empty<int>()).ToList();
            if (list.Count == 0) return new List<int>();
            var exist = new HashSet<int>(await movieDb.MusicTracks
                .Where(t => list.Contains(t.Id)).Select(t => t.Id).ToListAsync());
            return list.Where(exist.Contains).ToList();
        }

        private static string NormalizeName(string? raw, string fallback)
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) name = fallback;
            return name.Length > MaxPlaylistName ? name.Substring(0, MaxPlaylistName) : name;
        }

        public class CreatePlaylistRequest
        {
            public string? Name { get; set; }
            public List<int>? TrackIds { get; set; } // in order
        }

        [HttpPost("/API/Music/Playlist/Create")]
        public async Task<IActionResult> CreatePlaylist([FromBody] CreatePlaylistRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            if (request == null) return BadRequest(new { message = "Invalid request." });

            var name = NormalizeName(request.Name, "My playlist");
            var trackIds = await ValidateOrderedTracksAsync(request.TrackIds);

            var playlist = new MusicPlaylist
            {
                UserId = userId.Value,
                Name = name,
                CreatedUtc = DateTime.UtcNow,
            };
            movieDb.MusicPlaylists.Add(playlist);
            await movieDb.SaveChangesAsync();

            for (int pos = 0; pos < trackIds.Count; pos++)
                movieDb.MusicPlaylistItems.Add(new MusicPlaylistItem { PlaylistId = playlist.Id, TrackId = trackIds[pos], Position = pos });
            if (trackIds.Count > 0)
                await movieDb.SaveChangesAsync();

            return Ok(new { id = playlist.Id, name = playlist.Name, count = trackIds.Count });
        }

        [HttpGet("/API/Music/Playlist/Mine")]
        public async Task<IActionResult> MyPlaylists()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            // Mine now means "mine, plus anything shared with me" — a member needs the playlist to
            // appear somewhere, and the manager is that somewhere. isOwner drives which controls the
            // card offers (delete and share management are the owner's alone).
            var playlists = await movieDb.MusicPlaylists.AsNoTracking()
                .Where(p => p.UserId == userId.Value
                    || movieDb.MusicPlaylistShares.Any(sh => sh.PlaylistId == p.Id && sh.UserId == userId.Value))
                .OrderByDescending(p => p.Id)
                .Select(p => new
                {
                    p.Id, p.Name, p.CreatedUtc,
                    p.IsFavorites,
                    isOwner = p.UserId == userId.Value,
                    ownerName = p.User.Username,
                    sharedWith = movieDb.MusicPlaylistShares.Count(sh => sh.PlaylistId == p.Id),
                })
                .ToListAsync();
            if (playlists.Count == 0)
                return Ok(Array.Empty<object>());

            // Friends-scale playlists: pull every item once and group in memory for the count plus the
            // lead few titles/albums a tile needs (the album ids drive the tile's art collage).
            var ids = playlists.Select(p => p.Id).ToList();
            var rows = await movieDb.MusicPlaylistItems.AsNoTracking()
                .Where(i => ids.Contains(i.PlaylistId))
                .OrderBy(i => i.Position).ThenBy(i => i.Id)
                .Select(i => new { i.PlaylistId, i.TrackId, title = i.Track.Title, albumId = i.Track.AlbumId })
                .ToListAsync();
            var byPlaylist = rows.GroupBy(r => r.PlaylistId).ToDictionary(g => g.Key, g => g.ToList());

            // Favorites first: it's the one list the user never made and always has, so it reads as the
            // shelf everything else sits under rather than as whichever playlist happens to be newest.
            var result = playlists
                .OrderByDescending(p => p.IsFavorites)
                .ThenByDescending(p => p.Id)
                .Select(p =>
            {
                var items = byPlaylist.GetValueOrDefault(p.Id) ?? new();
                return new
                {
                    id = p.Id,
                    name = p.Name,
                    createdUtc = p.CreatedUtc,
                    count = items.Count,
                    isFavorites = p.IsFavorites,
                    p.isOwner,
                    p.ownerName,
                    p.sharedWith,
                    trackTitles = items.Take(3).Select(i => i.title).ToList(),
                    // Four is what the tile's art collage needs; twelve is what the Music Explore tab's
                    // "Your favourites" rail draws from (R9 S7) — the same ids, a longer prefix.
                    albumIds = items.Where(i => i.albumId != null).Select(i => i.albumId!.Value).Distinct().Take(12).ToList(),
                };
            });
            return Ok(result);
        }

        [HttpGet("/API/Music/Playlist/{id}/Items")]
        public async Task<IActionResult> PlaylistItems(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var playlist = await LoadAccessiblePlaylistAsync(id, userId.Value);
            if (playlist == null) return NotFound(new { message = "Playlist not found." });

            // Shaped like the album tracklist so the client can hand it straight to player.playTracks.
            var items = await movieDb.MusicPlaylistItems.AsNoTracking()
                .Where(i => i.PlaylistId == id)
                .OrderBy(i => i.Position).ThenBy(i => i.Id)
                .Select(i => new
                {
                    id = i.TrackId,
                    title = i.Track.Title,
                    durationSec = i.Track.DurationSec,
                    requiresTranscode = i.Track.RequiresTranscode,
                    missing = i.Track.MissingSinceUtc != null,
                    artistId = i.Track.ArtistId,
                    artistName = i.Track.Artist.Name,
                    albumId = i.Track.AlbumId,
                    albumTitle = i.Track.Album != null ? i.Track.Album.Title : null,
                })
                .ToListAsync();

            // isFavorites so the manage modal can drop the controls this list doesn't have (rename,
            // share, delete) rather than offering them and letting the server refuse.
            return Ok(new { id = playlist.Id, name = playlist.Name, isFavorites = playlist.IsFavorites, items });
        }

        public class PlaylistTracksRequest
        {
            public List<int>? TrackIds { get; set; } // in order
        }

        [HttpPost("/API/Music/Playlist/{id}/AddItems")]
        public async Task<IActionResult> AddPlaylistItems(int id, [FromBody] PlaylistTracksRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var playlist = await LoadAccessiblePlaylistAsync(id, userId.Value);
            if (playlist == null) return NotFound(new { message = "Playlist not found." });

            var trackIds = await ValidateOrderedTracksAsync(request?.TrackIds);
            if (trackIds.Count == 0)
                return Ok(new { count = await movieDb.MusicPlaylistItems.CountAsync(i => i.PlaylistId == id) });

            int nextPos = 1 + (await movieDb.MusicPlaylistItems.Where(i => i.PlaylistId == id).MaxAsync(i => (int?)i.Position) ?? -1);
            foreach (var trackId in trackIds)
                movieDb.MusicPlaylistItems.Add(new MusicPlaylistItem { PlaylistId = id, TrackId = trackId, Position = nextPos++ });
            await movieDb.SaveChangesAsync();

            return Ok(new { count = await movieDb.MusicPlaylistItems.CountAsync(i => i.PlaylistId == id) });
        }

        /// <summary>Replaces the whole ordered lineup — reorder and remove in one call, positions
        /// rewritten 0..n-1 (the manage modal's only save verb).</summary>
        [HttpPost("/API/Music/Playlist/{id}/SetItems")]
        public async Task<IActionResult> SetPlaylistItems(int id, [FromBody] PlaylistTracksRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var playlist = await LoadAccessiblePlaylistAsync(id, userId.Value);
            if (playlist == null) return NotFound(new { message = "Playlist not found." });

            var trackIds = await ValidateOrderedTracksAsync(request?.TrackIds);

            var existing = await movieDb.MusicPlaylistItems.Where(i => i.PlaylistId == id).ToListAsync();
            movieDb.MusicPlaylistItems.RemoveRange(existing);
            for (int pos = 0; pos < trackIds.Count; pos++)
                movieDb.MusicPlaylistItems.Add(new MusicPlaylistItem { PlaylistId = id, TrackId = trackIds[pos], Position = pos });
            await movieDb.SaveChangesAsync();

            return Ok(new { count = trackIds.Count });
        }

        public class RenamePlaylistRequest
        {
            public string? Name { get; set; }
        }

        [HttpPost("/API/Music/Playlist/{id}/Rename")]
        public async Task<IActionResult> RenamePlaylist(int id, [FromBody] RenamePlaylistRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var playlist = await LoadAccessiblePlaylistAsync(id, userId.Value);
            if (playlist == null) return NotFound(new { message = "Playlist not found." });

            if (playlist.IsFavorites)
                return BadRequest(new { message = "Your Favorites list can't be renamed." });

            var name = (request?.Name ?? "").Trim();
            if (name.Length == 0)
                return BadRequest(new { message = "A name is required." });
            playlist.Name = NormalizeName(name, "My playlist");
            await movieDb.SaveChangesAsync();
            return Ok(new { id = playlist.Id, name = playlist.Name });
        }

        [HttpPost("/API/Music/Playlist/{id}/Delete")]
        public async Task<IActionResult> DeletePlaylist(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var playlist = await LoadOwnedPlaylistAsync(id, userId.Value);
            if (playlist == null) return NotFound(new { message = "Playlist not found." });

            // Favorites is a fixture of the account, not something you made — emptying it is the
            // supported way to clear it, and it comes straight back the next time a heart is clicked.
            if (playlist.IsFavorites)
                return BadRequest(new { message = "Your Favorites list can't be deleted." });

            // Items cascade with the playlist (§2.2); the tracks themselves are Restrict and untouched.
            movieDb.MusicPlaylists.Remove(playlist);
            await movieDb.SaveChangesAsync();
            return Ok(new { deleted = true });
        }

        // ── Favorites (the heart in the play bar) ────────────────────────────────────────────────
        // One list per account, flagged on the ordinary playlist table so it plays, shuffles, reorders
        // and shows up in the manager with no new machinery. What the flag takes AWAY is what makes it
        // favorites: it can't be shared, renamed or deleted, so it stays exactly one person's forever.
        //
        // Created lazily, on the first heart. A GET that writes rows would mint an empty Favorites for
        // every account that ever loaded the player, so the list simply doesn't exist until it's used —
        // and the client treats "no list" as "nothing favorited", which is the same thing.

        private const string FavoritesName = "Favorites";

        private Task<MusicPlaylist?> LoadFavoritesAsync(int userId) =>
            movieDb.MusicPlaylists.FirstOrDefaultAsync(p => p.UserId == userId && p.IsFavorites);

        /// <summary>The caller's favorites list, creating it if this is their first one.</summary>
        /// <remarks>
        /// Two hearts clicked at once on a fresh account both see "no list" and both try to insert.
        /// The filtered unique index makes the loser throw rather than mint a second Favorites; it then
        /// re-reads the winner's row, so a double click is a no-op instead of a 500 and a mess.
        /// </remarks>
        private async Task<MusicPlaylist> GetOrCreateFavoritesAsync(int userId)
        {
            var existing = await LoadFavoritesAsync(userId);
            if (existing != null) return existing;

            var playlist = new MusicPlaylist
            {
                UserId = userId,
                Name = FavoritesName,
                IsFavorites = true,
                CreatedUtc = DateTime.UtcNow,
            };
            movieDb.MusicPlaylists.Add(playlist);
            try
            {
                await movieDb.SaveChangesAsync();
                return playlist;
            }
            catch (DbUpdateException)
            {
                movieDb.Entry(playlist).State = EntityState.Detached;
                var winner = await LoadFavoritesAsync(userId);
                if (winner == null) throw; // not the race after all — a real write failure, so surface it
                return winner;
            }
        }

        /// <summary>Every favorited track id, so the player can draw a filled or empty heart without a
        /// round trip per song. Asked once a session; friends-scale lists are a few KB of ints.</summary>
        [HttpGet("/API/Music/Favorites")]
        public async Task<IActionResult> Favorites()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var playlist = await LoadFavoritesAsync(userId.Value);
            if (playlist == null)
                return Ok(new { playlistId = (int?)null, trackIds = Array.Empty<int>() });

            var trackIds = await movieDb.MusicPlaylistItems.AsNoTracking()
                .Where(i => i.PlaylistId == playlist.Id)
                .OrderBy(i => i.Position).ThenBy(i => i.Id)
                .Select(i => i.TrackId)
                .ToListAsync();
            return Ok(new { playlistId = (int?)playlist.Id, trackIds });
        }

        public class FavoriteRequest
        {
            public int TrackId { get; set; }
            public bool Favorite { get; set; }
        }

        /// <summary>Heart or un-heart one track. Idempotent in both directions — the client sends the
        /// state it wants, not a flip, so a double-tap or a stale UI can't invert the result.</summary>
        [HttpPost("/API/Music/Favorite")]
        public async Task<IActionResult> SetFavorite([FromBody] FavoriteRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            if (request == null) return BadRequest(new { message = "Invalid request." });

            if (!request.Favorite)
            {
                // Un-hearting an account with no favorites list is already true; don't create one to
                // say so.
                var existing = await LoadFavoritesAsync(userId.Value);
                if (existing == null)
                    return Ok(new { favorite = false, count = 0, playlistId = (int?)null });

                var rows = await movieDb.MusicPlaylistItems
                    .Where(i => i.PlaylistId == existing.Id && i.TrackId == request.TrackId)
                    .ToListAsync();
                if (rows.Count > 0)
                {
                    // Positions are left with a hole. Order is all that's read (Position, then Id), and
                    // re-densifying would rewrite every later row on each un-heart; the manage modal's
                    // Save closes the gaps whenever the list is next edited by hand.
                    movieDb.MusicPlaylistItems.RemoveRange(rows);
                    await movieDb.SaveChangesAsync();
                }
                return Ok(new
                {
                    favorite = false,
                    count = await movieDb.MusicPlaylistItems.CountAsync(i => i.PlaylistId == existing.Id),
                    playlistId = (int?)existing.Id,
                });
            }

            var track = await movieDb.MusicTracks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == request.TrackId);
            if (track == null) return NotFound(new { message = "No such track." });

            var favorites = await GetOrCreateFavoritesAsync(userId.Value);
            var already = await movieDb.MusicPlaylistItems
                .AnyAsync(i => i.PlaylistId == favorites.Id && i.TrackId == track.Id);
            if (!already)
            {
                int nextPos = 1 + (await movieDb.MusicPlaylistItems
                    .Where(i => i.PlaylistId == favorites.Id)
                    .MaxAsync(i => (int?)i.Position) ?? -1);
                movieDb.MusicPlaylistItems.Add(new MusicPlaylistItem
                {
                    PlaylistId = favorites.Id, TrackId = track.Id, Position = nextPos,
                });
                await movieDb.SaveChangesAsync();
            }

            return Ok(new
            {
                favorite = true,
                count = await movieDb.MusicPlaylistItems.CountAsync(i => i.PlaylistId == favorites.Id),
                playlistId = (int?)favorites.Id,
            });
        }

        // ── Sharing (music-plan.md §2.4) ─────────────────────────────────────────────────────────
        // A shared playlist is COLLABORATIVE: members add, reorder, rename. Only the owner may delete
        // it or change who has access, because both are irreversible for every other member.
        // "Leave" is a member's own exit and is therefore theirs, not the owner's.

        public class ShareRequest
        {
            public List<int>? UserIds { get; set; }
        }

        /// <summary>People this playlist is shared with. Visible to anyone who has access, so members
        /// can see who else is in — a collaborative list where you can't tell who else can edit it is
        /// worse than not sharing at all.</summary>
        [HttpGet("/API/Music/Playlist/{id}/Shares")]
        public async Task<IActionResult> PlaylistShares(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var playlist = await LoadAccessiblePlaylistAsync(id, userId.Value);
            if (playlist == null) return NotFound(new { message = "Playlist not found." });

            var shares = await movieDb.MusicPlaylistShares.AsNoTracking()
                .Where(sh => sh.PlaylistId == id)
                .Select(sh => new { userId = sh.UserId, username = sh.User.Username, sharedUtc = sh.CreatedUtc })
                .ToListAsync();
            var owner = await movieDb.Users.AsNoTracking()
                .Where(u => u.UserID == playlist.UserId)
                .Select(u => u.Username).FirstOrDefaultAsync();

            // meId so a member's "leave" knows which share to revoke without a second round-trip.
            return Ok(new
            {
                ownerId = playlist.UserId,
                ownerName = owner,
                isOwner = playlist.UserId == userId.Value,
                meId = userId.Value,
                shares,
            });
        }

        [HttpPost("/API/Music/Playlist/{id}/Share")]
        public async Task<IActionResult> SharePlaylist(int id, [FromBody] ShareRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var playlist = await LoadOwnedPlaylistAsync(id, userId.Value);   // owner only
            if (playlist == null) return NotFound(new { message = "Playlist not found." });
            if (playlist.IsFavorites)
                return BadRequest(new { message = "Favorites are private — they can't be shared." });

            var wanted = (request?.UserIds ?? new List<int>())
                .Where(u => u != userId.Value)                                // sharing with yourself is a no-op
                .Distinct().ToList();
            if (wanted.Count == 0) return Ok(new { added = 0 });

            // Only real accounts, and never a second row for a pair that already exists (the unique
            // index would throw; a repeated "share with Bob" should simply be idempotent).
            var real = await movieDb.Users.Where(u => wanted.Contains(u.UserID)).Select(u => u.UserID).ToListAsync();
            var already = await movieDb.MusicPlaylistShares
                .Where(sh => sh.PlaylistId == id && real.Contains(sh.UserId))
                .Select(sh => sh.UserId).ToListAsync();

            var toAdd = real.Except(already).ToList();
            foreach (var uid in toAdd)
                movieDb.MusicPlaylistShares.Add(new MusicPlaylistShare
                {
                    PlaylistId = id, UserId = uid, CreatedUtc = DateTime.UtcNow,
                });
            await movieDb.SaveChangesAsync();
            return Ok(new { added = toAdd.Count, alreadyShared = already.Count });
        }

        /// <summary>Revoke access. The OWNER may remove anyone; a MEMBER may remove only themselves,
        /// which is how you leave a playlist someone shared with you.</summary>
        [HttpPost("/API/Music/Playlist/{id}/Unshare")]
        public async Task<IActionResult> UnsharePlaylist(int id, [FromBody] ShareRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var playlist = await LoadAccessiblePlaylistAsync(id, userId.Value);
            if (playlist == null) return NotFound(new { message = "Playlist not found." });

            var isOwner = playlist.UserId == userId.Value;
            var targets = (request?.UserIds ?? new List<int>()).Distinct().ToList();
            if (!isOwner && (targets.Count != 1 || targets[0] != userId.Value))
                return Forbid();

            var rows = await movieDb.MusicPlaylistShares
                .Where(sh => sh.PlaylistId == id && targets.Contains(sh.UserId)).ToListAsync();
            movieDb.MusicPlaylistShares.RemoveRange(rows);
            await movieDb.SaveChangesAsync();
            return Ok(new { removed = rows.Count });
        }

        /// <summary>Accounts a playlist can be shared with: password-holders, since streaming is
        /// password-only (§3.1) and a passwordless account could never open the playlist anyway.
        /// Usernames only — this is a picker, not a directory.</summary>
        [HttpGet("/API/Music/ShareTargets")]
        public async Task<IActionResult> ShareTargets()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            var users = await movieDb.Users.AsNoTracking()
                .Where(u => u.PasswordHash != null && u.UserID != userId.Value && u.Username != null)
                .OrderBy(u => u.Username)
                .Select(u => new { id = u.UserID, username = u.Username })
                .ToListAsync();
            return Ok(users);
        }

        // ── Admin: bulk album-art warm (music-plan.md §2.5) ──────────────────────────────────────────
        // Runs the remote art lookup IN THE WEB APP so it writes to the live images mount — the CLI's
        // pass can't, because a dev box has no access to prod's volume (same reason
        // /API/Admin/IngestReview/BackfillPosters exists). This is the bulk version of what
        // MusicImageController does lazily on first view; both share MusicArtFill and one throttle gate.
        //
        // Bounded per call (limit) and resumable (after cursor), returning { processed, remaining,
        // nextCursor } so the CALLER drives it to completion — MusicBrainz's 1 req/s means the whole
        // catalog is far more than one request's worth of work.

        /// <summary>Admin = a config-designated username AND a password-verified session; the
        /// passwordless communal login alone proves nothing (see AdminController's remarks).</summary>
        private bool IsCurrentUserAdmin() =>
            User.FindFirst("amr")?.Value == "pwd"
            && config.AdminUsernames.Any(a => string.Equals(a, User.FindFirst(ClaimTypes.Name)?.Value, StringComparison.OrdinalIgnoreCase));

        /// <summary>Accepts one album's cover as raw image bytes and stores it on the live images mount.</summary>
        /// <remarks>
        /// The remote lookup can only ask the internet, and it has now exhausted itself — the albums
        /// still bare are forum compilations, audiobooks and bootlegs that MusicBrainz has never heard
        /// of. Many of them DO carry art, embedded in the files or sitting as cover.jpg beside them,
        /// but that is on the music share, which this process cannot read (images mount and music
        /// share live on different hosts — the same split that forced BackfillArt to exist).
        ///
        /// So the extraction happens where the files are and the bytes are posted here. This endpoint
        /// only stores; it never fetches, and it shares MusicArtStore/MusicArtFill with the other two
        /// paths so all three agree on sizes, naming and the dominant colour.
        /// </remarks>
        [HttpPost("/API/Admin/Music/UploadArt/{albumId:int}")]
        public async Task<IActionResult> UploadArt(int albumId)
        {
            if (!IsCurrentUserAdmin()) return Forbid();
            if (MusicArtStore.ResolveDir(config) == null)
                return StatusCode(501, new { message = "No images directory configured (MusicImagesDir / MoviePostersDir)." });

            var album = await movieDb.MusicAlbums.Include(a => a.Artist).FirstOrDefaultAsync(a => a.Id == albumId);
            if (album == null) return NotFound(new { message = "No such album." });

            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return BadRequest(new { message = "Empty body." });

            var ok = await MusicArtFill.StoreAsync(movieDb, config, album, bytes);
            return ok
                ? Ok(new { albumId, stored = true, dominantColor = album.DominantColor })
                : StatusCode(422, new { albumId, stored = false, message = "Those bytes aren't a decodable image." });
        }

        /// <summary>Removes an album's stored art, for when the art that is there is WRONG.</summary>
        /// <remarks>
        /// Until now art could only be added or overwritten, which is fine while a replacement exists.
        /// It often doesn't: the in-house Disney compilations ("Disney's Ballads", "Hero Songs",
        /// "Music Around Disneyland") are not in any public database, so the covers an unverified
        /// backfill hung on them — ZOMBIES, High School Musical 3, Queen's Greatest Hits — could not be
        /// taken off. A blank tile is strictly better than a confidently wrong one.
        ///
        /// <para><paramref name="recheck"/> also clears the negative cache so the lookup will try again
        /// on next view; leave it false to make the album stay bare, which is what you want when the
        /// record genuinely has no cover to find and re-asking would just re-fetch the same wrong one.</para>
        /// </remarks>
        [HttpPost("/API/Admin/Music/ClearArt/{albumId:int}")]
        public async Task<IActionResult> ClearArt(int albumId, [FromQuery] bool recheck = false)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            var album = await movieDb.MusicAlbums.FirstOrDefaultAsync(a => a.Id == albumId);
            if (album == null) return NotFound(new { message = "No such album." });

            var removed = 0;
            var dir = MusicArtStore.ResolveDir(config);
            if (dir != null)
            {
                foreach (var thumbnail in new[] { false, true })
                {
                    var path = Path.Combine(dir, MusicArtStore.FileName(albumId, thumbnail));
                    try
                    {
                        if (System.IO.File.Exists(path)) { System.IO.File.Delete(path); removed++; }
                    }
                    catch (IOException) { /* leave the flag alone if the mount refuses */ }
                }
            }

            album.HasArt = false;
            album.DominantColor = null;
            album.ArtCheckedUtc = recheck ? null : DateTime.UtcNow;
            await movieDb.SaveChangesAsync();
            return Ok(new { albumId, cleared = true, filesRemoved = removed, willRecheck = recheck });
        }

        [HttpPost("/API/Admin/Music/BackfillArt")]
        public async Task<IActionResult> BackfillArt([FromQuery] int limit = 25, [FromQuery] int after = 0)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            var imagesDir = MusicArtStore.ResolveDir(config);
            if (imagesDir == null)
                return StatusCode(501, new { message = "No images directory configured (MusicImagesDir / MoviePostersDir)." });

            limit = Math.Clamp(limit, 1, 100);

            // The work set is "art missing ON THIS MOUNT and worth asking about": never-asked albums
            // (ArtCheckedUtc null), plus albums flagged HasArt whose file isn't here — that flag means a
            // local CLI run found art on a different mount, so this one still has to fetch it.
            // A miss clears HasArt and stamps ArtCheckedUtc, so every album leaves the set exactly once
            // and a driver loop terminates.
            var candidates = await movieDb.MusicAlbums
                .Where(a => a.ArtCheckedUtc == null || a.HasArt)
                .OrderBy(a => a.Id)
                .Select(a => new { a.Id })
                .ToListAsync();

            bool NeedsArt(int id) =>
                !System.IO.File.Exists(Path.Combine(imagesDir, MusicArtStore.FileName(id, thumbnail: false)));

            var pending = candidates.Where(c => c.Id > after && NeedsArt(c.Id)).Select(c => c.Id).ToList();
            var batchIds = pending.Take(limit).ToList();

            int filled = 0, missed = 0;
            using var http = MusicRemoteArt.CreateHttp();
            foreach (var id in batchIds)
            {
                var album = await movieDb.MusicAlbums.Include(a => a.Artist).FirstOrDefaultAsync(a => a.Id == id);
                if (album == null) continue;

                // Wait our turn behind any lazy fetch rather than doubling the request rate.
                await MusicRemoteArt.Gate.WaitAsync(HttpContext.RequestAborted);
                try
                {
                    if (await MusicArtFill.FetchAndStoreAsync(movieDb, config, http, album, MusicRemoteArt.SpaceCallAsync))
                        filled++;
                    else
                        missed++;
                }
                finally
                {
                    MusicRemoteArt.Gate.Release();
                }
            }

            var withArt = await movieDb.MusicAlbums.CountAsync(a => a.HasArt);
            var totalAlbums = await movieDb.MusicAlbums.CountAsync();
            return Ok(new
            {
                processed = batchIds.Count,
                filled,
                missed,
                remaining = Math.Max(0, pending.Count - batchIds.Count),
                nextCursor = batchIds.Count > 0 ? batchIds[^1] : after,
                withArt,
                totalAlbums,
            });
        }
    }
}
