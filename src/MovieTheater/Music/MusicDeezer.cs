using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MovieTheater.Music
{
    /// <summary>
    /// Reading Deezer's public API (2026-08-31): album search and a tracklist with per-track
    /// <c>rank</c>. Pure parsing, so the shapes it has to survive are pinned by tests rather than
    /// discovered in production.
    /// </summary>
    /// <remarks>
    /// <para><b>Why Deezer at all.</b> It needs NO credentials — no key, no OAuth, no registration —
    /// which makes it the one second opinion available without waiting on anybody. Measured against
    /// the Last.fm listener counts already held, over a sample stratified across all ten popularity
    /// bands of the library: <b>Spearman ρ = 0.788</b>. High enough that it corroborates rather than
    /// contradicts, low enough that its disagreements carry information.</para>
    /// <para><b>Album-first, like the Last.fm pass.</b> Searching per track cost one request each and
    /// matched 65% of a sample; fetching a whole album's tracklist costs two requests per ALBUM
    /// (~8,300 for this library instead of ~60,000) and matched 77%, because an album's tracklist
    /// gives title matching a context a global search does not have.</para>
    /// <para><b>The album match must be GATED.</b> In that same sample, a search for Johnny Cash's
    /// <i>America</i> returned a confidently wrong album and 0 of 21 tracks matched — the failure
    /// mode is not "no answer", it is a plausible answer about a different record. So the caller
    /// checks the returned artist and title before believing a tracklist.</para>
    /// </remarks>
    public static class MusicDeezer
    {
        /// <summary>One album Deezer offered: its id, and the artist/title to judge it by.</summary>
        public readonly record struct AlbumHit(long Id, string Title, string Artist);

        /// <summary>
        /// The first album in a <c>search/album</c> body, or null when nothing came back.
        /// </summary>
        public static AlbumHit? ParseAlbumSearch(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var album in data.EnumerateArray())
                {
                    if (album.ValueKind != JsonValueKind.Object) continue;
                    if (!album.TryGetProperty("id", out var id)) continue;
                    var albumId = id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n) ? n
                        : id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out var s) ? s
                        : 0;
                    if (albumId == 0) continue;
                    var title = album.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var artist = album.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object
                                 && a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    return new AlbumHit(albumId, title, artist);
                }
                return null;
            }
            catch (JsonException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>
        /// Every track in an album tracklist body, with the <c>rank</c> Deezer orders them by.
        /// </summary>
        /// <remarks>
        /// A track with no usable rank is DROPPED rather than scored 0 — 0 would assert that nobody
        /// plays it, and the caller's whole contract is that an unknown track simply has no row.
        /// </remarks>
        public static List<(string Title, long Rank)> ParseTracks(string? json)
        {
            var tracks = new List<(string, long)>();
            if (string.IsNullOrWhiteSpace(json)) return tracks;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    return tracks;

                foreach (var track in data.EnumerateArray())
                {
                    if (track.ValueKind != JsonValueKind.Object) continue;
                    var title = track.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    if (!track.TryGetProperty("rank", out var r)) continue;
                    long? rank = r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var n) ? n
                        : r.ValueKind == JsonValueKind.String && long.TryParse(r.GetString(), out var s) ? s
                        : null;
                    if (rank == null || rank < 0) continue;
                    tracks.Add((title!.Trim(), rank.Value));
                }
                return tracks;
            }
            catch (JsonException) { return tracks; }
            catch (InvalidOperationException) { return tracks; }
        }

        /// <summary>
        /// Whether an album Deezer returned is plausibly the album that was asked for.
        /// </summary>
        /// <remarks>
        /// The gate exists because of a measured failure, not a hypothetical one: Johnny Cash's
        /// <i>America</i> matched a different record entirely and would have written 21 wrong scores.
        /// Both halves must agree — an artist match alone would accept any other album by them, and a
        /// title match alone would accept a covers record.
        /// <para>Comparison is on the same fold the tracks are matched by
        /// (<see cref="MusicTrackTitles.Normalize"/>), and containment rather than equality in either
        /// direction, because editions decorate one side or the other: "Hoss" against "Hoss (Remastered)",
        /// "Pinocchio" against "Pinocchio (Original Motion Picture Soundtrack)".</para>
        /// </remarks>
        public static bool AcceptsAlbum(string? theirTitle, string? theirArtist, string ourTitle, string ourArtist)
        {
            var t1 = MusicTrackTitles.Normalize(theirTitle);
            var t2 = MusicTrackTitles.Normalize(ourTitle);
            var a1 = MusicTrackTitles.Normalize(theirArtist);
            var a2 = MusicTrackTitles.Normalize(ourArtist);
            if (t1.Length == 0 || t2.Length == 0 || a1.Length == 0 || a2.Length == 0) return false;
            return Overlaps(t1, t2) && Overlaps(a1, a2);
        }

        private static bool Overlaps(string a, string b) =>
            a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }
}
