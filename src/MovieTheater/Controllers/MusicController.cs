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

        // ── Genre + rating on the shelf rows (R9 S10) ────────────────────────────────────────────
        // The Music browse holds its whole shelf client-side and filters it there, so the rail's
        // Genre facet, the "By genre" group and the "Top rated" sort can only exist if the genre and
        // the score arrive ON the shelf rows. That is the section's per-shelf fetch rule working as
        // designed — one fetch, everything the browse can ask about — not a widened payload for its
        // own sake. Both additions are small: a genre list is at most four short strings and the
        // scores are three numbers.

        /// <summary>Genres per album id, strongest first, MERGED across sources.</summary>
        /// <remarks>
        /// The tag pass and the external passes disagree about a record on purpose, and both are
        /// right about something — the files say what this rip was labelled, MusicBrainz says what
        /// the world calls it. The rail lists the UNION so a search for either finds the album, with
        /// the file's own answer first (it is the one about THIS copy) and everything de-duplicated
        /// case-insensitively so "indie rock" and "Indie Rock" are one pill.
        /// </remarks>
        private async Task<Dictionary<int, List<string>>> GenresByAlbumAsync(List<int> albumIds)
        {
            if (albumIds.Count == 0) return new Dictionary<int, List<string>>();
            var rows = await movieDb.MusicAlbumGenres.AsNoTracking()
                .Where(g => albumIds.Contains(g.AlbumId))
                .Select(g => new { g.AlbumId, g.Genre, g.Source, g.Weight })
                .ToListAsync();
            return rows
                .GroupBy(r => r.AlbumId)
                .ToDictionary(g => g.Key, g => g
                    // Tags first (they describe this copy), then by how strongly the source asserts it.
                    .OrderBy(r => r.Source == MusicGenreSources.Tags ? 0 : 1)
                    .ThenByDescending(r => r.Weight)
                    .Select(r => r.Genre)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToList());
        }

        /// <summary>Every album's genres, for a whole-shelf fetch — one scan instead of a 2,900-id IN list.</summary>
        private async Task<Dictionary<int, List<string>>> AllGenresByAlbumAsync()
        {
            var rows = await movieDb.MusicAlbumGenres.AsNoTracking()
                .Select(g => new { g.AlbumId, g.Genre, g.Source, g.Weight })
                .ToListAsync();
            return rows
                .GroupBy(r => r.AlbumId)
                .ToDictionary(g => g.Key, g => g
                    .OrderBy(r => r.Source == MusicGenreSources.Tags ? 0 : 1)
                    .ThenByDescending(r => r.Weight)
                    .Select(r => r.Genre)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToList());
        }

        private sealed record AlbumScores(double? Average, int Count, int? Mine);

        /// <summary>The house's average + the caller's own score, per album id. Two small grouped
        /// reads; the blend itself is <see cref="MusicPopularity.Blend"/> and happens per row.</summary>
        private async Task<Dictionary<int, AlbumScores>> ScoresByAlbumAsync(int? userId)
        {
            var aggregates = await movieDb.MusicAlbumRatings.AsNoTracking()
                .GroupBy(r => r.AlbumId)
                .Select(g => new { AlbumId = g.Key, Average = (double?)g.Average(r => (double)r.Score), Count = g.Count() })
                .ToListAsync();
            var mine = userId == null
                ? new List<(int AlbumId, int Score)>()
                : (await movieDb.MusicAlbumRatings.AsNoTracking()
                    .Where(r => r.UserId == userId.Value)
                    .Select(r => new { r.AlbumId, r.Score })
                    .ToListAsync()).Select(r => (r.AlbumId, r.Score)).ToList();

            var map = aggregates.ToDictionary(a => a.AlbumId, a => new AlbumScores(a.Average, a.Count, null));
            foreach (var (albumId, score) in mine)
                map[albumId] = map.TryGetValue(albumId, out var row)
                    ? row with { Mine = score }
                    : new AlbumScores(null, 0, score);
            return map;
        }

        private static readonly AlbumScores NoScores = new(null, 0, null);

        // ── Play telemetry (R9 closing pass) ─────────────────────────────────────────────────────

        /// <summary>Library-wide plays for one album or artist: how many, and when last.</summary>
        private sealed record PlayRollup(int Plays, DateTime? LastPlayedUtc);

        private static readonly PlayRollup NoPlays = new(0, null);

        /// <summary>
        /// The library-wide play roll-ups, by album and by artist — two small grouped reads over
        /// <see cref="MusicPlayStat"/> joined to the tracks.
        /// </summary>
        /// <remarks>
        /// <para>The numbers ride the SHELF ROWS for the reason genre and rating do: the Music browse
        /// holds its whole shelf client-side, so a "Most played" order (and the Explore rail behind
        /// it) can only exist if the count is ON the row. Summing an aggregate table is what makes
        /// that affordable — an event log would make this a COUNT over every play ever.</para>
        /// <para><b>Library-wide, never per listener.</b> The rows are per user; what a card SHOWS is
        /// the sum across everyone, so it says how often a record gets played in this house and never
        /// by whom. An artist's plays include their loose tracks, which belong to no album.</para>
        /// </remarks>
        private async Task<(Dictionary<int, PlayRollup> ByAlbum, Dictionary<int, PlayRollup> ByArtist)> PlayRollupsAsync()
        {
            var byAlbum = (await (from s in movieDb.MusicPlayStats.AsNoTracking()
                                  join t in movieDb.MusicTracks.AsNoTracking() on s.MusicTrackId equals t.Id
                                  where t.AlbumId != null
                                  group s by t.AlbumId!.Value into g
                                  select new { AlbumId = g.Key, Plays = g.Sum(x => x.PlayCount), Last = (DateTime?)g.Max(x => x.LastPlayedUtc) })
                             .ToListAsync())
                .ToDictionary(r => r.AlbumId, r => new PlayRollup(r.Plays, r.Last));

            var byArtist = (await (from s in movieDb.MusicPlayStats.AsNoTracking()
                                   join t in movieDb.MusicTracks.AsNoTracking() on s.MusicTrackId equals t.Id
                                   group s by t.ArtistId into g
                                   select new { ArtistId = g.Key, Plays = g.Sum(x => x.PlayCount), Last = (DateTime?)g.Max(x => x.LastPlayedUtc) })
                              .ToListAsync())
                .ToDictionary(r => r.ArtistId, r => new PlayRollup(r.Plays, r.Last));

            return (byAlbum, byArtist);
        }

        /// <summary>One reported play: the track, and when it STARTED (the idempotency key).</summary>
        public sealed record PlayReport(int TrackId, DateTime StartedUtc);

        /// <summary>Bounds one report. The player sends one play at a time; the cap is for the
        /// <c>pagehide</c> flush, which can carry a small backlog, and for a runaway client.</summary>
        private const int MaxPlayReports = 50;

        /// <summary>
        /// Parses the beacon's body into reports. Pure so the parsing rules — which are the whole
        /// contract with a fire-and-forget sender — can be tested without a request.
        /// </summary>
        /// <remarks>
        /// A missing or unusable <c>startedAt</c> falls back to <paramref name="now"/>, so a report is
        /// never DROPPED for a clock problem, and a stamp outside a sane window is clamped rather
        /// than trusted: it only ever keys idempotency, and a wild value would make the next genuine
        /// play look like a duplicate. Every stamp is floored to the minute — that IS the key.
        /// </remarks>
        internal static List<PlayReport> ParsePlayReports(string? raw, DateTime now)
        {
            var reports = new List<PlayReport>();
            if (string.IsNullOrWhiteSpace(raw)) return reports;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch (JsonException) { return reports; }
            using (doc)
            {
                var root = doc.RootElement;
                // A bare object is one play; `plays` is the batch the pagehide flush sends.
                var items = root.ValueKind == JsonValueKind.Array ? root
                    : root.TryGetProperty("plays", out var p) && p.ValueKind == JsonValueKind.Array ? p
                    : default;
                var elements = items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray().ToList()
                    : new List<JsonElement> { root };

                foreach (var e in elements)
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("trackId", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                    if (!t.TryGetInt32(out var trackId) || trackId <= 0) continue;

                    var started = now;
                    if (e.TryGetProperty("startedAt", out var sa))
                    {
                        if (sa.ValueKind == JsonValueKind.String && DateTime.TryParse(sa.GetString(),
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                                out var parsed))
                            started = parsed;
                        else if (sa.ValueKind == JsonValueKind.Number && sa.TryGetInt64(out var ms))
                            started = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                    }
                    // A stamp only keys idempotency, so a wild one is clamped, never trusted.
                    if (started > now.AddMinutes(5) || started < now.AddDays(-1)) started = now;
                    reports.Add(new PlayReport(trackId, FloorToMinute(started)));
                    if (reports.Count >= MaxPlayReports) break;
                }
            }
            return reports;
        }

        private static DateTime FloorToMinute(DateTime t) =>
            new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);

        /// <summary>
        /// Records that the caller played a track. The music vertical's first play telemetry.
        /// </summary>
        /// <remarks>
        /// <para>The player fires this ONCE per track, when playback passes 30 s or 50 % of the
        /// track (whichever comes first) — a threshold, not a start, so skipping through a queue
        /// records nothing and a seek inside a track it already reported cannot fire it again.</para>
        ///
        /// <para>It arrives as a <c>sendBeacon</c> with <c>text/plain</c>, exactly like
        /// <c>/API/Music/Incident</c> and for the same reason: the last one is sent from
        /// <c>pagehide</c>, when the page is being frozen and will not survive a CORS preflight. So
        /// the body is read and parsed here rather than model-bound.</para>
        ///
        /// <para><b>Idempotent per user × track × started-at MINUTE.</b> The row remembers the
        /// minute of the play it last counted, and a report carrying that same minute is a no-op.
        /// That is what makes a fire-and-forget beacon safe: a retry, a <c>pagehide</c> flush racing
        /// the in-flight send, or two tabs cannot inflate a count. Bounded, like every write here —
        /// at most <see cref="MaxPlayReports"/> per call.</para>
        ///
        /// <para>Gated like every <c>/API/Music/*</c> route (the controller's StreamingUser policy).
        /// Rows are per user; only the library-wide sum is ever shown.</para>
        /// </remarks>
        [HttpPost("/API/Music/Play")]
        public async Task<IActionResult> Play()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            using var reader = new StreamReader(Request.Body);
            var raw = await reader.ReadToEndAsync();
            if (raw.Length > 64 * 1024) raw = raw.Substring(0, 64 * 1024);

            var now = DateTime.UtcNow;
            var reports = ParsePlayReports(raw, now);
            if (reports.Count == 0) return Ok(new { counted = 0, skipped = 0 });

            var trackIds = reports.Select(r => r.TrackId).Distinct().ToList();
            var valid = (await movieDb.MusicTracks.AsNoTracking()
                .Where(t => trackIds.Contains(t.Id)).Select(t => t.Id).ToListAsync()).ToHashSet();
            var existing = await movieDb.MusicPlayStats
                .Where(p => p.UserId == userId.Value && trackIds.Contains(p.MusicTrackId))
                .ToListAsync();

            int counted = 0, skipped = 0;
            foreach (var report in reports)
            {
                if (!valid.Contains(report.TrackId)) { skipped++; continue; }
                var row = existing.FirstOrDefault(p => p.MusicTrackId == report.TrackId);
                if (row == null)
                {
                    row = new MusicPlayStat
                    {
                        UserId = userId.Value,
                        MusicTrackId = report.TrackId,
                        PlayCount = 1,
                        LastPlayedUtc = now,
                        LastStartedUtc = report.StartedUtc,
                    };
                    movieDb.MusicPlayStats.Add(row);
                    existing.Add(row);
                    counted++;
                    continue;
                }
                // The same minute is the same play, however many times it is reported.
                if (row.LastStartedUtc == report.StartedUtc) { skipped++; continue; }
                row.PlayCount++;
                row.LastPlayedUtc = now;
                row.LastStartedUtc = report.StartedUtc;
                counted++;
            }

            await movieDb.SaveChangesAsync();
            return Ok(new { counted, skipped });
        }

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

            // The artist's headline genres (R9 S10) — the roll-up MusicArtistGenre exists to make
            // cheap, so the "one per artist" grid can say what a shelf-mate sounds like without the
            // client summing 2,900 albums on every render.
            var artistGenres = (await movieDb.MusicArtistGenres.AsNoTracking()
                    .Select(g => new { g.ArtistId, g.Genre, g.Source, g.Weight })
                    .ToListAsync())
                .GroupBy(g => g.ArtistId)
                .ToDictionary(g => g.Key, g => g
                    .OrderBy(r => r.Source == MusicGenreSources.Tags ? 0 : 1)
                    .ThenByDescending(r => r.Weight)
                    .Select(r => r.Genre)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList());

            // "Who has the best-regarded record on this shelf" — an artist has no score of their own,
            // so the Top-rated order over the one-per-artist grid reads the best of their albums.
            // Computed here rather than in the browser because the artist grid must be able to sort
            // even when the albums behind it are filtered out of the current view.
            var albumScores = await ScoresByAlbumAsync(null);
            var artistAlbums = await movieDb.MusicAlbums.AsNoTracking()
                .Select(al => new { al.Id, al.ArtistId, al.Popularity, al.ExternalRating })
                .ToListAsync();
            var artistTop = artistAlbums
                .GroupBy(al => al.ArtistId)
                .ToDictionary(g => g.Key, g => g
                    .Select(al =>
                    {
                        var s = albumScores.GetValueOrDefault(al.Id, NoScores);
                        return MusicPopularity.Blend(s.Average, s.Count, al.ExternalRating);
                    })
                    .Where(v => v != null)
                    .DefaultIfEmpty(null)
                    .Max());
            // "Who has the most widely heard record here" — the other half of the same question, kept
            // apart for the same reason the album orders are: fame is not a verdict.
            var artistTopPopularity = artistAlbums
                .GroupBy(al => al.ArtistId)
                .ToDictionary(g => g.Key, g => g.Select(al => al.Popularity)
                    .Where(v => v != null)
                    .DefaultIfEmpty(null)
                    .Max());

            var (_, playsByArtist) = await PlayRollupsAsync();

            return Ok(artists.Select(a =>
            {
                faces.TryGetValue(a.id, out var face);
                var plays = playsByArtist.GetValueOrDefault(a.id, NoPlays);
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
                    genres = artistGenres.GetValueOrDefault(a.id) ?? EmptyGenres,
                    topRating = artistTop.GetValueOrDefault(a.id),
                    topPopularity = artistTopPopularity.GetValueOrDefault(a.id),
                    // Every play of every track filed under this artist — their loose tracks
                    // included, which belong to no album and would otherwise be invisible here.
                    playCount = plays.Plays,
                    lastPlayedUtc = plays.LastPlayedUtc,
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
                    // The drilled-in album grid gets the same number the browse tiles carry, so a
                    // card does not lose its popularity the moment you open the artist it belongs to.
                    popularity = a.Popularity,
                    // The vote count travels WITH the rating: the card's badge tooltip says "rated by
                    // N people outside this house", and MusicBrainz ratings run thin enough that the
                    // number is the point. Sending the score alone would silently degrade the tooltip.
                    externalRating = a.ExternalRating,
                    externalRatingVotes = a.ExternalRatingVotes,
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
                    popularity = t.Popularity,
                    listeners = t.PopularityListeners,
                    // Where the song sits in THIS LIBRARY (percentile), agreed across every source
                    // that knows it — a different question from `popularity`, which is absolute.
                    rank = t.PopularityRank,
                    rankSources = t.PopularityRankSources,
                })
                .ToListAsync();

            // ── The artist's best-known songs (2026-08-31) ───────────────────────────────────────
            // "Which tracks of this artist are the most popular" — a question the album grid could
            // never answer, because the answer cuts ACROSS the records: a greatest-hits shelf the
            // library never had.
            //
            // Ordered on the server, not the client, for the reason the artist grid's orders are:
            // the page holds only what it was handed, and this list is deliberately a HANDFUL rather
            // than the artist's whole catalogue. Popularity descending, then the title, so an artist
            // whose songs tie does not reshuffle between fetches.
            //
            // Tracks with NO popularity are excluded outright rather than filed last: this is a
            // "best known" list, and a row that means "we have never been told" is not a low score.
            // The section disappears when the enrich pass has not reached this artist yet, which is
            // the honest empty state.
            // Ordered by the CONSENSUS rank where there is one, because a blend of the services that
            // know a song is better evidence than any single service's number, and only falling back
            // to the absolute score for tracks one source alone has ever mentioned.
            var topTrackCandidates = await movieDb.MusicTracks.AsNoTracking()
                .Where(t => t.ArtistId == id && t.MissingSinceUtc == null && t.Popularity != null)
                .OrderByDescending(t => t.PopularityRank ?? t.Popularity)
                .ThenByDescending(t => t.Popularity).ThenBy(t => t.Title).ThenBy(t => t.Id)
                .Take(TopTrackCandidates)
                .Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    durationSec = t.DurationSec,
                    codec = t.Codec,
                    requiresTranscode = t.RequiresTranscode,
                    missing = t.MissingSinceUtc != null,
                    popularity = t.Popularity,
                    listeners = t.PopularityListeners,
                    // Where the song sits in THIS LIBRARY (percentile), agreed across every source
                    // that knows it — a different question from `popularity`, which is absolute.
                    rank = t.PopularityRank,
                    rankSources = t.PopularityRankSources,
                    // Where it came from, so a row can say "— Hunky Dory" and open it. An artist's
                    // loose tracks belong to no album and carry nulls here by design.
                    albumId = t.AlbumId,
                    albumTitle = t.Album != null ? t.Album.Title : null,
                })
                .ToListAsync();

            // ONE ROW PER SONG. Without this the list is unreadable for exactly the artists it
            // matters most for: the Rolling Stones' first ten came back as Sympathy for the Devil
            // five times, Paint It Black three times and Gimme Shelter twice, because the library
            // holds the same recording on the studio album AND on four singles compilations, and
            // every copy carries the same popularity. The dedupe is on the SAME normalised title the
            // popularity was matched by (MusicTrackTitles), so the remix and the live take fold in
            // with the original — they are one song, and the question is which songs are well known.
            //
            // The survivor is the first in the order already applied (popularity, then title, then
            // id), which is deterministic and tends to pick the studio spelling — "Paint It Black"
            // sorts before "Paint It, Black".
            var seenSongs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var topTracks = topTrackCandidates
                .Where(t => seenSongs.Add(MusicTrackTitles.Normalize(t.title)))
                .Take(TopTracksPerArtist)
                .ToList();

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
                topTracks,
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
                    // Search for "Crazy" and four artists answer; the meter is what says which one
                    // the world means. The ORDER stays alphabetical — a search result is a lookup,
                    // and re-ranking it by fame would bury the obscure track somebody typed in full.
                    popularity = t.Popularity,
                    listeners = t.PopularityListeners,
                    // Where the song sits in THIS LIBRARY (percentile), agreed across every source
                    // that knows it — a different question from `popularity`, which is absolute.
                    rank = t.PopularityRank,
                    rankSources = t.PopularityRankSources,
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
            var rows = await query
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
                    popularity = a.Popularity,
                    externalRating = a.ExternalRating,
                    externalRatingVotes = a.ExternalRatingVotes,
                })
                .ToListAsync();

            // Genre + score, added to the rows the browse already fetches (R9 S10) — see the note by
            // GenresByAlbumAsync. A whole-shelf fetch reads every genre row in one scan; a real page
            // (the endpoint still supports one) asks only for the ids on it.
            var genres = rows.Count > 1000
                ? await AllGenresByAlbumAsync()
                : await GenresByAlbumAsync(rows.Select(r => r.id).ToList());
            var scores = await ScoresByAlbumAsync(GetCurrentUserId());
            var (playsByAlbum, _) = await PlayRollupsAsync();

            var items = rows.Select(a =>
            {
                var s = scores.GetValueOrDefault(a.id, NoScores);
                var plays = playsByAlbum.GetValueOrDefault(a.id, NoPlays);
                return new
                {
                    a.id,
                    a.title,
                    a.year,
                    a.tag,
                    a.artistId,
                    a.artistName,
                    a.artistSortName,
                    a.artistKind,
                    a.hasArt,
                    a.dominantColor,
                    a.popularity,
                    genres = genres.GetValueOrDefault(a.id) ?? EmptyGenres,
                    myRating = s.Mine,
                    ratingAvg = s.Average,
                    ratingCount = s.Count,
                    // The album's RATING — a verdict — 0-100. The house's own votes when it has any,
                    // shrunk toward the outside community's rating rather than toward popularity.
                    // That prior USED to be `a.popularity`, which with an empty ratings table made
                    // "Top rated" an order over fame (2026-08-31); popularity is served beside this
                    // as its own number and the site names the two separately everywhere.
                    // Null = nobody has reached a verdict, and the sort files those last.
                    rating = MusicPopularity.Blend(s.Average, s.Count, a.externalRating),
                    externalRating = a.externalRating,
                    externalRatingVotes = a.externalRatingVotes,
                    // Library-wide plays (R9 closing pass) — what "Most played" sorts on and what
                    // "Recently played" orders by. Zero and null are the honest answers for a record
                    // nobody has put on yet; the sort files those last rather than inventing a middle.
                    playCount = plays.Plays,
                    lastPlayedUtc = plays.LastPlayedUtc,
                };
            }).ToList();

            return Ok(new { total, page, pageSize, items });
        }

        private static readonly List<string> EmptyGenres = new();

        /// <summary>How many songs the artist page's "Most popular" section shows. A handful: it is a
        /// pointer at the well-known ones, not a second catalogue beside the album grid.</summary>
        private const int TopTracksPerArtist = 10;

        /// <summary>How many rows to read before folding duplicates down to
        /// <see cref="TopTracksPerArtist"/> songs. Twelve times the answer because the worst real
        /// case is a heavily-compiled artist whose top songs each appear on five or six releases —
        /// the Rolling Stones filled all ten slots with three songs. Still one indexed range read
        /// over one artist.</summary>
        private const int TopTrackCandidates = TopTracksPerArtist * 12;

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
                    // How widely heard the SONG is (2026-08-31), on the album's own scale — what lets
                    // a tracklist say which of these twelve are the famous ones. Null is "we don't
                    // know", never zero, and the row still renders: an album can be half-covered.
                    popularity = t.Popularity,
                    // The raw audience behind that score. The score is LOGARITHMIC, so it cannot
                    // express a drop — 73 and 50 on one album are 112,303 listeners and 2,905 — and
                    // the tracklist draws its comparison bar from this instead.
                    listeners = t.PopularityListeners,
                    // Where the song sits in THIS LIBRARY (percentile), agreed across every source
                    // that knows it — a different question from `popularity`, which is absolute.
                    rank = t.PopularityRank,
                    rankSources = t.PopularityRankSources,
                })
                .ToListAsync();

            var genres = (await GenresByAlbumAsync(new List<int> { id })).GetValueOrDefault(id) ?? EmptyGenres;
            var scores = (await ScoresByAlbumAsync(GetCurrentUserId())).GetValueOrDefault(id, NoScores);
            var plays = (await PlayRollupsAsync()).ByAlbum.GetValueOrDefault(id, NoPlays);

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
                genres,
                popularity = album.Popularity,
                externalRating = album.ExternalRating,
                externalRatingVotes = album.ExternalRatingVotes,
                externalRatingSource = album.ExternalRatingSource,
                myRating = scores.Mine,
                ratingAvg = scores.Average,
                ratingCount = scores.Count,
                rating = MusicPopularity.Blend(scores.Average, scores.Count, album.ExternalRating),
                playCount = plays.Plays,
                lastPlayedUtc = plays.LastPlayedUtc,
                tracks,
            });
        }

        // ── Site ratings (R9 S10) ────────────────────────────────────────────────────────────────
        // The music side of the movies' 0-100 rating feature, in its own table for the reason the
        // whole vertical has its own tables: Viewing's identity is a title in one of the three video
        // id spaces and its verbs (Seen, WantToWatch) mean nothing for a record you can put on again
        // tomorrow. The SHAPE is copied verbatim, including the rule that cost the movie side a bug:
        // 0 is a real score and unrated is NO ROW, so clearing a rating DELETES.

        public class RatingItem
        {
            public int AlbumId { get; set; }
            /// <summary>0–100, or null to CLEAR the rating (delete the row).</summary>
            public int? Value { get; set; }
        }

        public class SetMusicRatingsRequest
        {
            public List<RatingItem> Items { get; set; } = new();
        }

        /// <summary>
        /// The caller's own ratings. With <c>albumId</c>: that album's numbers (mine, the house
        /// average, the vote count, the blend). Without: every rating this user has, which is the
        /// list a "rate the shelf" surface starts from.
        /// </summary>
        [HttpGet("/API/Music/Rating")]
        public async Task<IActionResult> GetRating([FromQuery] int? albumId = null)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            if (albumId != null)
            {
                var album = await movieDb.MusicAlbums.AsNoTracking()
                    .Where(a => a.Id == albumId.Value)
                    .Select(a => new { a.Id, a.Popularity, a.ExternalRating, a.ExternalRatingVotes })
                    .FirstOrDefaultAsync();
                if (album == null) return NotFound();
                var s = (await ScoresByAlbumAsync(userId)).GetValueOrDefault(album.Id, NoScores);
                return Ok(new
                {
                    albumId = album.Id,
                    myRating = s.Mine,
                    ratingAvg = s.Average,
                    ratingCount = s.Count,
                    popularity = album.Popularity,
                    externalRating = album.ExternalRating,
                    externalRatingVotes = album.ExternalRatingVotes,
                    rating = MusicPopularity.Blend(s.Average, s.Count, album.ExternalRating),
                });
            }

            // Every rating one listener has is a few hundred rows at most (2,921 albums is the
            // ceiling), so this is the whole list and not a page — the same judgement the shelf
            // itself is fetched on.
            var mine = await movieDb.MusicAlbumRatings.AsNoTracking()
                .Where(r => r.UserId == userId.Value)
                .OrderByDescending(r => r.UpdatedUtc).ThenBy(r => r.AlbumId)
                .Select(r => new { albumId = r.AlbumId, score = r.Score, updatedUtc = r.UpdatedUtc })
                .ToListAsync();
            return Ok(new { ratings = mine });
        }

        /// <summary>
        /// Upserts the caller's own 0–100 ratings. Bounded + idempotent: one capped chunk per call,
        /// writes only CHANGED rows, and re-sending the same value is a no-op — a caller with a long
        /// list drives the chunk loop to completion (the movies' <c>SetRatings</c> contract).
        /// </summary>
        [HttpPost("/API/Music/Rating")]
        public async Task<IActionResult> SetRating([FromBody] SetMusicRatingsRequest request)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var items = request?.Items;
            if (items == null || items.Count == 0) return Ok(new { updated = 0, skipped = 0, deleted = 0 });
            // Bounded write (project rule): the caller sends capped chunks and drives the loop.
            if (items.Count > 200)
                return BadRequest(new { message = "Too many items; send at most 200 per call." });

            var albumIds = items.Select(i => i.AlbumId).Distinct().ToList();
            var valid = (await movieDb.MusicAlbums.Where(a => albumIds.Contains(a.Id)).Select(a => a.Id).ToListAsync()).ToHashSet();
            var existing = await movieDb.MusicAlbumRatings
                .Where(r => r.UserId == userId.Value && albumIds.Contains(r.AlbumId))
                .ToListAsync();

            int updated = 0, skipped = 0, deleted = 0;
            var now = DateTime.UtcNow;
            foreach (var item in items)
            {
                if (!valid.Contains(item.AlbumId)) { skipped++; continue; }
                var row = existing.FirstOrDefault(r => r.AlbumId == item.AlbumId);

                if (item.Value == null)
                {
                    // Unrated is the ABSENCE of a row, never a sentinel — clearing deletes.
                    if (row != null) { movieDb.MusicAlbumRatings.Remove(row); existing.Remove(row); deleted++; }
                    else skipped++;
                    continue;
                }

                var score = Math.Clamp(item.Value.Value, 0, 100);
                if (row == null)
                {
                    movieDb.MusicAlbumRatings.Add(new MusicAlbumRating
                    {
                        UserId = userId.Value, AlbumId = item.AlbumId, Score = score,
                        CreatedUtc = now, UpdatedUtc = now,
                    });
                    updated++;
                }
                else if (row.Score != score) { row.Score = score; row.UpdatedUtc = now; updated++; }
                else skipped++;
            }

            await movieDb.SaveChangesAsync();
            return Ok(new { updated, skipped, deleted });
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
