using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Channels;
using MovieTheater.Db;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Channel management surface (streaming-plan.md §8). Unlike the viewer-facing
    /// <see cref="ChannelController"/>, this is gated on the CanEditMovies permission rather
    /// than on a password-verified streaming session — administering channels shouldn't
    /// require that the editor has set a streaming password.
    /// </summary>
    [Authorize]
    public class ChannelAdminController : Controller
    {
        private readonly MovieDb movieDb;

        public ChannelAdminController(MovieDb movieDb)
        {
            this.movieDb = movieDb;
        }

        private int? GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
        }

        private async Task<bool> IsCurrentUserEditor()
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue) return false;
            var setting = await movieDb.UserSettings
                .FirstOrDefaultAsync(s => s.UserID == userId.Value && s.SettingKey == "CanEditMovies");
            return setting != null && string.Equals(setting.SettingValue, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Lookup data the create/edit form needs: genres and MPA ratings.</summary>
        [HttpGet("/API/Channel/Admin/Meta")]
        public async Task<IActionResult> Meta()
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            var genres = await movieDb.Genres
                .OrderBy(g => g.Name)
                .Select(g => new { id = g.Id, name = g.Name })
                .ToListAsync();

            var ratingIds = await movieDb.RatingMaps
                .Select(rm => rm.MPARatingID)
                .Distinct()
                .OrderBy(id => id)
                .ToListAsync();
            var mpaNames = await movieDb.RatingMpas
                .ToDictionaryAsync(mpa => mpa.RatingID, mpa => mpa.MPAName);
            var ratings = ratingIds.Select(id => new
            {
                id,
                name = mpaNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : id.ToString(),
            }).ToList();

            return Json(new { genres, ratings });
        }

        /// <summary>Every channel (including disabled ones) with its filter expanded for editing.</summary>
        [HttpGet("/API/Channel/Admin/List")]
        public async Task<IActionResult> List()
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            var channels = await movieDb.Channels
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Id)
                .ToListAsync();

            var result = channels.Select(c =>
            {
                var f = ChannelFilter.Parse(c.FilterJson);
                return new
                {
                    id = c.Id,
                    name = c.Name,
                    description = c.Description,
                    sortOrder = c.SortOrder,
                    enabled = c.Enabled,
                    shuffleMode = c.ShuffleMode,
                    filter = new
                    {
                        genreIds = f.GenreIds,
                        genreMode = f.GenreMode,
                        yearMin = f.YearMin,
                        yearMax = f.YearMax,
                        maxMpaRatingId = f.MaxMpaRatingId,
                        unwatchedByUserId = f.UnwatchedByUserId,
                        excludeRemoveFromRandom = f.ExcludeRemoveFromRandom,
                    },
                };
            });

            return Json(result);
        }

        public class SaveChannelRequest
        {
            public int? Id { get; set; }
            public string? Name { get; set; }
            public string? Description { get; set; }
            public int SortOrder { get; set; }
            public bool Enabled { get; set; } = true;
            public string? ShuffleMode { get; set; }

            public List<int>? GenreIds { get; set; }
            public string? GenreMode { get; set; }
            public int? YearMin { get; set; }
            public int? YearMax { get; set; }
            public int? MaxMpaRatingId { get; set; }
            public int? UnwatchedByUserId { get; set; }
            public bool ExcludeRemoveFromRandom { get; set; } = true;
        }

        [HttpPost("/API/Channel/Admin/Save")]
        public async Task<IActionResult> Save([FromBody] SaveChannelRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            if (string.IsNullOrWhiteSpace(req.Name))
                return BadRequest(new { message = "Name is required." });

            var filter = new ChannelFilter
            {
                GenreIds = (req.GenreIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList(),
                GenreMode = string.Equals(req.GenreMode, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "any",
                YearMin = req.YearMin,
                YearMax = req.YearMax,
                MaxMpaRatingId = req.MaxMpaRatingId,
                UnwatchedByUserId = req.UnwatchedByUserId,
                ExcludeRemoveFromRandom = req.ExcludeRemoveFromRandom,
            };
            var shuffleMode = string.Equals(req.ShuffleMode, "ReleaseDate", StringComparison.OrdinalIgnoreCase)
                ? "ReleaseDate"
                : "SeededShuffle";

            Channel channel;
            if (req.Id is int id && id > 0)
            {
                channel = await movieDb.Channels.FirstOrDefaultAsync(c => c.Id == id);
                if (channel == null)
                    return NotFound(new { message = "Channel not found." });

                // The lineup is materialized ahead and never rewritten, so a filter/shuffle
                // change won't reach already-generated items. Drop the not-yet-aired tail so
                // the new rule takes effect going forward without disrupting what's airing now.
                var filterChanged = channel.FilterJson != filter.ToJson() || channel.ShuffleMode != shuffleMode;

                channel.Name = req.Name.Trim();
                channel.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
                channel.SortOrder = req.SortOrder;
                channel.Enabled = req.Enabled;
                channel.ShuffleMode = shuffleMode;
                channel.FilterJson = filter.ToJson();

                if (filterChanged)
                {
                    var now = DateTime.UtcNow;
                    var future = await movieDb.ChannelScheduleItems
                        .Where(i => i.ChannelId == channel.Id && i.StartUtc > now)
                        .ToListAsync();
                    if (future.Count > 0)
                        movieDb.ChannelScheduleItems.RemoveRange(future);
                }
            }
            else
            {
                channel = new Channel
                {
                    Name = req.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                    SortOrder = req.SortOrder,
                    Enabled = req.Enabled,
                    ShuffleMode = shuffleMode,
                    FilterJson = filter.ToJson(),
                    Seed = new Random().Next(1, int.MaxValue),
                    AnchorUtc = DateTime.UtcNow,
                };
                movieDb.Channels.Add(channel);
            }

            await movieDb.SaveChangesAsync();
            return Json(new { id = channel.Id });
        }

        public class DeleteChannelRequest
        {
            public int Id { get; set; }
        }

        [HttpPost("/API/Channel/Admin/Delete")]
        public async Task<IActionResult> Delete([FromBody] DeleteChannelRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            var channel = await movieDb.Channels.FirstOrDefaultAsync(c => c.Id == req.Id);
            if (channel == null)
                return NotFound(new { message = "Channel not found." });

            var items = await movieDb.ChannelScheduleItems
                .Where(i => i.ChannelId == channel.Id)
                .ToListAsync();
            if (items.Count > 0)
                movieDb.ChannelScheduleItems.RemoveRange(items);

            movieDb.Channels.Remove(channel);
            await movieDb.SaveChangesAsync();
            return Json(new { success = true });
        }
    }
}
