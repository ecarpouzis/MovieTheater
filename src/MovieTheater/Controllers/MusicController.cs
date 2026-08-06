using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
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
        });


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

            var token = MusicCapabilityToken.Mint(config.StreamTokenSecret!, new MusicCapabilityToken.Payload(
                userId.Value, track.Id, track.RelativePath,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TokenLifetimeSeconds));

            // The ROUTE decides how the gateway serves the bytes, so the capability itself is
            // unchanged (still 4 fields) — a browser-native format streams as a file with Range,
            // an exotic one is piped through ffmpeg as mp3 (§Phase 7). Keeping the token identical
            // is deliberate: no version skew between the site and the gateway.
            var transcode = track.RequiresTranscode;
            return Ok(new
            {
                trackId = track.Id,
                url = MusicStreamRoutes.Url(config.StreamGatewayBaseUrl!, token, transcode),
                transcoded = transcode,
                mimeType = transcode ? "audio/mpeg" : MusicMimeTypes.FromExtension(track.Extension),
                durationSec = track.DurationSec,
                title = track.Title,
                artist = track.Artist.Name,
                album = track.Album?.Title,
            });
        }

        [HttpGet("/API/Music/Artists")]
        public async Task<IActionResult> Artists()
        {
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
            var artists = await movieDb.MusicArtists.AsNoTracking()
                .OrderBy(a => a.SortName)
                .Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    sortName = a.SortName,
                    folderName = a.FolderName,
                    yearRange = a.YearRange,
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
                .Where(t => t.ArtistId == id && t.AlbumId == null)
                .OrderBy(t => t.FileName)
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
                albums,
                looseTracks,
            });
        }

        [HttpGet("/API/Music/Search")]
        public async Task<IActionResult> Search(string? q = null)
        {
            var term = (q ?? "").Trim();
            if (term.Length < 2)
                return Ok(new { tracks = Array.Empty<object>() });

            // Track-title search only: the client already holds all artists/albums and matches those
            // itself; songs are the one thing it can't (20k+ rows).
            var tracks = await movieDb.MusicTracks.AsNoTracking()
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
        public async Task<IActionResult> Albums(string? q = null, int page = 0, int pageSize = 60)
        {
            // Ceiling above the whole catalog (1.3k albums): the library page loads it once and
            // filters client-side, the sanctioned pattern for a modest catalog (BoardGames).
            pageSize = Math.Clamp(pageSize, 1, 5000);
            var query = movieDb.MusicAlbums.AsNoTracking().Include(a => a.Artist).AsQueryable();
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
                .OrderBy(t => t.DiscNo).ThenBy(t => t.TrackNo).ThenBy(t => t.FileName)
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
        private async Task<MusicPlaylist?> LoadAccessiblePlaylistAsync(int id, int userId) =>
            await movieDb.MusicPlaylists.FirstOrDefaultAsync(p =>
                p.Id == id && (p.UserId == userId
                    || movieDb.MusicPlaylistShares.Any(sh => sh.PlaylistId == p.Id && sh.UserId == userId)));

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

            var result = playlists.Select(p =>
            {
                var items = byPlaylist.GetValueOrDefault(p.Id) ?? new();
                return new
                {
                    id = p.Id,
                    name = p.Name,
                    createdUtc = p.CreatedUtc,
                    count = items.Count,
                    p.isOwner,
                    p.ownerName,
                    p.sharedWith,
                    trackTitles = items.Take(3).Select(i => i.title).ToList(),
                    albumIds = items.Where(i => i.albumId != null).Select(i => i.albumId!.Value).Distinct().Take(4).ToList(),
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

            return Ok(new { id = playlist.Id, name = playlist.Name, items });
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

            // Items cascade with the playlist (§2.2); the tracks themselves are Restrict and untouched.
            movieDb.MusicPlaylists.Remove(playlist);
            await movieDb.SaveChangesAsync();
            return Ok(new { deleted = true });
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
