using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Models;
using MovieTheater.Normalization;
using MovieTheater.Services;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Poster;
using MovieTheater.Services.BoardgameImage;
using MovieTheater.Services.Tmdb;
using MovieTheater.Services.Omdb;
using MovieTheater.Services.Google;
using MovieTheater.Services.Bgg;

namespace MovieTheater.Controllers
{
    public partial class APIController
    {
        [HttpGet("/API/GetRandomMovies")]
        public async Task<IActionResult> GetRandomMovies(int take = 50)
        {
            var mq = await GetBaseMovieQuery();
            var sq = await GetBaseSeriesQuery();
            var movies = await mq.Where(m => !m.RemoveFromRandom).OrderBy(m => Guid.NewGuid()).Take(take).Select(ToCardDto).ToListAsync();
            // Sprinkle a proportional number of series into the random landing grid.
            var series = await sq.Where(s => !s.RemoveFromRandom).OrderBy(s => Guid.NewGuid()).Take(Math.Max(1, take / 10)).Select(ToSeriesCardDto).ToListAsync();
            var all = movies.Concat(series).ToList();
            var rng = new Random();
            for (int i = all.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (all[i], all[j]) = (all[j], all[i]); }
            return Ok(all.Take(take).ToList());
        }

        // Browse filtered by the coarse, normalized Title Type bucket(s). `type` is a comma-separated
        // list (OR across types, like the multi-select genre filter): a title shows if its bucket is any
        // selected. Movies/Short read Movie.NormalizedTitleType (a persisted computed column off
        // TitleType); Series come from the Series table; Misc from the MiscVideo table (tt-less library
        // videos — workprints, stage performances, shorts sets).
        // Returns the { movies, totalCount, page, pageSize } envelope so the client can infinite-scroll
        // instead of pulling the whole library at once. pageSize <= 0 returns everything (the old
        // behavior) for any caller that still wants the full list. Combos without Misc stay fully
        // DB-paged; only Misc-inclusive combos pay an in-memory merge (Misc is a materialized list).
        [HttpGet("/API/GetMoviesByType")]
        public async Task<IActionResult> GetMoviesByType(string type, int page = 1, int pageSize = 60, int? seed = null, string? sort = null)
        {
            var types = ParseTypeScope(type);
            if (types.Count == 0)
                return BadRequest(new { Message = $"Unknown title type '{type}'" });

            // The `sort` param drives the order (Random/Alphabetical/Recently Added/IMDB/RT/Popcornmeter).
            // A bare legacy `seed` with no sort — the URL shape the old landing grid used — means the
            // shuffled discovery grid, which IS the random sort now, so it just maps onto it. With
            // neither, A→Z by title.
            bool hasSort = sort != null || seed.HasValue;
            string s = NormalizeSort(sort ?? (seed.HasValue ? "random" : null));
            int sd = seed ?? 0;

            bool wantSeries = types.Contains(NormalizedTitleType.Series);
            bool wantMisc = types.Contains(NormalizedTitleType.Misc);
            // Movies and Short both live in the Movie table, keyed by the NormalizedTitleType column.
            var movieBuckets = types.Where(t => t == NormalizedTitleType.Movies || t == NormalizedTitleType.Short).ToList();

            IQueryable<Movie>? mq = movieBuckets.Count > 0
                ? (await GetBaseMovieQuery()).Where(m => movieBuckets.Contains(m.NormalizedTitleType))
                : null;
            IQueryable<Series>? sq = wantSeries ? await GetBaseSeriesQuery() : null;

            // Misc alone: an explicit sort orders it; otherwise it keeps its curated collection ordering.
            if (wantMisc && mq == null && sq == null)
            {
                var misc = await GetMiscCards();
                return Ok(PageCards(hasSort ? SortCards(misc, s, sd) : misc, page, pageSize));
            }

            if (!wantMisc)
            {
                // Pure-DB paths — no Misc, so everything pages at the database.
                if (mq != null && sq != null)
                    return Ok(await PageMergedAsync(mq, sq, page, pageSize, s, sd));
                if (mq != null)
                    return Ok(await PageCardsAsync(SortMovies(mq, s, sd).Select(ToCardDto), page, pageSize));
                return Ok(await PageCardsAsync(SortSeries(sq!, s, sd).Select(ToSeriesCardDto), page, pageSize));
            }

            // Misc mixed with movies/series → merge all selected sources in memory, ordered uniformly by
            // the chosen sort (Misc's own table can't UNION with the Movie/Series queries).
            var cards = new List<MovieCardDto>();
            if (mq != null) cards.AddRange(await mq.Select(ToCardDto).ToListAsync());
            if (sq != null) cards.AddRange(await sq.Select(ToSeriesCardDto).ToListAsync());
            cards.AddRange(await GetMiscCards());
            return Ok(PageCards(SortCards(cards, s, sd), page, pageSize));
        }

