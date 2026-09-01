using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MovieTheater.Music
{
    /// <summary>
    /// Reading the Spotify Web API (2026-08-31): the token response, album search, an album's track
    /// ids, and the per-track <c>popularity</c>. Pure parsing, so the shapes are pinned by tests.
    /// </summary>
    /// <remarks>
    /// <para><b>Spotify is the purpose-built source.</b> Last.fm reports listeners and Deezer an
    /// internal rank, both of which are proxies; <c>popularity</c> is Spotify's own 0–100 estimate of
    /// how much a track is being played right now, which is the actual question. It is also the
    /// largest audience of the three by a wide margin.</para>
    ///
    /// <para><b>It authenticates as an APP, never as a person.</b> The client-credentials flow trades
    /// a client id and secret for a bearer token; there is no endpoint anywhere in the Web API that
    /// accepts an account email and password. That is why a user login cannot be used here and why
    /// the configuration holds an app registration instead.</para>
    ///
    /// <para><b>Popularity is not on the album's tracklist.</b> <c>/v1/albums/{id}</c> returns simplified
    /// track objects, which carry an id and a name but no popularity — it lives only on the full
    /// track object. So an album costs a search, a fetch, and one batched <c>/v1/tracks?ids=</c> of up
    /// to 50, which is where the number finally appears. Three requests per album rather than one per
    /// track is the same trade the other two sources make.</para>
    /// </remarks>
    public static class MusicSpotify
    {
        /// <summary>How many track ids one <c>/v1/tracks</c> call accepts. Spotify's documented cap.</summary>
        public const int MaxTrackIdsPerRequest = 50;

        /// <summary>The bearer token and how long it is good for.</summary>
        public readonly record struct Token(string AccessToken, int ExpiresInSeconds);

        /// <summary>
        /// The access token out of a client-credentials response, or null when the body carries none
        /// (bad credentials answer with an <c>error</c> object and no token).
        /// </summary>
        public static Token? ParseToken(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("access_token", out var t) || t.ValueKind != JsonValueKind.String) return null;
                var token = t.GetString();
                if (string.IsNullOrWhiteSpace(token)) return null;
                var expires = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
                    ? e.GetInt32() : 3600;
                return new Token(token!, expires);
            }
            catch (JsonException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>One album Spotify offered: its id, and the artist/title the gate judges it by.</summary>
        public readonly record struct AlbumHit(string Id, string Title, string Artist);

        /// <summary>The first album in a <c>/v1/search?type=album</c> body, or null.</summary>
        public static AlbumHit? ParseAlbumSearch(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("albums", out var albums)
                    || albums.ValueKind != JsonValueKind.Object
                    || !albums.TryGetProperty("items", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var album in items.EnumerateArray())
                {
                    if (album.ValueKind != JsonValueKind.Object) continue;
                    var id = album.TryGetProperty("id", out var i) ? i.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var title = album.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    // artists is a LIST; the first credited one is what the album is filed under.
                    var artist = "";
                    if (album.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                        foreach (var a in artists.EnumerateArray())
                        {
                            if (a.ValueKind != JsonValueKind.Object) continue;
                            artist = a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                            break;
                        }
                    return new AlbumHit(id!, title, artist);
                }
                return null;
            }
            catch (JsonException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>
        /// The track ids on an album, from <c>/v1/albums/{id}</c> or <c>/v1/albums/{id}/tracks</c>.
        /// These are SIMPLIFIED track objects and carry no popularity — that is what the batched
        /// <c>/v1/tracks</c> call is for.
        /// </summary>
        public static List<string> ParseAlbumTrackIds(string? json)
        {
            var ids = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return ids;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return ids;

                // Both shapes: the album object nests tracks.items, the tracks endpoint returns items.
                JsonElement items;
                if (root.TryGetProperty("items", out var direct) && direct.ValueKind == JsonValueKind.Array)
                    items = direct;
                else if (root.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Object
                         && tracks.TryGetProperty("items", out var nested) && nested.ValueKind == JsonValueKind.Array)
                    items = nested;
                else return ids;

                foreach (var track in items.EnumerateArray())
                {
                    if (track.ValueKind != JsonValueKind.Object) continue;
                    var id = track.TryGetProperty("id", out var i) ? i.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
                }
                return ids;
            }
            catch (JsonException) { return ids; }
            catch (InvalidOperationException) { return ids; }
        }

        /// <summary>
        /// Name and popularity for every full track object in a <c>/v1/tracks?ids=</c> body.
        /// </summary>
        /// <remarks>
        /// A null entry is normal in this response — Spotify pads the array for ids it could not
        /// resolve — and is skipped rather than treated as a failure. A track with no popularity
        /// field is dropped rather than scored 0, the same rule the other two sources follow.
        /// </remarks>
        public static List<(string Name, long Popularity)> ParseTracks(string? json)
        {
            var result = new List<(string, long)>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("tracks", out var tracks)
                    || tracks.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var track in tracks.EnumerateArray())
                {
                    // Spotify pads the array with nulls for ids it could not resolve.
                    if (track.ValueKind != JsonValueKind.Object) continue;
                    var name = track.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!track.TryGetProperty("popularity", out var p) || p.ValueKind != JsonValueKind.Number) continue;
                    if (!p.TryGetInt32(out var popularity) || popularity < 0) continue;
                    result.Add((name!.Trim(), popularity));
                }
                return result;
            }
            catch (JsonException) { return result; }
            catch (InvalidOperationException) { return result; }
        }
    }
}
