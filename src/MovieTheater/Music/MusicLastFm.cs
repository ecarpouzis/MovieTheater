using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MovieTheater.Music
{
    /// <summary>
    /// Reading one Last.fm <c>album.getinfo</c> body (R9 S10). Pure, so the shapes it has to survive
    /// can be pinned by tests instead of discovered in production.
    /// </summary>
    public static class MusicLastFm
    {
        /// <summary>
        /// The listener count and top tags out of one <c>album.getinfo</c> body.
        /// </summary>
        /// <remarks>
        /// <para><b>Last.fm does not keep <c>tags</c> one shape</b>, and that is the whole difficulty.
        /// Measured over 995 cached answers from this library: 869 came back as
        /// <c>"tags":{"tag":[…]}</c>, <b>115 as the empty STRING <c>"tags":""</c></b> (an album nobody
        /// has tagged), and 11 collapsed a lone tag to a bare object, <c>"tags":{"tag":{…}}</c>.</para>
        /// <para>The empty-string shape was a data-loss bug, not a curiosity:
        /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> throws
        /// <see cref="InvalidOperationException"/> — which is NOT a <see cref="JsonException"/> and so
        /// slipped past a <c>catch (JsonException)</c> — when asked for a property of a string. Reaching
        /// for <c>tags.tag</c> on those 115 albums threw straight out of the read and discarded the
        /// listener count parsed moments earlier, so ~12% of the library silently lost a popularity
        /// score Last.fm had already handed over. Nine Inch Nails' <i>The Downward Spiral</i> was one
        /// of them.</para>
        /// <para><c>listeners</c> is documented as a string and has always been one here; a number is
        /// accepted too, because the cost of that guess being wrong is a lost score rather than a
        /// visible failure.</para>
        /// </remarks>
        public static (long? Listeners, List<(string Genre, int Votes)> Tags) ParseAlbum(string? json)
        {
            var tags = new List<(string, int)>();
            if (string.IsNullOrWhiteSpace(json)) return (null, tags);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                // A "not found" answer is {"error":6,…}: no album element, which is a clean MISS.
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("album", out var album)
                    || album.ValueKind != JsonValueKind.Object)
                    return (null, tags);

                long? listeners = null;
                if (album.TryGetProperty("listeners", out var l))
                {
                    if (l.ValueKind == JsonValueKind.String && long.TryParse(l.GetString(), out var parsed))
                        listeners = parsed;
                    else if (l.ValueKind == JsonValueKind.Number && l.TryGetInt64(out var direct))
                        listeners = direct;
                }

                if (album.TryGetProperty("tags", out var tagRoot)
                    && tagRoot.ValueKind == JsonValueKind.Object
                    && tagRoot.TryGetProperty("tag", out var tagNode))
                {
                    var items = tagNode.ValueKind switch
                    {
                        JsonValueKind.Array => tagNode.EnumerateArray().ToList(),
                        // One tag arrives bare rather than in a list of one.
                        JsonValueKind.Object => new List<JsonElement> { tagNode },
                        _ => new List<JsonElement>(),
                    };

                    // Last.fm's top tags come ranked but unweighted; rank IS the weight, descending, so
                    // the strongest tag keeps the biggest number the way the other sources' do.
                    var rank = items.Count;
                    foreach (var tag in items)
                    {
                        var name = tag.ValueKind == JsonValueKind.Object && tag.TryGetProperty("name", out var n)
                            ? n.GetString()
                            : null;
                        if (!string.IsNullOrWhiteSpace(name)) tags.Add((name!, rank));
                        rank--;
                    }
                }

                // Every tag Last.fm ranked, uncapped: this reports what was SAID. How many of them
                // become genre rows is the caller's policy, applied at the call site.
                return (listeners, tags);
            }
            // A malformed body is a MISS, never a throw: this runs inside a bulk pass whose whole
            // point is that one odd answer costs one album, not the run.
            catch (JsonException) { return (null, tags); }
            catch (InvalidOperationException) { return (null, tags); }
        }

        /// <summary>
        /// Every track in one <c>artist.gettoptracks</c> body, with the listener count Last.fm
        /// ranked it by (2026-08-31) — the source of <see cref="MovieTheater.Db.MusicTrack.Popularity"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this method and not <c>track.getinfo</c>.</b> Per-track lookups would be
        /// exact and need no name matching at all, but the library holds 60,797 music tracks and that
        /// is 60,797 requests. This asks ONCE PER ARTIST and gets their whole ranked catalogue back:
        /// 7,921 requests for the same library, an eighth of the traffic. The cost is that the answer
        /// has to be joined to our rows by NAME (<see cref="MusicTrackTitles"/>), and that the join
        /// is imperfect — measured against four real artists before the pass was written, it matched
        /// 209/213 Beatles tracks, 366/378 Nine Inch Nails, 143/146 Radiohead and 97/97 Boards of
        /// Canada: <b>97–100%</b>. Nearly every miss was OUR tag being wrong ("Threre's A Place") or
        /// carrying a performance credit Last.fm files elsewhere, which no lookup method would have
        /// fixed.</para>
        /// <para><b>The shapes.</b> <c>toptracks.track</c> is an array; an artist with exactly one
        /// known track collapses it to a bare object, the same trap <see cref="ParseAlbum"/> hit with
        /// tags — and an unknown artist answers <c>{"error":6}</c> with no <c>toptracks</c> at all,
        /// which is a clean MISS. <c>listeners</c> is a string here as it is there.</para>
        /// <para>Returned in the order Last.fm gave them, which is already descending by listeners:
        /// this reports what was SAID, and the caller decides what to keep.</para>
        /// </remarks>
        public static List<(string Name, long Listeners)> ParseTopTracks(string? json)
        {
            var tracks = new List<(string, long)>();
            if (string.IsNullOrWhiteSpace(json)) return tracks;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("toptracks", out var top)
                    || top.ValueKind != JsonValueKind.Object
                    || !top.TryGetProperty("track", out var node))
                    return tracks;

                var items = node.ValueKind switch
                {
                    JsonValueKind.Array => node.EnumerateArray().ToList(),
                    // One known track arrives bare rather than in a list of one.
                    JsonValueKind.Object => new List<JsonElement> { node },
                    _ => new List<JsonElement>(),
                };

                foreach (var track in items)
                {
                    if (track.ValueKind != JsonValueKind.Object) continue;
                    var name = track.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    long? listeners = null;
                    if (track.TryGetProperty("listeners", out var l))
                    {
                        if (l.ValueKind == JsonValueKind.String && long.TryParse(l.GetString(), out var parsed))
                            listeners = parsed;
                        else if (l.ValueKind == JsonValueKind.Number && l.TryGetInt64(out var direct))
                            listeners = direct;
                    }
                    // A track with no usable count carries no popularity, and a 0 would claim nobody
                    // has heard it. Dropping it leaves the row unmatched, which is the honest state.
                    if (listeners == null) continue;
                    tracks.Add((name!.Trim(), listeners.Value));
                }
                return tracks;
            }
            // A malformed body is a MISS, never a throw — one odd answer costs one artist, not the run.
            catch (JsonException) { return tracks; }
            catch (InvalidOperationException) { return tracks; }
        }

        /// <summary>
        /// Last.fm's stand-in for "this record has no picture" — a grey star, served with a 200 and a
        /// perfectly valid JPEG body at every size.
        /// </summary>
        /// <remarks>
        /// It passes every pixel test there is, which makes it the same class of trap as the
        /// <c>proof.jpg</c> a scene release ships: cover-SHAPED, and not a cover. It has to be refused
        /// by identity, because nothing about the image itself will give it away — and shipping it
        /// would be worse than leaving the album blank, since a blank album stays in the work queue
        /// while a starred one looks finished.
        /// </remarks>
        public const string PlaceholderImageId = "2a96cbd8b46e442fc41c2b86b821562f";

        private static readonly string[] SizeOrder = { "mega", "extralarge", "large", "medium", "small" };

        /// <summary>
        /// The best cover URL and the release MBID out of one <c>album.getinfo</c> body — the two
        /// things in that answer that can become album art.
        /// </summary>
        /// <remarks>
        /// <para>The MBID matters more than the URL: it is an EXACT release identity, so it turns a
        /// fuzzy "search MusicBrainz for this artist and title" into a direct Cover Art Archive
        /// lookup, and CAA holds full-resolution scans where Last.fm serves a 300px thumbnail.</para>
        /// <para>Sizes are ranked rather than trusted in document order, and the placeholder is
        /// refused at every size — see <see cref="PlaceholderImageId"/>.</para>
        /// </remarks>
        public static (string? ImageUrl, string? Mbid) ParseArt(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return (null, null);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("album", out var album)
                    || album.ValueKind != JsonValueKind.Object)
                    return (null, null);

                string? mbid = null;
                if (album.TryGetProperty("mbid", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    var raw = m.GetString();
                    if (!string.IsNullOrWhiteSpace(raw)) mbid = raw!.Trim();
                }

                string? best = null;
                var bestRank = int.MaxValue;
                if (album.TryGetProperty("image", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var image in images.EnumerateArray())
                    {
                        if (image.ValueKind != JsonValueKind.Object) continue;
                        var url = image.TryGetProperty("#text", out var u) ? u.GetString() : null;
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        if (url!.Contains(PlaceholderImageId, StringComparison.OrdinalIgnoreCase)) continue;

                        var size = image.TryGetProperty("size", out var s) ? s.GetString() ?? "" : "";
                        var rank = Array.IndexOf(SizeOrder, size);
                        if (rank < 0) rank = SizeOrder.Length;
                        if (rank < bestRank) { best = url.Trim(); bestRank = rank; }
                    }
                }
                return (best, mbid);
            }
            catch (JsonException) { return (null, null); }
            catch (InvalidOperationException) { return (null, null); }
        }

        /// <summary>
        /// The full-resolution form of a Last.fm image URL, or null when it is not one we can reshape.
        /// </summary>
        /// <remarks>
        /// Last.fm serves <c>/i/u/&lt;size&gt;/&lt;hash&gt;.jpg</c>, where the size segment is a resize
        /// directive rather than part of the identity; dropping it returns the image as uploaded. Worth
        /// asking for, because the largest size Last.fm advertises in <c>album.getinfo</c> is 300×300 —
        /// below the 600px the mount stores — so taking the advertised URL would bank a cover softer
        /// than the one available. The caller must treat a failure here as "use the sized URL", never as
        /// a miss.
        /// </remarks>
        public static string? OriginalSizeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var marker = "/i/u/";
            var at = url!.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            var tail = url.Substring(at + marker.Length);
            var slash = tail.IndexOf('/');
            if (slash < 0) return null;                       // already unsized
            var size = tail.Substring(0, slash);
            // Only strip a genuine resize directive ("300x300", "770x0", "avatar170s"), never a path.
            if (!size.Contains('x') && !size.EndsWith("s", StringComparison.OrdinalIgnoreCase)) return null;
            return url.Substring(0, at + marker.Length) + tail.Substring(slash + 1);
        }
    }
}