        // A–Z bucket sizes + offsets for an alphabetically-ordered browse — what the Browse page's letter
        // pager jumps with (offset → slot index), the same strip the music library and the arcade lobby
        // use. Letter boundaries agree with the page ordering because both order by SimpleTitle under the
        // same SQL collation (offsets are counted by walking the ordered key list itself, the arcade
        // GameLetters approach).
        //
        // `mode`/`value` name the SAME filter the matching browse endpoint applies — they go through the
        // one ApplyBrowseFilter below, so the two can't drift into bucketing different rows than they
        // page. No mode = the plain Type-scope browse (GetMoviesByType).
        //
        // Misc-inclusive scopes get no letters (their ordering is a curated in-memory merge, so there is
        // no DB row order to walk) and the pager falls back to page numbers client-side. Only meaningful
        // for the alpha sort; the client never calls this under any other.
        [HttpGet("/API/BrowseLetters")]
        public async Task<IActionResult> BrowseLetters(string type, string? mode = null, string? value = null)
        {
            var types = ParseTypeScope(type);
            if (types.Count == 0)
                return BadRequest(new { Message = $"Unknown title type '{type}'" });
            if (types.Contains(NormalizedTitleType.Misc))
                return Ok(new { total = 0, letters = new List<object>() });

            var (mq, sq) = ApplyBrowseFilter(await GetBaseMovieQuery(), await GetBaseSeriesQuery(), mode, value);
            (mq, sq) = ApplyTypeScope(types, mq, sq);

            var keys = await OrderCardKeys(BuildCardKeys(mq, sq, "alpha"), "alpha").Select(k => k.SimpleTitle).ToListAsync();
            var letters = Web.LetterBuckets.Walk(keys)
                .Select(b => new { letter = b.Letter, count = b.Count, offset = b.Offset }).ToList();
            return Ok(new { total = keys.Count, letters });
        }

        // ── The browse modes' filters, in one place ─────────────────────────────────────────────
        // Each /API/Browse* endpoint narrows the movie + series base queries the same way; expressing it
        // once means /API/BrowseLetters can bucket EXACTLY the rows the endpoint will page. `mode` is the
        // client's URL mode param; an unknown or absent mode is the unfiltered Type-scope browse.
        //
        // (Rating browse is deliberately absent: it is movie-only and lives in APIController.Viewings —
        // its letters would need a different walk, and the client asks for none.)
        private (IQueryable<Movie> Movies, IQueryable<Series> Series) ApplyBrowseFilter(
            IQueryable<Movie> mq, IQueryable<Series> sq, string? mode, string? value)
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) return (mq, sq);

            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "title":
                    return (mq.Where(m => (m.SimpleTitle != null && m.SimpleTitle.Contains(v)) || (m.Title != null && m.Title.Contains(v))),
                            sq.Where(s => (s.SimpleTitle != null && s.SimpleTitle.Contains(v)) || (s.Title != null && s.Title.Contains(v))));

                case "actor":
                    return (mq.Where(m => m.Credits.Any(c => c.Person.DisplayName.Contains(v))
                                || (m.Actors != null && m.Actors.Contains(v)) || (m.Director != null && m.Director.Contains(v)) || (m.Writer != null && m.Writer.Contains(v))),
                            sq.Where(s => s.Credits.Any(c => c.Person.DisplayName.Contains(v))
                                || (s.Actors != null && s.Actors.Contains(v)) || (s.Director != null && s.Director.Contains(v)) || (s.Writer != null && s.Writer.Contains(v))));

                case "letter":
                    // "#" is the digit bucket — a LIKE character class, since EF has no StartsWithAny.
                    if (v == "#")
                        return (mq.Where(m => EF.Functions.Like(m.SimpleTitle, "[0-9]%")),
                                sq.Where(s => EF.Functions.Like(s.SimpleTitle, "[0-9]%")));
                    return (mq.Where(m => m.SimpleTitle != null && m.SimpleTitle.StartsWith(v)),
                            sq.Where(s => s.SimpleTitle != null && s.SimpleTitle.StartsWith(v)));

