using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Db;
using MovieTheater.Web;

namespace MovieTheater.Controllers
{
    public partial class APIController
    {
        /// <summary>The wire shape of one group in a band — the catalog package's CardGroup, server side.</summary>
        public sealed class BrowseGroupDto
        {
            public string Key { get; set; } = "";
            public string Label { get; set; } = "";
            public int TotalItems { get; set; }
            public int RenderTotal { get; set; }
            public List<MovieCardDto> Items { get; set; } = new();
        }

        private static readonly TimeSpan GroupIndexTtlUnfiltered = TimeSpan.FromHours(6);
        private static readonly TimeSpan GroupIndexTtlFiltered = TimeSpan.FromMinutes(20);
        private const int HydrateChunk = 500;

        // The card the grouped views render: everything ToCardDto carries except the long text (plot, cast).
        private static readonly System.Linq.Expressions.Expression<Func<Movie, MovieCardDto>> ToSlimCardDto = m => new MovieCardDto
        {
            id = m.id,
            Kind = "movie",
            Title = m.Title,
            SimpleTitle = m.SimpleTitle,
            ReleaseDate = m.ReleaseDate ?? m.ImdbReleaseDate,
            Rating = m.MpaaRating ?? m.Rating ?? m.MpaaRatingInferred,
            RatingEstimated = m.MpaaRating == null && m.Rating == null && m.MpaaRatingInferred != null,
            Runtime = m.Runtime,
            imdbRating = m.ImdbRatingScraped ?? m.imdbRating,
            RtTomatometer = m.RtTomatometer,
            RtPopcornmeter = m.RtPopcornmeter,
            PosterVersion = m.PosterDetails != null ? m.PosterDetails.PosterVersion : 0,
            UploadedDate = m.UploadedDate,
        };

        private static readonly System.Linq.Expressions.Expression<Func<Series, MovieCardDto>> ToSlimSeriesCardDto = s => new MovieCardDto
        {
            id = s.Id,
            Kind = "series",
            Title = s.Title,
            SimpleTitle = s.SimpleTitle,
            ReleaseDate = s.ReleaseDate ?? s.ImdbReleaseDate,
            Rating = s.MpaaRating ?? s.Rating ?? s.MpaaRatingInferred,
            RatingEstimated = s.MpaaRating == null && s.Rating == null && s.MpaaRatingInferred != null,
            imdbRating = s.ImdbRatingScraped ?? s.imdbRating,
            RtTomatometer = s.RtTomatometer,
            RtPopcornmeter = s.RtPopcornmeter,
            PosterVersion = s.PosterDetails != null ? s.PosterDetails.PosterVersion : 0,
            UploadedDate = s.UploadedDate,
        };

