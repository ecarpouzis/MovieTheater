using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Photos;

namespace MovieTheater.Controllers
{
    public partial class PhotosController
    {
        private const int BrowseDefaultTop = 48;
        private const int BrowseMaxTop = 400;
        private const int GroupsDefaultTop = 20;
        private const int GroupsMaxTop = 50;
        private const int PerGroupDefaultTop = 24;
        private const int PerGroupMaxTop = 200;

        /// <summary>
        /// The timeline's rows with its four exclusions (shelf, missing, hidden-unless-admin-asked,
        /// dupe-collapsed, quarantined batches) — the ONE predicate the timeline, its year rail, the
        /// offset browse and the grouped browse share, so none of them can promise a photo another
        /// refuses to show. <paramref name="includeHidden"/> is the already-resolved admin opt-in.
        /// </summary>
        private async Task<IQueryable<PhotoAsset>> TimelineQueryAsync(bool includeHidden)
        {
            var query = TimelineShelf(movieDb.PhotoAssets).Where(a => a.MissingSinceUtc == null);
            if (!includeHidden) query = query.Where(a => !a.Hidden);
            var collapsed = PhotoDupeMasters.CollapsedAssetIds(movieDb);
            query = query.Where(a => !collapsed.Contains(a.Id));
            var quarantine = await QuarantineAsync(CurationStore);
            if (quarantine.Applied.Count > 0)
            {
                var pending = quarantine.Applied;
                query = query.Where(a => a.IngestBatch == null || !pending.Contains(a.IngestBatch));
            }
            return query;
        }

        /// <summary>
        /// The timeline as an OFFSET-paged list — what the catalog package's flat views (Grid / Wall /
        /// List) and their letter-free pager need: random access by <c>skip/top</c> over the dated
        /// photographs, newest first with the Id tie-break, optionally narrowed to a year or a month.
        /// The keyset <c>/API/Photos/Timeline</c> stays the timeline route's engine; this is the same
        /// predicate under a different pager. <c>total</c> is counted only on the first page.
        /// </summary>
        [HttpGet("/API/Photos/Browse")]
        public async Task<IActionResult> Browse(int skip = 0, int top = BrowseDefaultTop, int? year = null, int? month = null, bool includeHidden = false)
        {
            top = Math.Clamp(top, 1, BrowseMaxTop);
            skip = Math.Max(0, skip);
            includeHidden = ShowHidden(includeHidden);
            var query = (await TimelineQueryAsync(includeHidden)).Where(a => a.TakenAt != null);
            if (year is int y) query = query.Where(a => a.TakenAt!.Value.Year == y);
            if (month is int m && year != null) query = query.Where(a => a.TakenAt!.Value.Month == m);
            var total = skip == 0 ? await query.CountAsync() : -1;
            var rows = await query.OrderByDescending(a => a.TakenAt).ThenByDescending(a => a.Id).Skip(skip).Take(top).ToListAsync();
            var userId = GetCurrentUserId() ?? 0;
            var badges = await BadgesAsync(rows);
            return Json(new
            {
                items = rows.Select(a => Card(a, userId, badges)).ToList(),
                total,
                skip,
                top,
                includeHidden,
                dataPlane = DataPlaneConfigured,
            });
        }