                case "genre":
                    // AND across the selected genres (each one narrows further), unlike the Type scope's OR.
                    var list = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (list.Length == 0) return (mq.Where(m => false), sq.Where(s => false)); // e.g. ",,": a filter that names nothing matches nothing
                    foreach (var g in list)
                    {
                        var gg = g;
                        mq = mq.Where(m => m.MovieGenres.Any(x => x.Genre.Name == gg) || (m.Genre != null && m.Genre.Contains(gg)));
                        sq = sq.Where(s => s.SeriesGenres.Any(x => x.Genre.Name == gg) || (s.Genre != null && s.Genre.Contains(gg)));
                    }
                    return (mq, sq);

                case "franchise":
                    // The tag must sit on the title's NEWEST insight — the rule GetFranchiseRail and the grouped
                    // browse (Web.BrowseGroups) apply. Matching a superseded generation's tag here made "more of
                    // this franchise" disagree with the shelf it was opened from.
                    return (mq.Where(m => movieDb.TitleTags.Any(t => t.Category == TagCategory.Franchise && t.Value == v
                                && t.Insight.SubjectKind == InsightSubjectKind.Movie && t.Insight.SubjectId == m.id
                                && t.Insight.GeneratedUtc == movieDb.TitleInsights
                                    .Where(x => x.SubjectKind == InsightSubjectKind.Movie && x.SubjectId == m.id).Max(x => x.GeneratedUtc))),
                            sq.Where(s => movieDb.TitleTags.Any(t => t.Category == TagCategory.Franchise && t.Value == v
                                && t.Insight.SubjectKind == InsightSubjectKind.Series && t.Insight.SubjectId == s.Id
                                && t.Insight.GeneratedUtc == movieDb.TitleInsights
                                    .Where(x => x.SubjectKind == InsightSubjectKind.Series && x.SubjectId == s.Id).Max(x => x.GeneratedUtc))));

