using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieTheater.Arcade;

namespace MovieTheater.Controllers
{
    public partial class ArcadeController
    {
        private static readonly TimeSpan GameGroupsTtlUnfiltered = TimeSpan.FromHours(6);
        private static readonly TimeSpan GameGroupsTtlFiltered = TimeSpan.FromMinutes(20);

        /// <summary>
        /// The grouped lobby (Arcade.ArcadeGameGroups): a page of groups — by system, genre or decade —
        /// each with its first cards, under the SAME filter vocabulary as <c>/API/Arcade/Games</c>, so a
        /// group's members are exactly the cards the flat lobby would page for that filter set. Two-phase:
        /// <c>groupsSkip/groupsTop</c> over the heads, <c>perGroupSkip/perGroupTop</c> within each;
        /// <c>singleGroupKey</c> = "more of this group". Cards are the lobby's own card DTO
        /// (<c>BuildGameCardsAsync</c>), so the switcher's views open the same modal the lobby does.
        /// </summary>
        [HttpGet("/API/Arcade/GameGroups")]
        public async Task<IActionResult> GameGroups(
            string groupBy = null, string system = null, string hideRegions = null, int? maxPlayers = null,
            string variant = null, string genre = null, string search = null, string ra = null, string sort = null,
            int groupsSkip = 0, int groupsTop = 0, int perGroupTop = 0, int perGroupSkip = 0, string singleGroupKey = null,
            CancellationToken ct = default)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            if (!host.IsConfigured) return StatusCode(501, new { message = "The arcade is not configured on this server." });

            var by = ArcadeGameGroups.NormalizeGroupBy(groupBy);
            var var_ = NormalizeVariant(variant);
            var hidden = ParseHideRegions(hideRegions);
            var selectedSystems = ParseSystems(system);
            var baseQ = await VisibleGamesAsync(userId.Value);
            var index = await CachedGameGroupIndexAsync(baseQ, selectedSystems, maxPlayers, genre, search, hidden, var_, ra, by, ct);

            IReadOnlyList<ArcadeGameGroups.Head> page;
            if (!string.IsNullOrWhiteSpace(singleGroupKey))
                page = index.Heads.Where(h => string.Equals(h.Key, singleGroupKey, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
            else
                page = index.Heads.Skip(Math.Max(0, groupsSkip)).Take(ArcadeGameGroups.CapGroupsTop(groupsTop)).ToList();

            var top = ArcadeGameGroups.CapPerGroupTop(perGroupTop);
            var skip = Math.Max(0, perGroupSkip);
            var bands = page.Select(h => (h, members: ArcadeGameGroups.Band(index, h.Key, sort, top, skip))).ToList();

            // One card build for the whole band: BuildGameCardsAsync returns cards in key order, so the
            // concatenated member list slices back into groups by count.
            var keys = bands.SelectMany(b => b.members).Select(m => (m.System, m.CollapseKey, m.Title)).ToList();
            var cards = keys.Count > 0 ? await BuildGameCardsAsync(baseQ, keys, null, ct) : new List<object>();
            var offset = 0;
            var groups = bands.Select(b =>
            {
                var items = cards.Skip(offset).Take(b.members.Count).ToList();
                offset += b.members.Count;
                return new { key = b.h.Key, label = b.h.Label, totalItems = b.h.Count, renderTotal = b.h.Count, items };
            }).ToList();

            return Json(new { totalGroups = index.Heads.Count, groups });
        }

        /// <summary>Letter → first group index over the grouped order (the grouped views' letter rail).</summary>
        [HttpGet("/API/Arcade/GameGroupLetters")]
        public async Task<IActionResult> GameGroupLetters(
            string groupBy = null, string system = null, string hideRegions = null, int? maxPlayers = null,
            string variant = null, string genre = null, string search = null, string ra = null,
            CancellationToken ct = default)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();
            if (!host.IsConfigured) return StatusCode(501, new { message = "The arcade is not configured on this server." });
            var by = ArcadeGameGroups.NormalizeGroupBy(groupBy);
            var baseQ = await VisibleGamesAsync(userId.Value);
            var index = await CachedGameGroupIndexAsync(baseQ, ParseSystems(system), maxPlayers, genre, search, ParseHideRegions(hideRegions), NormalizeVariant(variant), ra, by, ct);
            var letters = ArcadeGameGroups.GroupLetters(index.Heads, by).Select(l => new { letter = l.Letter, firstIndex = l.FirstIndex }).ToList();
            return Json(new { totalGroups = index.Heads.Count, letters });
        }

        /// <summary>
        /// The lobby's card aggregates (its <c>groupedQ</c>: one row per (System, CollapseKey) with the
        /// sort columns and the anchor's genre CSV) for a filter set, grouped once and cached per user +
        /// filters + mode. ~17k cards ≈ 2 MB; the site's cache is byte-budgeted so the entry states its size.
        ///
        /// <para>The entry is SHARED across viewers — see <see cref="ArcadeGameGroups.CacheKey"/> for why
        /// that is safe here and what would make it unsafe. It takes no user id for that reason.</para>
        /// </summary>
        private async Task<ArcadeGameGroups.GroupIndex> CachedGameGroupIndexAsync(
            IQueryable<Db.ArcadeGame> baseQ, List<string> systems, int? maxPlayers, string genre, string search,
            List<string> hideRegions, string var_, string ra, string by, CancellationToken ct = default)
        {
            var filtered = ArcadeGameGroups.IsFiltered(systems, maxPlayers, genre, search, hideRegions, var_, ra);
            var key = ArcadeGameGroups.CacheKey(ArcadeGameGroups.FilterSig(systems, maxPlayers, genre, search, hideRegions, var_, ra), by);
            var cached = await cache.GetOrCreateAsync(key, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = filtered ? GameGroupsTtlFiltered : GameGroupsTtlUnfiltered;
                var matchQ = ApplyCardFilters(baseQ, systems, maxPlayers, genre, search, hideRegions, var_, ra);
                var index = await ArcadeGameGroups.LoadIndexAsync(matchQ, by, ct);
                entry.Size = index.ApproxBytes;
                return index;
            });
            return cached ?? new ArcadeGameGroups.GroupIndex { By = by };
        }
    }
}