        /// <summary>
        /// The grouped photo browse for the catalog package's Extended / Shelves / Newspaper views:
        /// <c>groupBy</c> = year | month | album | folder. Two-phase like the other sections —
        /// <c>groupsSkip/groupsTop</c> over the heads, <c>perGroupSkip/perGroupTop</c> within each,
        /// <c>singleGroupKey</c> for "more of this group". Year and month groups honour the timeline's
        /// exclusions; album groups are the family albums' curated entries (hidden/missing assets
        /// dropped); folder groups are the top-level folders of the tree the folder view shows.
        /// </summary>
        [HttpGet("/API/Photos/BrowseGroups")]
        public async Task<IActionResult> BrowseGroups(string? groupBy = null, int groupsSkip = 0, int groupsTop = 0, int perGroupTop = 0, int perGroupSkip = 0,
            string? singleGroupKey = null, bool includeHidden = false)
        {
            includeHidden = ShowHidden(includeHidden);
            var by = (groupBy ?? "").Trim().ToLowerInvariant() switch { "month" => "month", "album" => "album", "folder" => "folder", _ => "year" };
            groupsTop = groupsTop <= 0 ? GroupsDefaultTop : Math.Min(groupsTop, GroupsMaxTop);
            perGroupTop = perGroupTop <= 0 ? PerGroupDefaultTop : Math.Min(perGroupTop, PerGroupMaxTop);
            perGroupSkip = Math.Max(0, perGroupSkip);
            groupsSkip = Math.Max(0, groupsSkip);

            var timeline = await TimelineQueryAsync(includeHidden);
            IQueryable<PhotoAsset> tree = movieDb.PhotoAssets.Where(a => a.MissingSinceUtc == null);
            if (!includeHidden) tree = tree.Where(a => !a.Hidden);

            // ── Heads ──
            var heads = new List<(string Key, string Label, int Count, int AlbumId)>();
            switch (by)
            {
                case "month":
                {
                    var months = await timeline.Where(a => a.TakenAt != null)
                        .GroupBy(a => new { a.TakenAt!.Value.Year, a.TakenAt!.Value.Month })
                        .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
                        .OrderByDescending(g => g.Year).ThenByDescending(g => g.Month).ToListAsync();
                    heads.AddRange(months.Select(m => ($"{m.Year:D4}-{m.Month:D2}", $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m.Month)} {m.Year}", m.Count, 0)));
                    break;
                }
                case "album":
                {
                    var albums = await movieDb.PhotoAlbums.Where(a => a.Shelf == PhotoShelf.Timeline)
                        .OrderByDescending(a => a.ArtistName != null ? 1 : 0).ThenBy(a => a.SortOrder).ThenByDescending(a => a.CreatedUtc)
                        .Select(a => new { a.Id, a.Slug, a.Title, Count = a.Entries.Count(e => e.PhotoAsset.MissingSinceUtc == null && (includeHidden || !e.PhotoAsset.Hidden)) })
                        .ToListAsync();
                    heads.AddRange(albums.Select(a => (a.Slug, a.Title, a.Count, a.Id)));
                    break;
                }
                case "folder":
                {
                    var folders = await tree.Where(a => a.Path.Contains("/"))
                        .Select(a => a.Path.Substring(0, a.Path.IndexOf("/")))
                        .GroupBy(name => name).Select(g => new { Name = g.Key, Count = g.Count() })
                        .OrderBy(f => f.Name).ToListAsync();
                    heads.AddRange(folders.Select(f => (f.Name, f.Name, f.Count, 0)));
                    break;
                }
                default:
                {
                    var years = await timeline.Where(a => a.TakenAt != null)
                        .GroupBy(a => a.TakenAt!.Value.Year)
                        .Select(g => new { Year = g.Key, Count = g.Count() })
                        .OrderByDescending(g => g.Year).ToListAsync();
                    heads.AddRange(years.Select(y => (y.Year.ToString(), y.Year.ToString(), y.Count, 0)));
                    break;
                }
            }

            var page = !string.IsNullOrWhiteSpace(singleGroupKey)
                ? heads.Where(h => string.Equals(h.Key, singleGroupKey, StringComparison.OrdinalIgnoreCase)).Take(1).ToList()
                : heads.Skip(groupsSkip).Take(groupsTop).ToList();

            // ── Bands: one small index-backed query per group, then one badge pass over everything ──
            var members = new List<(string Key, List<PhotoAsset> Rows)>();
            foreach (var h in page)
            {
                List<PhotoAsset> rows;
                switch (by)
                {
                    case "month":
                    {
                        var parts = h.Key.Split('-');
                        var y = int.Parse(parts[0]); var m = int.Parse(parts[1]);
                        rows = await timeline.Where(a => a.TakenAt != null && a.TakenAt.Value.Year == y && a.TakenAt.Value.Month == m)
                            .OrderByDescending(a => a.TakenAt).ThenByDescending(a => a.Id).Skip(perGroupSkip).Take(perGroupTop).ToListAsync();
                        break;
                    }
                    case "album":
                    {
                        var albumId = h.AlbumId;
                        rows = await movieDb.PhotoAlbumEntries.Where(e => e.PhotoAlbumId == albumId)
                            .OrderBy(e => e.SortOrder).ThenBy(e => e.Id)
                            .Select(e => e.PhotoAsset)
                            .Where(a => a.MissingSinceUtc == null && (includeHidden || !a.Hidden))
                            .Skip(perGroupSkip).Take(perGroupTop).ToListAsync();
                        break;
                    }
                    case "folder":
                    {
                        var prefix = h.Key + "/";
                        rows = await tree.Where(a => a.Path.StartsWith(prefix))
                            .OrderBy(a => a.Path).ThenBy(a => a.Id).Skip(perGroupSkip).Take(perGroupTop).ToListAsync();
                        break;
                    }
                    default:
                    {
                        var y = int.Parse(h.Key);
                        rows = await timeline.Where(a => a.TakenAt != null && a.TakenAt.Value.Year == y)
                            .OrderByDescending(a => a.TakenAt).ThenByDescending(a => a.Id).Skip(perGroupSkip).Take(perGroupTop).ToListAsync();
                        break;
                    }
                }
                members.Add((h.Key, rows));
            }
            var userId = GetCurrentUserId() ?? 0;
            var badges = await BadgesAsync(members.SelectMany(m => m.Rows).ToList());
            var groups = page.Select(h => new
            {
                key = h.Key,
                label = h.Label,
                totalItems = h.Count,
                renderTotal = h.Count,
                items = members.First(m => m.Key == h.Key).Rows.Select(a => Card(a, userId, badges)).ToList(),
            }).ToList();
            return Json(new { totalGroups = heads.Count, groups, includeHidden, dataPlane = DataPlaneConfigured });
        }
    }
}