                default:
                    return (mq, sq);
            }
        }

        // Parse the comma-separated Title-Type scope — the persistent Browse "Type" filter, applied as an
        // overarching scope across every browse mode. An empty result means no scope (all types).
        private static HashSet<NormalizedTitleType> ParseTypeScope(string? types) =>
            (types ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => Enum.TryParse<NormalizedTitleType>(t, true, out var nt) ? nt : (NormalizedTitleType?)null)
                .Where(nt => nt.HasValue)
                .Select(nt => nt!.Value)
                .ToHashSet();

        // Narrow base movie/series queries to a Type scope. Movies/Short live in the Movie table (keyed by
        // NormalizedTitleType); Series is the whole Series table; Misc has no genre/cast/title-search
        // presence so it never participates here. An empty scope is a no-op (all types). A kind absent from
        // the scope is emptied so it contributes nothing to the merged result.
        private static (IQueryable<Movie> Movies, IQueryable<Series> Series) ApplyTypeScope(
            HashSet<NormalizedTitleType> scope, IQueryable<Movie> mq, IQueryable<Series> sq)
        {
            if (scope.Count == 0)
                return (mq, sq);
            var movieBuckets = scope.Where(t => t == NormalizedTitleType.Movies || t == NormalizedTitleType.Short).ToList();
            mq = movieBuckets.Count > 0 ? mq.Where(m => movieBuckets.Contains(m.NormalizedTitleType)) : mq.Where(m => false);
            sq = scope.Contains(NormalizedTitleType.Series) ? sq : sq.Where(s => false);
            return (mq, sq);
        }

        // Page a card query at the DB (SELECT just one page + a COUNT). The query MUST already
        // be ordered so Skip/Take is stable. pageSize <= 0 → return the whole set.
        private static async Task<object> PageCardsAsync(IQueryable<MovieCardDto> ordered, int page, int pageSize)
        {
            if (pageSize <= 0)
            {
                var all = await ordered.ToListAsync();
                return new { movies = all, totalCount = all.Count, page = 1, pageSize = all.Count };
            }
            if (page < 1) page = 1;
            // Only the first page's totalCount is consumed by the client — it sets the "Showing X of Y"
            // header and the infinite-scroll hasMore bound once, then ignores it on every subsequent
            // page fetch. So skip the COUNT round-trip on page > 1 (-1 = "not computed").
            var totalCount = page == 1 ? await ordered.CountAsync() : -1;
            var paged = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return new { movies = paged, totalCount, page, pageSize };
        }

        // In-memory peer of PageCardsAsync for already-materialized card lists (e.g. Misc).
        private static object PageCards(List<MovieCardDto> all, int page, int pageSize)
        {
            var totalCount = all.Count;
            if (pageSize <= 0)
                return new { movies = all, totalCount, page = 1, pageSize = totalCount };
            if (page < 1) page = 1;
            var paged = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return new { movies = paged, totalCount, page, pageSize };
        }

        // Lightweight ordering key for the merged movie+series browse modes. Projecting just these
        // scalars (no navigations) keeps the cross-table UNION trivially translatable.
        private class CardKey
        {
            public string Kind { get; set; } = "movie";
            public int Id { get; set; }
            public string? SimpleTitle { get; set; }
            /// <summary>The active rating metric for a rating sort (null for alpha sort or an unscored
            /// title). Kept as decimal so the int RT scores and the decimal IMDB rating share one column.</summary>
            public decimal? SortValue { get; set; }

            /// <summary>The library add-date for the "Recently Added" sort (null for other sorts / undated rows).</summary>
            public DateTime? SortDate { get; set; }

            /// <summary>The seeded shuffle key for the random sort (null for every other sort). See
            /// ShuffleKeyOf — the series branch carries the salt that keeps the overlapping id spaces apart.</summary>
            public long? ShuffleKey { get; set; }
        }

        // The ordering-key UNION for the merged movie+series browse, projecting the active sort metric
        // into CardKey.SortValue. Branching per sort (rather than a parameterized CASE) keeps the
        // generated SQL clean — and the alpha case projects no rating column at all.
        private static IQueryable<CardKey> BuildCardKeys(IQueryable<Movie> mq, IQueryable<Series> sq, string sort, int seed = 0) => sort switch
        {
            "imdb" => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, SortValue = m.ImdbRatingScraped ?? m.imdbRating })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, SortValue = s.ImdbRatingScraped ?? s.imdbRating })),
            "rt" => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, SortValue = (decimal?)m.RtTomatometer })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, SortValue = (decimal?)s.RtTomatometer })),
            "popcorn" => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, SortValue = (decimal?)m.RtPopcornmeter })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, SortValue = (decimal?)s.RtPopcornmeter })),
            "added" => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, SortDate = m.UploadedDate })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, SortDate = s.UploadedDate })),
            "random" => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle, ShuffleKey = ((long)m.id + seed) * ShuffleMul % ShuffleMod })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle, ShuffleKey = ((long)s.Id + seed + SeriesShuffleSalt) * ShuffleMul % ShuffleMod })),
            _ => mq.Select(m => new CardKey { Kind = "movie", Id = m.id, SimpleTitle = m.SimpleTitle })
                .Concat(sq.Select(s => new CardKey { Kind = "series", Id = s.Id, SimpleTitle = s.SimpleTitle })),
        };

        // Order merged keys by the chosen sort: rating sorts desc with unscored (null → -1) last, then
        // SimpleTitle/Kind/Id as a stable tiebreak; alpha is SimpleTitle/Kind/Id. Random orders by the
        // seeded shuffle key — already unique per title, so Kind/Id is only belt-and-braces.
        private static IOrderedQueryable<CardKey> OrderCardKeys(IQueryable<CardKey> keys, string sort) => sort switch
        {
            "alpha" => keys.OrderBy(k => k.SimpleTitle).ThenBy(k => k.Kind).ThenBy(k => k.Id),
            "added" => keys.OrderByDescending(k => k.SortDate ?? DateTime.MinValue).ThenBy(k => k.SimpleTitle).ThenBy(k => k.Kind).ThenBy(k => k.Id),
            "random" => keys.OrderBy(k => k.ShuffleKey ?? 0L).ThenBy(k => k.Kind).ThenBy(k => k.Id),
            _ => keys.OrderByDescending(k => k.SortValue ?? -1m).ThenBy(k => k.SimpleTitle).ThenBy(k => k.Kind).ThenBy(k => k.Id),
        };

        // Page a merged movie+series browse result at the DB without pulling the whole filtered set
        // (two-phase, mirroring the MyBooks views-perf "band items" approach):
        //   1. UNION just the ordering keys (Kind/Id/SimpleTitle) across both tables and Skip/Take
        //      that — a cheap scalar set-operation. A stable secondary sort (Kind, Id) guarantees the
        //      page boundaries don't drift between fetches, so infinite scroll never dupes/skips.
        //   2. Materialize the full card DTOs for just the page's ids and restore the merged order.
        // pageSize <= 0 returns the whole merged set (back-compat).
        private static async Task<object> PageMergedAsync(IQueryable<Movie> mq, IQueryable<Series> sq, int page, int pageSize, string sort = "alpha", int seed = 0)
        {
            var keys = BuildCardKeys(mq, sq, sort, seed);

            if (pageSize <= 0)
            {
                var allMovies = await mq.Select(ToCardDto).ToListAsync();
                var allSeries = await sq.Select(ToSeriesCardDto).ToListAsync();
                var allMerged = SortCards(allMovies.Concat(allSeries), sort, seed);
                return new { movies = allMerged, totalCount = allMerged.Count, page = 1, pageSize = allMerged.Count };
            }
            if (page < 1) page = 1;
            // Count only on the first page — the client reads totalCount once and discards it on every
            // later page fetch. Here that COUNT is a full UNION of both table scans, so skipping it on
            // page > 1 saves the most expensive query of the request (-1 = "not computed").
            var totalCount = page == 1 ? await keys.CountAsync() : -1;

            var pageKeys = await OrderCardKeys(keys, sort)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .ToListAsync();

            var movieIds = pageKeys.Where(k => k.Kind == "movie").Select(k => k.Id).ToList();
            var seriesIds = pageKeys.Where(k => k.Kind == "series").Select(k => k.Id).ToList();
            var movieCards = movieIds.Count > 0
                ? await mq.Where(m => movieIds.Contains(m.id)).Select(ToCardDto).ToListAsync()
                : new List<MovieCardDto>();
            var seriesCards = seriesIds.Count > 0
                ? await sq.Where(s => seriesIds.Contains(s.Id)).Select(ToSeriesCardDto).ToListAsync()
                : new List<MovieCardDto>();

            var movieById = movieCards.ToDictionary(c => c.id);
            var seriesById = seriesCards.ToDictionary(c => c.id);
            var pageCards = pageKeys
                .Select(k => k.Kind == "movie"
                    ? (movieById.TryGetValue(k.Id, out var mc) ? mc : null)
                    : (seriesById.TryGetValue(k.Id, out var sc) ? sc : null))
                .Where(c => c != null)
                .ToList();

            return new { movies = pageCards, totalCount, page, pageSize };
        }

        // Empty merged result in the paginated envelope shape (for empty queries).
        private static object EmptyPage(int pageSize) =>
            new { movies = new List<MovieCardDto>(), totalCount = 0, page = 1, pageSize };

        // Approved (un-quarantined) MiscVideos as browse cards. They carry only an inferred rating
        // (a related misc inherits its parent's; a standalone one is judged on its own), gated the
        // same way every other title is. Poster (if any) is served from the separate /MiscImage
        // namespace; the card builds the poster URL off Kind="misc", not the shared id space.
        private async Task<List<MovieCardDto>> GetMiscCards()
        {
            int ageRestriction = await GetAgeRestrictionAsync();
            var raw = await movieDb.MiscVideos
                .Where(v => v.ReviewBatch == null)
                .Where(Web.RatingGate.MiscVisibleAtAge(movieDb, ageRestriction))
                .OrderBy(v => v.CollectionName ?? "")
                .ThenBy(v => v.SortOrder ?? int.MaxValue)
                .ThenBy(v => v.SimpleTitle ?? v.Title)
                .Select(v => new { v.Id, v.Title, v.SimpleTitle, v.Year, v.Description, v.Category, v.PlayableId, v.MpaaRatingInferred })
                .ToListAsync();
            return raw.Select(v => new MovieCardDto
            {
                id = v.Id,
                Kind = "misc",
                Title = v.Title,
                SimpleTitle = v.SimpleTitle,
                ReleaseDate = v.Year.HasValue ? new DateTime(v.Year.Value, 1, 1) : (DateTime?)null,
                Rating = v.MpaaRatingInferred,
                RatingEstimated = v.MpaaRatingInferred != null,
                Plot = v.Description,
                Category = v.Category,
                PlayableId = v.PlayableId,
            }).ToList();
        }

        // Misc-video cards for an explicit id set (the Rate page's misc bars). MiscVideo has its own id
        // space, so this is separate from GetMoviesByIds; same projection + age gate as GetMiscCards.
        [HttpPost("/API/GetMiscByIds")]
        public async Task<IActionResult> GetMiscByIds([FromBody] List<int> ids)
        {
            if (ids == null || ids.Count == 0)
                return Ok(new List<MovieCardDto>());

            int ageRestriction = await GetAgeRestrictionAsync();
            var raw = await movieDb.MiscVideos
                .Where(v => ids.Contains(v.Id) && v.ReviewBatch == null)
                .Where(Web.RatingGate.MiscVisibleAtAge(movieDb, ageRestriction))
                .Select(v => new { v.Id, v.Title, v.SimpleTitle, v.Year, v.Description, v.Category, v.PlayableId, v.MpaaRatingInferred })
                .ToListAsync();
            var cards = raw.Select(v => new MovieCardDto
            {
                id = v.Id,
                Kind = "misc",
                Title = v.Title,
                SimpleTitle = v.SimpleTitle,
                ReleaseDate = v.Year.HasValue ? new DateTime(v.Year.Value, 1, 1) : (DateTime?)null,
                Rating = v.MpaaRatingInferred,
                RatingEstimated = v.MpaaRatingInferred != null,
                Plot = v.Description,
                Category = v.Category,
                PlayableId = v.PlayableId,
            }).OrderBy(c => c.SimpleTitle ?? c.Title, StringComparer.OrdinalIgnoreCase).ToList();
            return Ok(cards);
        }

        // ── Unified search over movies + series (the frontend uses these instead of /odata/Movies) ──

        // All five share one shape: narrow the base queries by the mode's filter (ApplyBrowseFilter —
        // the same call /API/BrowseLetters makes), apply the Type scope, page the merged result under
        // the chosen sort. `seed` is only read by the random sort.
        [HttpGet("/API/BrowseTitle")]
        public async Task<IActionResult> BrowseTitle(string q, int page = 1, int pageSize = 60, string? types = null, string? sort = null, int seed = 0)
            => await BrowseFilteredAsync("title", q, page, pageSize, types, sort, seed);

        [HttpGet("/API/BrowseLetter")]
        public async Task<IActionResult> BrowseLetter(string letter, int page = 1, int pageSize = 60, string? types = null, string? sort = null, int seed = 0)
            => await BrowseFilteredAsync("letter", letter, page, pageSize, types, sort, seed);

        [HttpGet("/API/BrowseGenre")]
        public async Task<IActionResult> BrowseGenre(string genres, int page = 1, int pageSize = 60, string? types = null, string? sort = null, int seed = 0)
            => await BrowseFilteredAsync("genre", genres, page, pageSize, types, sort, seed);

        // All titles the model tagged as part of a franchise / shared universe (TagCategory.Franchise),
        // e.g. "mcu", "studio-ghibli". The franchise value is the model's normalized tag (lowercase);
        // the detail modal's franchise chips pass it through verbatim.
        [HttpGet("/API/BrowseFranchise")]
        public async Task<IActionResult> BrowseFranchise(string franchise, int page = 1, int pageSize = 60, string? types = null, string? sort = null, int seed = 0)
            => await BrowseFilteredAsync("franchise", franchise, page, pageSize, types, sort, seed);

        [HttpGet("/API/BrowsePerson")]
        public async Task<IActionResult> BrowsePerson(string q, int page = 1, int pageSize = 60, string? types = null, string? sort = null, int seed = 0)
            => await BrowseFilteredAsync("actor", q, page, pageSize, types, sort, seed);

        private async Task<IActionResult> BrowseFilteredAsync(string mode, string? value, int page, int pageSize, string? types, string? sort, int seed)
        {
            // An empty value is not "match everything" — the caller cleared the field, and the client
            // navigates back to the unfiltered browse rather than reading an unbounded page from here.
            if ((value ?? "").Trim().Length == 0) return Ok(EmptyPage(pageSize));
            var (mq, sq) = ApplyBrowseFilter(await GetBaseMovieQuery(), await GetBaseSeriesQuery(), mode, value);
            (mq, sq) = ApplyTypeScope(ParseTypeScope(types), mq, sq);
            return Ok(await PageMergedAsync(mq, sq, page, pageSize, NormalizeSort(sort), seed));
        }
    }
}
