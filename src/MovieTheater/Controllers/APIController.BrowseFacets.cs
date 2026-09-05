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
    /// <summary>
    /// The facet rail's endpoints for Movies/TV (R9 S2): the combinable flat browse, the facet counts
    /// over a scope, and the people typeahead. The same <see cref="BrowseFilterQuery"/> rides
    /// <c>/API/BrowseGroups</c>, <c>/API/BrowseGroupLetters</c> and <c>/API/BrowseLetters</c>, so every
    /// view and the strip see one filtered set.
    /// </summary>
    public partial class APIController
    {
        private static readonly TimeSpan FacetsTtlUnfiltered = TimeSpan.FromHours(6);
        private static readonly TimeSpan FacetsTtlFiltered = TimeSpan.FromMinutes(20);

        /// <summary>
        /// GET /API/Browse — the flat, paged browse over the combinable filter. Misc videos ride along
        /// only when the scope names them and no facet narrows the set (they carry no genres, credits,
        /// tags or certificates to filter on) — the same rule the type browse applies.
        /// </summary>
        [HttpGet("/API/Browse")]
        public async Task<IActionResult> BrowseAsync([FromQuery] BrowseFilterQuery? fq = null, string? types = null, string? sort = null, int seed = 0, int page = 1, int pageSize = 60, [FromQuery(Name = "for")] string? forUser = null, CancellationToken ct = default)
        {
            var typeScope = ParseTypeScope(types);
            if (!string.IsNullOrWhiteSpace(types) && typeScope.Count == 0) return BadRequest(new { Message = $"Unknown title type '{types}'" });
            var filter = BrowseFilter.From(fq);
            var (mq, sq) = ApplyTypeScope(typeScope, await GetBaseMovieQuery(ct), await GetBaseSeriesQuery(ct));
            // `my=` reads the LIST OWNER's rows: the caller, or the friend `for=<username>` names.
            (mq, sq) = BrowseFilter.Apply(movieDb, mq, sq, filter, await ResolveListOwnerAsync(forUser, ct));
            var s = NormalizeSort(sort);
            var wantMisc = typeScope.Contains(NormalizedTitleType.Misc) && !filter.HasFacets;
            if (!wantMisc) return Ok(await PageMergedAsync(mq, sq, page, pageSize, s, seed, ct));

            // Misc is a small, in-memory list; a text search still applies to it.
            var misc = await GetMiscCards(ct);
            if (filter.Q.Length > 0)
                misc = misc.Where(c => (c.SimpleTitle ?? "").Contains(filter.Q, StringComparison.OrdinalIgnoreCase) || (c.Title ?? "").Contains(filter.Q, StringComparison.OrdinalIgnoreCase)).ToList();
            var onlyMisc = typeScope.All(t => t == NormalizedTitleType.Misc);
            if (onlyMisc) return Ok(PageCards(SortCards(misc, s, seed), page, pageSize));
            var cards = new List<MovieCardDto>();
            cards.AddRange(await mq.Select(ToCardDto).ToListAsync(ct));
            cards.AddRange(await sq.Select(ToSeriesCardDto).ToListAsync(ct));
            cards.AddRange(misc);
            return Ok(PageCards(SortCards(cards, s, seed), page, pageSize));
        }

        /// <summary>
        /// GET /API/BrowseFacets — the option lists with counts over the SCOPE (types + text), cached per
        /// viewer facts like the group index: the counts describe what the rail can reach, not the current
        /// selection (the Long Box rule), so they are one cached pass rather than a recount per click.
        /// </summary>
        [HttpGet("/API/BrowseFacets")]
        public async Task<IActionResult> BrowseFacetsAsync(string? types = null, string? q = null, CancellationToken ct = default)
        {
            var typeScope = ParseTypeScope(types);
            if (!string.IsNullOrWhiteSpace(types) && typeScope.Count == 0) return BadRequest(new { Message = $"Unknown title type '{types}'" });
            var text = (q ?? "").Trim();
            var age = await GetAgeRestrictionAsync(ct);
            // Not user-keyed: the counts pass a null user id to BrowseFilter.Apply, so nothing personal
            // can reach them and one pass per (age, scope, text) serves everyone — and can be warmed.
            var key = BrowseCacheKeys.Facets(age, typeScope, text);
            var counts = await memoryCache.GetOrCreateAsync(key, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = text.Length > 0 ? FacetsTtlFiltered : FacetsTtlUnfiltered;
                var (mq, sq) = ApplyTypeScope(typeScope, await GetBaseMovieQuery(ct), await GetBaseSeriesQuery(ct));
                (mq, sq) = BrowseFilter.Apply(movieDb, mq, sq, new BrowseFilter { Q = text }, null);
                var miscCount = typeScope.Contains(NormalizedTitleType.Misc) ? (await GetMiscCards(ct)).Count : 0;
                var c = await BrowseFilter.CountAsync(movieDb, mq, sq, miscCount, ct);
                entry.Size = c.ApproxBytes;
                return c;
            });
            return Ok(counts);
        }

        /// <summary>
        /// GET /API/BrowsePeople?q= — the people facet's typeahead: credited names matching the text, with
        /// how many titles each stands in, most-credited first. The rail's dynamic long tail.
        /// </summary>
        [HttpGet("/API/BrowsePeople")]
        public async Task<IActionResult> BrowsePeopleAsync(string? q = null, int top = 20, CancellationToken ct = default)
        {
            var text = (q ?? "").Trim();
            top = Math.Clamp(top, 1, 50);
            if (text.Length < 2) return Ok(new { items = Array.Empty<object>(), total = 0 });
            var mq = await GetBaseMovieQuery(ct);
            var sq = await GetBaseSeriesQuery(ct);
            var movieIds = mq.Select(m => m.id);
            var seriesIds = sq.Select(s => s.Id);
            var movieHits = await movieDb.MovieCredits.Where(c => c.Person.DisplayName.Contains(text) && movieIds.Contains(c.MovieID))
                .Select(c => new { c.Person.DisplayName, c.MovieID }).Distinct()
                .GroupBy(c => c.DisplayName).Select(g => new { Name = g.Key, C = g.Count() }).ToListAsync(ct);
            var seriesHits = await movieDb.SeriesCredits.Where(c => c.Person.DisplayName.Contains(text) && seriesIds.Contains(c.SeriesId))
                .Select(c => new { c.Person.DisplayName, c.SeriesId }).Distinct()
                .GroupBy(c => c.DisplayName).Select(g => new { Name = g.Key, C = g.Count() }).ToListAsync(ct);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in movieHits) counts[h.Name] = counts.GetValueOrDefault(h.Name) + h.C;
            foreach (var h in seriesHits) counts[h.Name] = counts.GetValueOrDefault(h.Name) + h.C;
            var ordered = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            return Ok(new { items = ordered.Take(top).Select(kv => new { value = kv.Key, label = kv.Key, count = kv.Value }), total = ordered.Count });
        }
    }
}