        /// <summary>
        /// The grouped movie/TV browse (Web.BrowseGroups): a page of groups with each group's first
        /// members. Same scope vocabulary as the flat endpoints — <c>types</c> (the Type scope),
        /// <c>mode</c>/<c>value</c> (the same filter <c>ApplyBrowseFilter</c> gives <c>/API/Browse*</c>),
        /// <c>sort</c>/<c>seed</c> (the order INSIDE each group) — plus the two-phase paging:
        /// <c>groupsSkip/groupsTop</c> over the heads, <c>perGroupSkip/perGroupTop</c> within each.
        /// <c>singleGroupKey</c> serves "more of this group" (a band of exactly that group).
        /// </summary>
        [HttpGet("/API/BrowseGroups")]
        public async Task<IActionResult> BrowseGroupsAsync(
            string? groupBy = null, string? types = null, string? mode = null, string? value = null,
            string? sort = null, int seed = 0,
            int groupsSkip = 0, int groupsTop = 0, int perGroupTop = 0, int perGroupSkip = 0,
            string? singleGroupKey = null, [FromQuery] BrowseFilterQuery? fq = null, CancellationToken ct = default)
        {
            var by = BrowseGroups.NormalizeGroupBy(groupBy);
            var scope = await ResolveGroupScopeAsync(types, mode, value, BrowseFilter.From(fq));
            if (scope == null) return BadRequest(new { Message = $"Unknown title type '{types}'" });
            var index = await CachedGroupIndexAsync(scope, by, ct);
            var heads = index.Heads;

            IReadOnlyList<BrowseGroups.Head> page;
            if (!string.IsNullOrWhiteSpace(singleGroupKey))
                page = heads.Where(h => string.Equals(h.Key, singleGroupKey, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
            else
                page = heads.Skip(Math.Max(0, groupsSkip)).Take(BrowseGroups.CapGroupsTop(by, groupsTop)).ToList();

            var band = BrowseGroups.Band(index, page.Select(h => h.Key).ToList(), NormalizeSort(sort), seed,
                BrowseGroups.CapPerGroupTop(perGroupTop), Math.Max(0, perGroupSkip));

            // Hydrate the band's members, then restore each group's order. The ids came out of the GATED
            // index, so the plain tables are read here — re-running the age gate's three correlated
            // lookups per row for up to 960 cards was most of a band's cost. Slim cards: the grouped
            // views render title/year/poster/ratings, never plot or cast, and that text was ~70% of
            // the payload. Chunked well under SQL Server's parameter cap.
            var movieIds = band.Members.Values.SelectMany(m => m).Where(m => m.Kind == "movie").Select(m => m.Id).Distinct().ToList();
            var seriesIds = band.Members.Values.SelectMany(m => m).Where(m => m.Kind == "series").Select(m => m.Id).Distinct().ToList();
            var movieById = new Dictionary<int, MovieCardDto>();
            var seriesById = new Dictionary<int, MovieCardDto>();
            foreach (var chunk in movieIds.Chunk(HydrateChunk))
                foreach (var c in await movieDb.Movies.Where(m => chunk.Contains(m.id)).Select(ToSlimCardDto).ToListAsync(ct)) movieById[c.id] = c;
            foreach (var chunk in seriesIds.Chunk(HydrateChunk))
                foreach (var c in await movieDb.Series.Where(x => chunk.Contains(x.Id)).Select(ToSlimSeriesCardDto).ToListAsync(ct)) seriesById[c.id] = c;
            var miscById = scope.MiscCards.ToDictionary(c => c.id);

            var groups = page.Select(h => new BrowseGroupDto
            {
                Key = h.Key,
                Label = h.Label,
                TotalItems = h.Count,
                RenderTotal = h.Count,
                Items = (band.Members.TryGetValue(h.Key, out var members) ? members : new List<BrowseGroups.Member>())
                    .Select(m => m.Kind == "movie" ? (movieById.TryGetValue(m.Id, out var mc) ? mc : null)
                        : m.Kind == "series" ? (seriesById.TryGetValue(m.Id, out var sc) ? sc : null)
                        : (miscById.TryGetValue(m.Id, out var xc) ? xc : null))
                    .Where(c => c != null).Select(c => c!).ToList(),
            }).ToList();

            return Ok(new { totalGroups = heads.Count, groups });
        }

        /// <summary>Letter → first group index over the grouped order, for the grouped views' letter rail.</summary>
        [HttpGet("/API/BrowseGroupLetters")]
        public async Task<IActionResult> BrowseGroupLettersAsync(string? groupBy = null, string? types = null, string? mode = null, string? value = null, [FromQuery] BrowseFilterQuery? fq = null, CancellationToken ct = default)
        {
            var by = BrowseGroups.NormalizeGroupBy(groupBy);
            var scope = await ResolveGroupScopeAsync(types, mode, value, BrowseFilter.From(fq));
            if (scope == null) return BadRequest(new { Message = $"Unknown title type '{types}'" });
            var index = await CachedGroupIndexAsync(scope, by, ct);
            var letters = BrowseGroups.GroupLetters(index.Heads, by).Select(l => new { letter = l.Letter, firstIndex = l.FirstIndex }).ToList();
            return Ok(new { totalGroups = index.Heads.Count, letters });
        }

        private sealed class GroupScope
        {
            public IQueryable<Movie> Movies { get; init; } = default!;
            public IQueryable<Series> Series { get; init; } = default!;
            public IReadOnlyList<MovieCardDto> MiscCards { get; init; } = Array.Empty<MovieCardDto>();
            public IReadOnlyList<BrowseGroups.MiscLight> Misc { get; init; } = Array.Empty<BrowseGroups.MiscLight>();
            public string CacheKey { get; init; } = "";
            public bool Filtered { get; init; }
        }

        /// <summary>The same scope the flat endpoints page: quarantine + series exclusion + age gate (the base queries), the filter mode, the Type scope; misc joins when the scope includes it.</summary>
        /// <summary>
        /// The scope a grouped/lettered request reads: the legacy one-criterion browse (mode/value) AND the
        /// facet rail's combinable filter (R9 S2) compose — a link from the old world still works, a rail
        /// selection narrows it further. Misc rides along only when nothing narrows the set.
        /// </summary>
        private async Task<GroupScope?> ResolveGroupScopeAsync(string? types, string? mode, string? value, BrowseFilter? filter = null)
        {
            filter ??= BrowseFilter.Empty;
            var typeScope = ParseTypeScope(types);
            if (!string.IsNullOrWhiteSpace(types) && typeScope.Count == 0) return null;
            var (mq, sq) = ApplyBrowseFilter(await GetBaseMovieQuery(), await GetBaseSeriesQuery(), mode, value);
            (mq, sq) = ApplyTypeScope(typeScope, mq, sq);
            (mq, sq) = BrowseFilter.Apply(movieDb, mq, sq, filter, GetCurrentUserId());
            var v = (value ?? "").Trim();
            var filtered = v.Length > 0 || !filter.IsEmpty;
            // Misc has no genre/cast/title-search presence (see ApplyTypeScope) — it joins only the plain Type-scope browse.
            var wantMisc = typeScope.Contains(NormalizedTitleType.Misc) && !filtered;
            var miscCards = wantMisc ? await GetMiscCards() : new List<MovieCardDto>();
            var age = await GetAgeRestrictionAsync();
            var user = GetCurrentUserId()?.ToString() ?? "anon";
            var cacheKey = $"browse:groups:{user}:{age}:{string.Join(",", typeScope.OrderBy(t => t))}:{(mode ?? "").Trim().ToLowerInvariant()}:{v.ToLowerInvariant()}:{filter.Sig}";
            return new GroupScope
            {
                Movies = mq, Series = sq, MiscCards = miscCards,
                Misc = miscCards.Select(c => new BrowseGroups.MiscLight(c.id, c.SimpleTitle, c.Title, c.ReleaseDate?.Year)).ToList(),
                CacheKey = cacheKey, Filtered = filtered,
            };
        }

        /// <summary>
        /// The scope's light index, keyed by user facts + filter + group mode: one gated pass per scope,
        /// then heads, the letter rail and every band come from memory. Shared by both actions, so the
        /// rail warms the bands.
        /// </summary>
        private async Task<BrowseGroups.GroupIndex> CachedGroupIndexAsync(GroupScope scope, string by, CancellationToken ct)
        {
            var key = $"{scope.CacheKey}:{by}";
            var cached = await memoryCache.GetOrCreateAsync(key, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = scope.Filtered ? GroupIndexTtlFiltered : GroupIndexTtlUnfiltered;
                var index = await BrowseGroups.BuildIndexAsync(movieDb, scope.Movies, scope.Series, scope.Misc, by, ct);
                // The site's cache is byte-budgeted (Startup: 200 MB SizeLimit) — every entry must state a size.
                entry.Size = index.ApproxBytes;
                return index;
            });
            return cached ?? new BrowseGroups.GroupIndex { By = by };
        }
    }
}
