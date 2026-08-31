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
    }
}
