using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Music
{
    /// <summary>
    /// The EXTERNAL metadata leg (R9 S10): MusicBrainz release-group tags as genres, and Last.fm
    /// album listeners as <c>MusicAlbum.Popularity</c> (0–100). Everything it fetches is cached raw
    /// on disk first (<see cref="MusicResponseCache"/>, <c>data/music-cache/</c>) — the IMDb page
    /// cache's convention, for the IMDb page cache's reason: at one request a second a full pass is
    /// ~50 minutes of somebody else's server, and every future change to what we EXTRACT has to be an
    /// offline re-parse or it is another 50 minutes.
    ///
    /// <para><b>What it writes.</b> <c>MusicAlbumGenre</c> rows stamped <c>Source='musicbrainz'</c>
    /// or <c>'lastfm'</c> — never the tag pass's rows, which is what the Source column is for — plus
    /// <c>MusicAlbum.Popularity</c>/<c>PopularitySource</c>/<c>PopularityCheckedUtc</c>. It does NOT
    /// roll up to artists: <c>music-genres --rollup-only</c> owns that, and running it afterwards
    /// folds every source's albums in.</para>
    ///
    /// <para><b>Bulk-job rules.</b> Dry-run-first (writes nothing without <c>--apply</c>). Bounded:
    /// at most <c>--take</c> ALBUMS per run. Resumable and idempotent: the queue IS
    /// "PopularityCheckedUtc IS NULL" ordered by Id and the stamp goes on hit AND miss, so the queue
    /// shrinks monotonically and terminates; the genre write REPLACES this source's rows for the
    /// album, so a re-run cannot double up. Polite: one request per second to MusicBrainz through the
    /// process-wide gate <c>MusicRemoteArt</c> already owns (a second gate would silently double the
    /// rate), with the contact-bearing User-Agent they require. Degrades: no Last.fm key means the
    /// popularity half says "not configured" and skips — the MusicBrainz half needs no key.</para>
    /// </summary>
    [Command("music-enrich", Description = "Genres from MusicBrainz + popularity from Last.fm, cached raw on disk (dry-run unless --apply).")]
    public class MusicEnrichCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("dry-run", Description = "Force a dry run. Redundant (dry is the default) but accepted, so the safe spelling is never a typo.")]
        public bool DryRun { get; set; }

        [CommandOption("take", Description = "Max ALBUMS to look up this run (default 50).")]
        public int Take { get; set; } = 50;

        [CommandOption("after", Description = "Resume cursor: skip albums whose Id is ≤ this (from a prior run's nextCursor).")]
        public int After { get; set; }

        [CommandOption("source", Description = "Which source(s): musicbrainz | lastfm | both (default).")]
        public string Source { get; set; } = "both";

        [CommandOption("cache-dir", Description = "Raw response cache root. Default: data/music-cache (gitignored).")]
        public string? CacheDir { get; set; }

        [CommandOption("verbose", Description = "Print a line per album, not just the summary.")]
        public bool Verbose { get; set; }

        /// <summary>Popularity drifts; a cached listener count older than this is re-asked. The tag
        /// lists do not drift, so they are read from cache at any age.</summary>
        private static readonly TimeSpan PopularityTtl = TimeSpan.FromDays(120);

        /// <summary>A MusicBrainz tag needs at least this many votes to count as a genre. One person
        /// tagging a record "songs my dad likes" is not a genre; the threshold is what keeps the long
        /// tail a long tail instead of a landfill.</summary>
        private const int MinTagVotes = 2;

        /// <summary>Genres taken from one external answer, most-voted first.</summary>
        private const int MaxGenresPerAlbum = 4;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public MusicEnrichCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (DryRun) Apply = false;
            var wantMb = Source is "both" or "musicbrainz";
            var wantLastFm = Source is "both" or "lastfm";
            if (!wantMb && !wantLastFm) { w.WriteLine($"Unknown --source '{Source}': use musicbrainz, lastfm or both."); return; }

            var lastFmKey = config.LastFmApiKey;
            if (wantLastFm && string.IsNullOrWhiteSpace(lastFmKey))
            {
                // Degrade, do not fail: the MusicBrainz half needs no key and is the half that carries
                // genre. Naming the setting is the whole message — there is nothing for the operator
                // to debug, only a key to paste in.
                w.WriteLine("Last.fm: not configured (set LastFmApiKey in appsettings) — popularity is SKIPPED this run.");
                wantLastFm = false;
            }

            await using var db = await dbFactory.CreateDbContextAsync();
            var cache = new MusicResponseCache(CacheDir);

            var pendingTotal = await db.MusicAlbums.CountAsync(a => a.PopularityCheckedUtc == null && a.Id > After);
            var batch = await db.MusicAlbums
                .Where(a => a.PopularityCheckedUtc == null && a.Id > After)
                .OrderBy(a => a.Id)
                .Include(a => a.Artist)
                .Take(Math.Max(1, Take))
                .ToListAsync();

            int mbHits = 0, mbMisses = 0, popHits = 0, popMisses = 0, cacheHits = 0, genreRows = 0, errors = 0;
            using var http = MusicRemoteArt.CreateHttp();

            foreach (var album in batch)
            {
                var artist = album.Artist?.Name ?? "";
                var title = album.Title ?? "";
                var found = new List<(string Genre, int Weight)>();
                int? popularity = null;
                string? popularitySource = null;

                if (wantMb)
                {
                    try
                    {
                        var (tags, fromCache) = await MusicBrainzTagsAsync(http, cache, artist, title);
                        if (fromCache) cacheHits++;
                        if (tags.Count > 0) { mbHits++; found.AddRange(tags.Select(t => (t.Genre, t.Votes))); }
                        else mbMisses++;
                    }
                    catch (Exception ex) { errors++; if (Verbose) w.WriteLine($"  ! {album.Id} musicbrainz: {ex.Message}"); }
                }

                if (wantLastFm)
                {
                    try
                    {
                        var (listeners, lfmTags, fromCache) = await LastFmAsync(http, cache, lastFmKey!, artist, title);
                        if (fromCache) cacheHits++;
                        popularity = MusicPopularity.FromAudience(listeners);
                        if (popularity != null) { popHits++; popularitySource = MusicGenreSources.LastFm; }
                        else popMisses++;
                        // Last.fm's own top tags are genres too, kept under their own Source so the two
                        // externals never overwrite each other. The replace runs even on an EMPTY
                        // answer: a re-run that now gets nothing must clear what it wrote last time,
                        // or a retired tag lives on in the facet forever.
                        if (Apply)
                            genreRows += await ReplaceGenresAsync(db, album.Id, MusicGenreSources.LastFm,
                                lfmTags.Select(t => (t.Genre, t.Votes)).ToList());
                        else genreRows += lfmTags.Count;
                    }
                    catch (Exception ex) { errors++; if (Verbose) w.WriteLine($"  ! {album.Id} lastfm: {ex.Message}"); }
                }

                if (Apply)
                {
                    if (wantMb) genreRows += await ReplaceGenresAsync(db, album.Id, MusicGenreSources.MusicBrainz, found);
                    if (popularity != null) { album.Popularity = popularity; album.PopularitySource = popularitySource; }
                    // Stamped on a miss as well — the negative cache, and the queue's stop condition.
                    album.PopularityCheckedUtc = DateTime.UtcNow;
                }
                else genreRows += found.Count;

                if (Verbose)
                    // The genres as they would be WRITTEN, not the raw tags: a dry run's job is to
                    // show the shape, and the fold (splitting "pop/rock", dropping the values that
                    // mean nothing) is most of what there is to see.
                    w.WriteLine($"  {(found.Count > 0 || popularity != null ? "+" : "·")} {album.Id} {artist} — {title}: " +
                                $"genres [{string.Join(", ", found.SelectMany(f => MusicGenres.Split(f.Genre)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxGenresPerAlbum))}]" +
                                $" popularity {(popularity?.ToString() ?? "—")}");
            }

            if (Apply) await db.SaveChangesAsync();

            var nextCursor = batch.Count > 0 ? batch[^1].Id : After;
            var remaining = Apply ? Math.Max(0, pendingTotal - batch.Count) : pendingTotal;

            w.WriteLine();
            w.WriteLine($"looked up {batch.Count} album(s): musicbrainz {mbHits} hit / {mbMisses} miss, " +
                        $"popularity {popHits} hit / {popMisses} miss, {cacheHits} served from the disk cache, {errors} error(s)" +
                        (Apply ? "." : " — DRY RUN, nothing written."));
            w.WriteLine($"cache: {cache.Root}");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextCursor: {nextCursor}, " +
                        $"counts: {{ mbHits: {mbHits}, mbMisses: {mbMisses}, popHits: {popHits}, popMisses: {popMisses}, " +
                        $"genreRows: {genreRows}, cacheHits: {cacheHits}, errors: {errors} }} }}");
            // A dry run still FILLS THE CACHE, and that is deliberate: the point of the cache is that
            // a request is made once ever, so the answers a dry run collected are the answers the
            // --apply run parses, with no second trip to anyone's server. Nothing reaches the
            // database, and nothing is ever written under the music root.
            if (!Apply) w.WriteLine("DRY RUN — nothing written to the database (raw responses ARE cached above). Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after {nextCursor}.");
            if (Apply) w.WriteLine("Then run `music-genres --rollup-only --apply` to fold the new genres into the artist roll-ups.");
        }

        /// <summary>
        /// Replaces one SOURCE's genre rows for one album. The other sources' rows for the same album
        /// are never in scope — that is what makes Source part of the unique key.
        /// </summary>
        private static async Task<int> ReplaceGenresAsync(MovieDb db, int albumId, string source, List<(string Genre, int Weight)> genres)
        {
            var existing = await db.MusicAlbumGenres.Where(g => g.AlbumId == albumId && g.Source == source).ToListAsync();
            if (existing.Count > 0) db.MusicAlbumGenres.RemoveRange(existing);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var (genre, weight) in genres)
            {
                // SPLIT, not just Normalize: an external tag is as unruly as a file's own frame and
                // needs the same fold. Measured against MusicBrainz's crowd tags for one album:
                // "pop/rock", "alternative/indie rock" and "progressive rock_alternative rock" are
                // all one tag naming two genres, and storing them whole would put three unusable
                // singletons in the rail's long tail instead of votes on the pills already there.
                foreach (var norm in MusicGenres.Split(genre))
                {
                    if (!seen.Add(norm)) continue;
                    db.MusicAlbumGenres.Add(new MusicAlbumGenre
                    {
                        AlbumId = albumId, Genre = norm, Source = source, Weight = weight, CreatedUtc = DateTime.UtcNow,
                    });
                    added++;
                    if (added >= MaxGenresPerAlbum) return added;
                }
            }
            return added;
        }

        // ── MusicBrainz ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The release GROUP's tags for this album, most-voted first. The group rather than the
        /// release for the same reason the art lookup prefers it: tags are filed against the record,
        /// not against the particular pressing a search happened to rank first.
        /// </summary>
        private async Task<(List<(string Genre, int Votes)> Tags, bool FromCache)> MusicBrainzTagsAsync(
            HttpClient http, MusicResponseCache cache, string artist, string album)
        {
            var query = $"artist:\"{MusicRemoteArt.Sanitize(artist)}\" AND releasegroup:\"{MusicRemoteArt.Sanitize(album)}\"";
            var url = $"https://musicbrainz.org/ws/2/release-group/?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";
            var (json, fromCache) = await GetCachedAsync(http, cache, "musicbrainz", query, url, maxAge: null);
            var outList = new List<(string, int)>();
            if (json == null) return (outList, fromCache);

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("release-groups", out var groups)) return (outList, fromCache);
                foreach (var rg in groups.EnumerateArray())
                {
                    var title = rg.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var credit = CreditOf(rg);
                    // The SAME acceptance gate the art lookup uses (MusicRemoteArtMatchTests pins it).
                    // A confidently wrong genre is the same failure as a confidently wrong cover.
                    if (!MusicRemoteArt.Accepts(title, credit, album, artist, titleOnly: false)) continue;
                    if (!rg.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) continue;
                    foreach (var tag in tags.EnumerateArray())
                    {
                        var name = tag.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var votes = tag.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                        if (name == null || votes < MinTagVotes) continue;
                        outList.Add((name, votes));
                    }
                    if (outList.Count > 0) break; // the best-matching group answers; do not merge pressings
                }
            }
            catch (JsonException) { /* malformed answer = miss, same posture as the art lookup */ }

            return (outList.OrderByDescending(x => x.Item2).Take(MaxGenresPerAlbum).ToList(), fromCache);
        }

        private static string CreditOf(JsonElement element)
        {
            if (!element.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) return "";
            return string.Join(" ", credits.EnumerateArray()
                .Select(c => c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                .Where(n => n.Length > 0));
        }

        // ── Last.fm ─────────────────────────────────────────────────────────────────────────────────

        private async Task<(long? Listeners, List<(string Genre, int Votes)> Tags, bool FromCache)> LastFmAsync(
            HttpClient http, MusicResponseCache cache, string apiKey, string artist, string album)
        {
            var key = $"album.getinfo|{artist}|{album}";
            var url = "https://ws.audioscrobbler.com/2.0/?method=album.getinfo" +
                      $"&artist={Uri.EscapeDataString(artist)}&album={Uri.EscapeDataString(album)}" +
                      $"&api_key={Uri.EscapeDataString(apiKey)}&format=json&autocorrect=1";
            var (json, fromCache) = await GetCachedAsync(http, cache, "lastfm", key, url, PopularityTtl,
                // The key is in the URL and the cache is on disk in a repo working tree. Store the
                // request with the secret blanked — the answer is the artefact worth keeping, and a
                // key in a sidecar is a key that leaks the first time somebody zips data/.
                urlForMeta: url.Replace(Uri.EscapeDataString(apiKey), "«key»"));
            var tags = new List<(string, int)>();
            if (json == null) return (null, tags, fromCache);

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("album", out var a)) return (null, tags, fromCache);
                long? listeners = null;
                if (a.TryGetProperty("listeners", out var l) && long.TryParse(l.GetString(), out var parsed)) listeners = parsed;
                if (a.TryGetProperty("tags", out var tagRoot) && tagRoot.TryGetProperty("tag", out var tagArr)
                    && tagArr.ValueKind == JsonValueKind.Array)
                {
                    // Last.fm's top tags come ranked but unweighted; rank IS the weight, descending, so
                    // the strongest tag keeps the biggest number the way the other sources' do.
                    int rank = tagArr.GetArrayLength();
                    foreach (var tag in tagArr.EnumerateArray())
                    {
                        var name = tag.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (name != null) tags.Add((name, rank));
                        rank--;
                    }
                }
                return (listeners, tags.Take(MaxGenresPerAlbum).ToList(), fromCache);
            }
            catch (JsonException) { return (null, tags, fromCache); }
        }

        // ── the wire ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The cached body, fetching it once if it is not on disk (or is past <paramref name="maxAge"/>).
        /// A network failure is a MISS, never a throw — the caller stamps the negative cache either way,
        /// which is the same posture the art lookup takes.
        /// </summary>
        private async Task<(string? Body, bool FromCache)> GetCachedAsync(
            HttpClient http, MusicResponseCache cache, string source, string key, string url,
            TimeSpan? maxAge, string? urlForMeta = null)
        {
            var cached = await cache.TryReadAsync(source, key, maxAge);
            if (cached != null) return (cached, true);

            // The process-wide gate MusicRemoteArt owns, not a second one: two gates would silently
            // double the request rate MusicBrainz asks us to hold to.
            await MusicRemoteArt.Gate.WaitAsync();
            try
            {
                await MusicRemoteArt.SpaceCallAsync();
                using var response = await http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) return (null, false);
                // Raw, before any parsing — the whole point of the cache.
                await cache.SaveAsync(source, key, body, urlForMeta ?? url, (int)response.StatusCode);
                return (body, false);
            }
            catch (HttpRequestException) { return (null, false); }
            catch (TaskCanceledException) { return (null, false); }
            finally { MusicRemoteArt.Gate.Release(); }
        }
    }
}
