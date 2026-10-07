using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Requests
{
    /// <summary>A catalog row the matcher believes answers a request.</summary>
    public sealed record RequestMatch(string Kind, int Id, string Title, int? Year)
    {
        public string Key => Kind + ":" + Id;
    }

    /// <summary>What one bounded sweep did: how many open requests it looked at, how many gained a
    /// proposal, where the next call should start, and how many are left past that point.</summary>
    public sealed record SweepResult(int Checked, int Proposed, int? NextFrom, int Remaining);

    /// <summary>
    /// The LOOSE resolver behind the request queue (<see cref="ContentRequest"/>): given a request, find
    /// the one catalog row it most plausibly asks for, in the section it names. It never closes a
    /// request — it writes a PROPOSAL the admin confirms or dismisses on the Requests page.
    ///
    /// <para>Loose on purpose. Requests are typed on a phone in a shop ("the new dune", "Catan
    /// seafarers"), and the catalog's titles come from IMDb / BGG / folder names, so the comparison is
    /// on a normalized form: lowercased, diacritics folded, leading article dropped, "&amp;" → "and",
    /// punctuation stripped. Two titles AGREE when the normalized forms are equal, or when one contains
    /// the other as whole tokens and the shorter one is substantial enough not to be a stop word (two
    /// tokens, or a single token of four letters or more). A year, when both sides have one, must sit within a year — IMDb's and a shop's idea
    /// of a release year differ at the turn of a year often enough that exact would miss real matches.</para>
    ///
    /// <para>Cheap on purpose. The candidate pull is ONE indexed-ish LIKE on the title column for the
    /// request's most distinctive token (its longest), capped, and the scoring happens in memory over
    /// those few rows. Books are not matched: that library lives in the Books host's SQLite, out of
    /// this context's reach, so a books request is closed by hand.</para>
    /// </summary>
    public class ContentRequestMatcher
    {
        private const int CandidateCap = 60;

        private readonly MovieDb db;

        public ContentRequestMatcher(MovieDb db)
        {
            this.db = db;
        }

        // ── Normalization (pure; unit-tested) ────────────────────────────────────────────────────

        private static readonly Regex NonWord = new("[^a-z0-9 ]+", RegexOptions.Compiled);
        private static readonly Regex NonWordKeepMark = new("[^a-z0-9 \u0001]+", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(" {2,}", RegexOptions.Compiled);
        private static readonly string[] Articles = { "the ", "a ", "an " };

        /// <summary>The comparison form of a title. "Thé Lord &amp; the Rings: Part II!" → "lord and the rings part ii".</summary>
        public static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            // The ingest mappers' fold (lowercase + ASCII, ligatures included) — one fold for the site.
            var t = Ingest.TitleNorm.Fold(s).Replace("&", " and ").Replace("'", "");
            t = NonWord.Replace(t, " ");
            t = Spaces.Replace(t, " ").Trim();
            foreach (var a in Articles)
            {
                if (t.StartsWith(a, StringComparison.Ordinal) && t.Length > a.Length) { t = t.Substring(a.Length); break; }
            }
            return t;
        }

        /// <summary>The tokens worth probing the catalog with, most distinctive first: longest token first,
        /// ties by position. Single-letter tokens are never probes.</summary>
        public static List<string> ProbeWords(string normalized)
        {
            return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select((w, i) => (w, i))
                .Where(x => x.w.Length >= 2)
                .OrderByDescending(x => x.w.Length).ThenBy(x => x.i)
                .Select(x => x.w)
                .Take(2)
                .ToList();
        }

        /// <summary>
        /// The LIKE patterns for a RAW title: the same tokens as <see cref="ProbeWords"/>, but an apostrophe
        /// inside a token becomes a <c>%</c> so "assassins" still reaches a catalog row spelled "Assassin's"
        /// (the catalog keeps its apostrophes; the normalized form drops them). Letters are the only other
        /// characters, so no LIKE metacharacter can be injected.
        /// </summary>
        public static List<string> ProbePatterns(string? rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle)) return new List<string>();
            var t = Ingest.TitleNorm.Fold(rawTitle).Replace("&", " and ").Replace("'", "\u0001").Replace("\u2019", "\u0001");
            t = NonWordKeepMark.Replace(t, " ");
            t = Spaces.Replace(t, " ").Trim();
            foreach (var a in Articles)
            {
                if (t.StartsWith(a, StringComparison.Ordinal) && t.Length > a.Length) { t = t.Substring(a.Length); break; }
            }
            return t.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select((w, i) => (w, i, len: w.Replace("\u0001", "").Length))
                .Where(x => x.len >= 2)
                .OrderByDescending(x => x.len).ThenBy(x => x.i)
                .Select(x => x.w.Replace("\u0001", "%"))
                .Take(2)
                .ToList();
        }

        /// <summary>Do two NORMALIZED titles name the same thing, loosely? Equal, or one contains the other
        /// and the shorter is substantial: two tokens, or a single token of four letters or more — so "dune"
        /// reaches "dune part two" while "it" never reaches "it follows".</summary>
        public static bool TitlesAgree(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
            if (shorter.Length < 4 && !shorter.Contains(' ')) return false;
            // Whole-token containment: " x " inside " y " so "cat" never matches "catan".
            return (" " + longer + " ").Contains(" " + shorter + " ", StringComparison.Ordinal);
        }

        public static bool YearsAgree(int? requested, int? candidate) =>
            requested == null || candidate == null || Math.Abs(requested.Value - candidate.Value) <= 1;

        /// <summary>Rank a candidate against the request: 3 = exact title + year agrees, 2 = exact title,
        /// 1 = loose title + year agrees, 0 = no. Loose-title-with-a-wrong-year is a no: the loose rule is
        /// already generous and the year was the one thing the requester told us for certain.</summary>
        public static int Score(string reqNorm, int? reqYear, string candNorm, int? candYear)
        {
            if (!TitlesAgree(reqNorm, candNorm)) return 0;
            var exact = reqNorm == candNorm;
            var year = YearsAgree(reqYear, candYear);
            if (exact) return year ? 3 : 2;
            return year ? 1 : 0;
        }

        // ── The lookup ───────────────────────────────────────────────────────────────────────────

        private sealed record Candidate(string Kind, int Id, string Title, int? Year, string? Side);

        /// <summary>
        /// The best catalog row for this request, or null. <paramref name="skipKey"/> is the proposal an
        /// admin already dismissed ("kind:id") — never returned again, though a different row may be.
        /// </summary>
        public async Task<RequestMatch?> FindAsync(ContentRequest request, string? skipKey, CancellationToken ct)
        {
            var reqNorm = Normalize(request.Title);
            if (reqNorm.Length == 0) return null;
            var probes = ProbePatterns(request.Title);
            if (probes.Count == 0) return null;
            var detailNorm = Normalize(request.Detail);
            var exact = request.Title.Trim();

            foreach (var probe in probes)
            {
                var candidates = await PullCandidatesAsync(request.Section, probe, exact, ct);
                var best = candidates
                    .Select(c => (c, score: Score(reqNorm, request.Year, Normalize(c.Title), c.Year) + SideBonus(detailNorm, c.Side)))
                    .Where(x => x.score > 0 && (skipKey == null || x.c.Kind + ":" + x.c.Id != skipKey))
                    .OrderByDescending(x => x.score)
                    .ThenBy(x => x.c.Id)
                    .FirstOrDefault();
                if (best.c != null) return new RequestMatch(best.c.Kind, best.c.Id, best.c.Title, best.c.Year);
                // The longest token missed entirely (a diacritic the catalog kept, say); try the next.
            }
            return null;
        }

        /// <summary>The section's disambiguator, when given, breaks ties: an album whose ARTIST agrees with
        /// what the requester typed outranks one that merely shares a title. It never rescues a zero.</summary>
        private static int SideBonus(string detailNorm, string? side)
        {
            if (detailNorm.Length == 0 || string.IsNullOrEmpty(side)) return 0;
            return TitlesAgree(detailNorm, Normalize(side)) ? 1 : 0;
        }

        private async Task<List<Candidate>> PullCandidatesAsync(string section, string probe, string exact, CancellationToken ct)
        {
            // Two pulls per table, merged: the EXACT title (cheap; immune to the cap; the live SQL Server
            // collation is case-insensitive) and the LIKE probe, ordered shortest title first then newest
            // id first — a short probe ("her", "star") overflows the cap, and the rows that must survive it
            // are the near-exact ones and the newest, which is what the sweep exists to notice.
            var like = "%" + probe + "%";
            switch (section)
            {
                case ContentRequestSections.Movies:
                {
                    // Movies and series share the request section (the site's Movie Theater holds both).
                    // Only titles we can actually play: a mapped media file that is not missing. The site
                    // began as a watched-list tracker and still holds rows with no file — those are not
                    // "we have it". Pending-review ingest rows (ReviewBatch set) are deliberately INCLUDED:
                    // "it just came in" is exactly the moment to resolve a request.
                    var movieQ = db.Movies.AsNoTracking()
                        .Where(m => m.Playable != null && m.Playable.Files.Any(f => f.MissingSinceUtc == null));
                    var movies = await movieQ.Where(m => m.Title == exact || m.SimpleTitle == exact)
                        .Select(m => new { Id = m.id, m.Title, m.ReleaseDate, m.ImdbReleaseDate }).Take(CandidateCap).ToListAsync(ct);
                    movies.AddRange(await movieQ
                        .Where(m => (m.Title != null && EF.Functions.Like(m.Title, like)) || (m.SimpleTitle != null && EF.Functions.Like(m.SimpleTitle, like)))
                        .OrderBy(m => m.Title!.Length).ThenByDescending(m => m.id).Take(CandidateCap)
                        .Select(m => new { Id = m.id, m.Title, m.ReleaseDate, m.ImdbReleaseDate })
                        .ToListAsync(ct));
                    var seriesQ = db.Series.AsNoTracking()
                        .Where(s => db.Episodes.Any(e => e.SeriesId == s.Id && e.Playable != null && e.Playable.Files.Any(f => f.MissingSinceUtc == null)));
                    var series = await seriesQ.Where(s => s.Title == exact || s.SimpleTitle == exact)
                        .Select(s => new { s.Id, s.Title, s.StartYear }).Take(CandidateCap).ToListAsync(ct);
                    series.AddRange(await seriesQ
                        .Where(s => (s.Title != null && EF.Functions.Like(s.Title, like)) || (s.SimpleTitle != null && EF.Functions.Like(s.SimpleTitle, like)))
                        .OrderBy(s => s.Title!.Length).ThenByDescending(s => s.Id).Take(CandidateCap)
                        .Select(s => new { s.Id, s.Title, s.StartYear })
                        .ToListAsync(ct));
                    var list = movies.DistinctBy(m => m.Id).Select(m => new Candidate("movie", m.Id, m.Title ?? "", (m.ReleaseDate ?? m.ImdbReleaseDate)?.Year, null)).ToList();
                    list.AddRange(series.DistinctBy(s => s.Id).Select(s => new Candidate("series", s.Id, s.Title ?? "", s.StartYear, null)));
                    return list;
                }
                case ContentRequestSections.Music:
                {
                    // An album by title (artist as the tie-breaker), or an ARTIST when the request names one
                    // outright ("Get us some Bush") — the whole discography is the ask then.
                    var albums = await db.MusicAlbums.AsNoTracking().Where(a => a.Title == exact)
                        .Select(a => new { a.Id, a.Title, a.Year, Artist = a.Artist.Name }).Take(CandidateCap).ToListAsync(ct);
                    albums.AddRange(await db.MusicAlbums.AsNoTracking()
                        .Where(a => EF.Functions.Like(a.Title, like))
                        .OrderBy(a => a.Title.Length).ThenByDescending(a => a.Id).Take(CandidateCap)
                        .Select(a => new { a.Id, a.Title, a.Year, Artist = a.Artist.Name })
                        .ToListAsync(ct));
                    var artists = await db.MusicArtists.AsNoTracking().Where(a => a.Name == exact)
                        .Select(a => new { a.Id, a.Name }).Take(CandidateCap).ToListAsync(ct);
                    artists.AddRange(await db.MusicArtists.AsNoTracking()
                        .Where(a => EF.Functions.Like(a.Name, like))
                        .OrderBy(a => a.Name.Length).ThenByDescending(a => a.Id).Take(CandidateCap)
                        .Select(a => new { a.Id, a.Name })
                        .ToListAsync(ct));
                    // Scored on the BARE album title (the artist is the Side tie-breaker); the queue's
                    // "Artist — Title" label is put back by DisplayTitleAsync.
                    var list = albums.DistinctBy(a => a.Id).Select(a => new Candidate("album", a.Id, a.Title, a.Year, a.Artist)).ToList();
                    list.AddRange(artists.DistinctBy(a => a.Id).Select(a => new Candidate("artist", a.Id, a.Name, null, null)));
                    return list;
                }
                case ContentRequestSections.Boardgames:
                {
                    var games = await db.Boardgames.AsNoTracking().Where(b => b.Name == exact)
                        .Select(b => new { Id = b.id, b.Name, b.YearPublished }).Take(CandidateCap).ToListAsync(ct);
                    games.AddRange(await db.Boardgames.AsNoTracking()
                        .Where(b => b.Name != null && EF.Functions.Like(b.Name, like))
                        .OrderBy(b => b.Name!.Length).ThenByDescending(b => b.id).Take(CandidateCap)
                        .Select(b => new { Id = b.id, b.Name, b.YearPublished })
                        .ToListAsync(ct));
                    return games.DistinctBy(b => b.Id).Select(b => new Candidate("boardgame", b.Id, b.Name ?? "", b.YearPublished, null)).ToList();
                }
                case ContentRequestSections.Arcade:
                {
                    // Vanished ROMs are IsEnabled = false — not "we have it".
                    var gamesQ = db.ArcadeGames.AsNoTracking().Where(g => g.IsEnabled);
                    var games = await gamesQ.Where(g => g.Title == exact)
                        .Select(g => new { g.Id, g.Title, g.Year, g.System }).Take(CandidateCap).ToListAsync(ct);
                    games.AddRange(await gamesQ
                        .Where(g => EF.Functions.Like(g.Title, like))
                        .OrderBy(g => g.Title.Length).ThenByDescending(g => g.Id).Take(CandidateCap)
                        .Select(g => new { g.Id, g.Title, g.Year, g.System })
                        .ToListAsync(ct));
                    return games.DistinctBy(g => g.Id).Select(g => new Candidate("arcade", g.Id, g.Title, g.Year, g.System)).ToList();
                }
                default:
                    // Books live in the Books host's SQLite — not reachable from here; closed by hand.
                    return new List<Candidate>();
            }
        }

        /// <summary>The queue's display label for a match — an album is named with its artist.</summary>
        public async Task<string> DisplayTitleAsync(RequestMatch match, CancellationToken ct)
        {
            if (match.Kind != "album") return match.Title;
            var artist = await db.MusicAlbums.AsNoTracking().Where(a => a.Id == match.Id).Select(a => a.Artist.Name).FirstOrDefaultAsync(ct);
            return artist == null ? match.Title : artist + " — " + match.Title;
        }

        // ── The sweep ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Propose matches for OPEN requests that have no standing proposal, in id order from
        /// <paramref name="from"/> (exclusive), at most <paramref name="limit"/> per call. Bounded and
        /// resumable: the caller (the hosted sweep, or the admin's button) drives it to
        /// <c>NextFrom == null</c>. A request whose only agreeing row is the one an admin dismissed stays
        /// unproposed; a NEW agreeing row is proposed.
        /// </summary>
        public async Task<SweepResult> SweepAsync(int from, int limit, CancellationToken ct)
        {
            limit = Math.Clamp(limit, 1, 200);
            var batch = await db.ContentRequests
                .Where(r => r.Status == ContentRequestStatuses.Open && r.MatchKind == null && r.Id > from)
                .OrderBy(r => r.Id)
                .Take(limit + 1)
                .ToListAsync(ct);
            var hasMore = batch.Count > limit;
            if (hasMore) batch.RemoveAt(batch.Count - 1);

            var proposed = 0;
            var now = DateTime.UtcNow;
            foreach (var r in batch)
            {
                ct.ThrowIfCancellationRequested();
                var match = await FindAsync(r, r.DismissedMatchKey, ct);
                if (match == null) continue;
                r.MatchKind = match.Kind;
                r.MatchId = match.Id;
                r.MatchTitle = Truncate(await DisplayTitleAsync(match, ct) + (match.Year != null ? $" ({match.Year})" : ""), 300);
                r.MatchFoundUtc = now;
                proposed++;
            }
            if (proposed > 0) await db.SaveChangesAsync(ct);

            var lastId = batch.Count > 0 ? batch[^1].Id : from;
            var remaining = hasMore
                ? await db.ContentRequests.CountAsync(r => r.Status == ContentRequestStatuses.Open && r.MatchKind == null && r.Id > lastId, ct)
                : 0;
            return new SweepResult(batch.Count, proposed, hasMore ? lastId : null, remaining);
        }

        internal static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);
    }
}
