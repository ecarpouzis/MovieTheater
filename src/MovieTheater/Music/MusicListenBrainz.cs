using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MovieTheater.Music
{
    /// <summary>
    /// Reading MusicBrainz releases and ListenBrainz listen counts (2026-08-31) — the third
    /// popularity source, and the only one whose data is published as CC0 rather than merely exposed.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is worth a third pass.</b> Measured over a five-band sample of the library
    /// before this was built: <b>53%</b> of our tracks come back with a listen count, and the counts
    /// agree with Last.fm at <b>Spearman ρ = 0.720</b> — LOWER than Deezer's 0.788, which is the
    /// argument for it. A source that agreed perfectly would add nothing; this one disagrees enough
    /// to carry information and not so much as to be noise.</para>
    ///
    /// <para><b>It is keyed by MBID, not by name.</b> Last.fm and Deezer are joined to our rows by
    /// title, with all the folding that needs; MusicBrainz gives a recording an identity, so the join
    /// happens once per album and the popularity lookup afterwards is exact. The name matching moves
    /// to a single step (our title → the release's tracklist) instead of riding every request.</para>
    ///
    /// <para><b>Its audience is small and that MATTERS.</b> ListenBrainz's listener base is orders of
    /// magnitude below Last.fm's, so a count of three here is not the same evidence as a count of
    /// three there. That is not corrected in this file — it is the ranking's job
    /// (<see cref="MusicScoreRanking"/>), which weighs a source by the audience behind it and an
    /// observation by the count behind it.</para>
    /// </remarks>
    public static class MusicListenBrainz
    {
        /// <summary>How many recording MBIDs one popularity POST carries. Their endpoint takes a
        /// list; this keeps a body sane and a failure cheap.</summary>
        public const int MaxRecordingsPerRequest = 100;

        /// <summary>One release MusicBrainz offered: its id, and the artist/title to judge it by.</summary>
        public readonly record struct ReleaseHit(string Id, string Title, string Artist);

        /// <summary>The first release in a MusicBrainz <c>release/?query=</c> body, or null.</summary>
        public static ReleaseHit? ParseReleaseSearch(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("releases", out var releases)
                    || releases.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var release in releases.EnumerateArray())
                {
                    if (release.ValueKind != JsonValueKind.Object) continue;
                    var id = release.TryGetProperty("id", out var i) ? i.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var title = release.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var artist = "";
                    if (release.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array)
                        foreach (var credit in credits.EnumerateArray())
                        {
                            if (credit.ValueKind != JsonValueKind.Object) continue;
                            if (credit.TryGetProperty("name", out var cn)) { artist = cn.GetString() ?? ""; break; }
                            if (credit.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object
                                && a.TryGetProperty("name", out var an)) { artist = an.GetString() ?? ""; break; }
                        }
                    return new ReleaseHit(id!, title, artist);
                }
                return null;
            }
            catch (JsonException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>
        /// Track title → recording MBID for one release (<c>release/{id}?inc=recordings</c>).
        /// </summary>
        /// <remarks>
        /// A release is split into <c>media</c> (discs), each with its own <c>tracks</c>, so a
        /// double album's second disc is only reachable by walking both levels. The TRACK title and
        /// the RECORDING title can differ — the track is how this release billed it, which is what
        /// our tag is most likely to match — so the track's title is the key and the recording's id
        /// is the value.
        /// </remarks>
        public static List<(string Title, string RecordingMbid)> ParseReleaseRecordings(string? json)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("media", out var media)
                    || media.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var disc in media.EnumerateArray())
                {
                    if (disc.ValueKind != JsonValueKind.Object) continue;
                    if (!disc.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array) continue;
                    foreach (var track in tracks.EnumerateArray())
                    {
                        if (track.ValueKind != JsonValueKind.Object) continue;
                        var title = track.TryGetProperty("title", out var t) ? t.GetString() : null;
                        if (string.IsNullOrWhiteSpace(title)) continue;
                        if (!track.TryGetProperty("recording", out var recording) || recording.ValueKind != JsonValueKind.Object) continue;
                        var mbid = recording.TryGetProperty("id", out var rid) ? rid.GetString() : null;
                        if (string.IsNullOrWhiteSpace(mbid)) continue;
                        result.Add((title!.Trim(), mbid!));
                    }
                }
                return result;
            }
            catch (JsonException) { return result; }
            catch (InvalidOperationException) { return result; }
        }

        /// <summary>
        /// Listen counts by recording MBID from a <c>popularity/recording</c> body.
        /// </summary>
        /// <remarks>
        /// The endpoint answers for EVERY mbid asked about, including ones it has never seen, and
        /// those come back with a null count. A null is dropped rather than stored as 0 — "we have no
        /// listens for this" and "nobody has ever played this" are the same sentence here, but only
        /// the first is something the API actually knows.
        /// </remarks>
        public static Dictionary<string, long> ParsePopularity(string? json)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array) return result;
                foreach (var row in root.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    var mbid = row.TryGetProperty("recording_mbid", out var m) ? m.GetString() : null;
                    if (string.IsNullOrWhiteSpace(mbid)) continue;
                    if (!row.TryGetProperty("total_listen_count", out var c) || c.ValueKind != JsonValueKind.Number) continue;
                    if (!c.TryGetInt64(out var listens) || listens < 0) continue;
                    result[mbid!] = listens;
                }
                return result;
            }
            catch (JsonException) { return result; }
            catch (InvalidOperationException) { return result; }
        }
    }
}
