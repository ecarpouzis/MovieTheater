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

        // The lineup-ordering strategies the engine understands (ChannelScheduleService.EffectiveStrategy).
        private static readonly string[] ScheduleStrategies =
            { "SeededShuffle", "WeightedShuffle", "ReleaseDate", "NewestFirst", "Marathon", "EpisodeRoundRobin" };
        private static readonly HashSet<string> ValidStrategies =
            new(ScheduleStrategies, StringComparer.OrdinalIgnoreCase);

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

        /// <summary>Lookup data the create/edit form needs: genres, MPA ratings, strategies, content
        /// kinds, credit roles, and the tag vocabulary (per category).</summary>
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

            // The tag picker offers the values actually present in the library, per category, most-used
            // first (capped) — reflects real discovery tags, not just the seed list.
            var tagVocab = (await movieDb.TitleTags
                    .GroupBy(t => new { t.Category, t.Value })
                    .Select(g => new { g.Key.Category, g.Key.Value, n = g.Count() })
                    .ToListAsync())
                .GroupBy(x => x.Category)
                .ToDictionary(
                    g => g.Key.ToString(),
                    g => g.OrderByDescending(x => x.n).Select(x => x.Value).Take(120).ToList());

            return Json(new
            {
                genres,
                ratings,
                strategies = ScheduleStrategies,
                kinds = new[] { "Movies", "Series", "Misc" },
                creditRoles = Enum.GetNames(typeof(CreditRole)),
                tagCategories = Enum.GetNames(typeof(TagCategory)),
                tagVocab,
            });
        }

        /// <summary>Person typeahead for the credits picker — DisplayName contains the query, shorter
        /// (closer) names first.</summary>
        [HttpGet("/API/Channel/Admin/People")]
        public async Task<IActionResult> People(string q)
        {
            if (!await IsCurrentUserEditor()) return Forbid();
            var term = (q ?? "").Trim();
            if (term.Length < 2) return Json(Array.Empty<object>());

            var people = await movieDb.People
                .Where(p => p.DisplayName != null && p.DisplayName.Contains(term))
                .OrderBy(p => p.DisplayName!.Length)
                .ThenBy(p => p.DisplayName)
                .Take(20)
                .Select(p => new { id = p.Id, name = p.DisplayName })
                .ToListAsync();
            return Json(people);
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

            var filters = channels.ToDictionary(c => c.Id, c => ChannelFilter.Parse(c.FilterJson));

            // Resolve every credited person's name once so the form can show pre-selected people.
            var personIds = filters.Values.SelectMany(f => f.Credits).SelectMany(cr => cr.PersonIds).Distinct().ToList();
            var personNames = personIds.Count == 0
                ? new Dictionary<int, string>()
                : await movieDb.People.Where(p => personIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p.DisplayName ?? $"#{p.Id}");

            var result = channels.Select(c =>
            {
                var f = filters[c.Id];
                return new
                {
                    id = c.Id,
                    name = c.Name,
                    description = c.Description,
                    sortOrder = c.SortOrder,
                    enabled = c.Enabled,
                    category = c.Category,
                    logoPath = c.LogoPath,
                    catalogKey = c.CatalogKey,
                    scheduleStrategy = c.ScheduleStrategy ?? (string.Equals(c.ShuffleMode, "ReleaseDate", StringComparison.OrdinalIgnoreCase) ? "ReleaseDate" : "SeededShuffle"),
                    seasonStartMonth = c.SeasonStartMonth,
                    seasonStartDay = c.SeasonStartDay,
                    seasonEndMonth = c.SeasonEndMonth,
                    seasonEndDay = c.SeasonEndDay,
                    filter = new
                    {
                        genreIds = f.GenreIds,
                        genreMode = f.GenreMode,
                        yearMin = f.YearMin,
                        yearMax = f.YearMax,
                        maxMpaRatingId = f.MaxMpaRatingId,
                        excludeAdult = f.ExcludeAdult,
                        unwatchedByUserId = f.UnwatchedByUserId,
                        excludeRemoveFromRandom = f.ExcludeRemoveFromRandom,
                        kinds = KindsToList(f.Kinds),
                        pathContains = f.PathContains,
                        languages = f.Languages,
                        excludeLanguages = f.ExcludeLanguages,
                        minViewers = f.MinViewers,
                        cultClassic = f.CultClassic,
                        surrealism = f.Surrealism,
                        intensity = f.Intensity,
                        novelty = f.Novelty,
                        rewatchability = f.Rewatchability,
                        energy = f.Energy,
                        tags = f.Tags.Select(t => new { category = t.Category.ToString(), values = t.Values, mode = t.Mode, negate = t.Negate }),
                        credits = f.Credits.Select(cr => new
                        {
                            role = cr.Role?.ToString(),
                            personIds = cr.PersonIds,
                            people = cr.PersonIds.Select(pid => new { id = pid, name = personNames.TryGetValue(pid, out var nm) ? nm : $"#{pid}" }),
                        }),
                    },
                };
            });

            return Json(result);
        }

        public class SaveRange
        {
            public double? Min { get; set; }
            public double? Max { get; set; }
        }

        public class SaveTagRule
        {
            public string? Category { get; set; }
            public List<string>? Values { get; set; }
            public string? Mode { get; set; }
            public bool Negate { get; set; }
        }

        public class SaveCreditRule
        {
            public List<int>? PersonIds { get; set; }
            public string? Role { get; set; }
        }

        public class SaveChannelRequest
        {
            public int? Id { get; set; }
            public string? Name { get; set; }
            public string? Description { get; set; }
            public int SortOrder { get; set; }
            public bool Enabled { get; set; } = true;
            public string? Category { get; set; }
            public string? ScheduleStrategy { get; set; }

            public int? SeasonStartMonth { get; set; }
            public int? SeasonStartDay { get; set; }
            public int? SeasonEndMonth { get; set; }
            public int? SeasonEndDay { get; set; }

            // ── Filter facets ──
            public List<int>? GenreIds { get; set; }
            public string? GenreMode { get; set; }
            public int? YearMin { get; set; }
            public int? YearMax { get; set; }
            public int? MaxMpaRatingId { get; set; }
            public bool ExcludeAdult { get; set; } = true;
            public bool ExcludeRemoveFromRandom { get; set; } = true;
            public List<string>? Kinds { get; set; }
            public List<string>? PathContains { get; set; }
            public List<string>? Languages { get; set; }
            public List<string>? ExcludeLanguages { get; set; }
            public int? MinViewers { get; set; }
            public SaveRange? CultClassic { get; set; }
            public SaveRange? Surrealism { get; set; }
            public SaveRange? Intensity { get; set; }
            public SaveRange? Novelty { get; set; }
            public SaveRange? Rewatchability { get; set; }
            public SaveRange? Energy { get; set; }
            public List<SaveTagRule>? Tags { get; set; }
            public List<SaveCreditRule>? Credits { get; set; }
        }

        [HttpPost("/API/Channel/Admin/Save")]
        public async Task<IActionResult> Save([FromBody] SaveChannelRequest req)
        {
            if (!await IsCurrentUserEditor()) return Forbid();

            if (string.IsNullOrWhiteSpace(req.Name))
                return BadRequest(new { message = "Name is required." });

            var existing = (req.Id is int eid && eid > 0)
                ? await movieDb.Channels.FirstOrDefaultAsync(c => c.Id == eid)
                : null;
            if (req.Id is int rid && rid > 0 && existing == null)
                return NotFound(new { message = "Channel not found." });

            // Start from the STORED filter so catalog-authored facets the form doesn't surface (IMDb/RT/
            // popularity ranges, networks, countries, freshness) survive an admin edit — then overwrite
            // only the form-managed fields. Rebuilding a fresh ChannelFilter here would silently strip them.
            var filter = ChannelFilter.Parse(existing?.FilterJson);
            filter.GenreIds = (req.GenreIds ?? new()).Where(id => id > 0).Distinct().ToList();
            filter.GenreMode = string.Equals(req.GenreMode, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "any";
            filter.YearMin = req.YearMin;
            filter.YearMax = req.YearMax;
            filter.MaxMpaRatingId = req.MaxMpaRatingId;
            filter.ExcludeAdult = req.ExcludeAdult;
            filter.ExcludeRemoveFromRandom = req.ExcludeRemoveFromRandom;
            filter.Kinds = ParseKinds(req.Kinds);
            filter.PathContains = Clean(req.PathContains);
            filter.Languages = Clean(req.Languages);
            filter.ExcludeLanguages = Clean(req.ExcludeLanguages);
            filter.MinViewers = req.MinViewers is int mv && mv > 0 ? mv : null;
            filter.CultClassic = ToRange(req.CultClassic);
            filter.Surrealism = ToRange(req.Surrealism);
            filter.Intensity = ToRange(req.Intensity);
            filter.Novelty = ToRange(req.Novelty);
            filter.Rewatchability = ToRange(req.Rewatchability);
            filter.Energy = ToRange(req.Energy);
            filter.Tags = (req.Tags ?? new())
                .Where(t => Enum.TryParse<TagCategory>(t.Category, true, out _) && (t.Values?.Count ?? 0) > 0)
                .Select(t => new TagRule
                {
                    Category = Enum.Parse<TagCategory>(t.Category!, true),
                    Values = Clean(t.Values),
                    Mode = string.Equals(t.Mode, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "any",
                    Negate = t.Negate,
                })
                .Where(t => t.Values.Count > 0)
                .ToList();
            filter.Credits = (req.Credits ?? new())
                .Select(c => new CreditRule
                {
                    PersonIds = (c.PersonIds ?? new()).Where(id => id > 0).Distinct().ToList(),
                    Role = Enum.TryParse<CreditRole>(c.Role, true, out var r) ? r : (CreditRole?)null,
                })
                .Where(c => c.PersonIds.Count > 0)
                .ToList();

            var strategy = !string.IsNullOrWhiteSpace(req.ScheduleStrategy) && ValidStrategies.Contains(req.ScheduleStrategy)
                ? ScheduleStrategies.First(s => string.Equals(s, req.ScheduleStrategy, StringComparison.OrdinalIgnoreCase))
                : "SeededShuffle";
            // Keep the legacy ShuffleMode roughly consistent (it's only a fallback once ScheduleStrategy is set).
            var shuffleMode = string.Equals(strategy, "ReleaseDate", StringComparison.OrdinalIgnoreCase) ? "ReleaseDate" : "SeededShuffle";
            var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim();

            Channel channel;
            if (existing != null)
            {
                // The lineup is materialized ahead and never rewritten, so a filter/strategy change won't
                // reach already-generated items. Drop the not-yet-aired tail so the new rule takes effect
                // going forward. Season changes only affect visibility, so they don't need a tail drop.
                var filterChanged = existing.FilterJson != filter.ToJson()
                    || (existing.ScheduleStrategy ?? "") != strategy
                    || existing.ShuffleMode != shuffleMode;

                existing.Name = req.Name.Trim();
                existing.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
                existing.SortOrder = req.SortOrder;
                existing.Enabled = req.Enabled;
                existing.Category = category;
                existing.ScheduleStrategy = strategy;
                existing.ShuffleMode = shuffleMode;
                existing.FilterJson = filter.ToJson();
                existing.SeasonStartMonth = req.SeasonStartMonth;
                existing.SeasonStartDay = req.SeasonStartDay;
                existing.SeasonEndMonth = req.SeasonEndMonth;
                existing.SeasonEndDay = req.SeasonEndDay;

                if (filterChanged)
                {
                    var now = DateTime.UtcNow;
                    var future = await movieDb.ChannelScheduleItems
                        .Where(i => i.ChannelId == existing.Id && i.StartUtc > now)
                        .ToListAsync();
                    if (future.Count > 0)
                        movieDb.ChannelScheduleItems.RemoveRange(future);
                }
                channel = existing;
            }
            else
            {
                channel = new Channel
                {
                    Name = req.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                    SortOrder = req.SortOrder,
                    Enabled = req.Enabled,
                    Category = category,
                    ScheduleStrategy = strategy,
                    ShuffleMode = shuffleMode,
                    FilterJson = filter.ToJson(),
                    SeasonStartMonth = req.SeasonStartMonth,
                    SeasonStartDay = req.SeasonStartDay,
                    SeasonEndMonth = req.SeasonEndMonth,
                    SeasonEndDay = req.SeasonEndDay,
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

        // ── helpers ──
        private static ContentKinds ParseKinds(List<string>? kinds)
        {
            ContentKinds result = 0;
            foreach (var k in kinds ?? new())
                if (Enum.TryParse<ContentKinds>(k, true, out var ck)) result |= ck;
            return result == 0 ? ContentKinds.Movies : result; // empty ⇒ movies-only (back-compat default)
        }

        private static List<string> KindsToList(ContentKinds kinds)
        {
            var list = new List<string>();
            if (kinds.HasFlag(ContentKinds.Movies)) list.Add("Movies");
            if (kinds.HasFlag(ContentKinds.Series)) list.Add("Series");
            if (kinds.HasFlag(ContentKinds.Misc)) list.Add("Misc");
            return list;
        }

        private static FilterRange? ToRange(SaveRange? r) =>
            r == null || (r.Min == null && r.Max == null) ? null : new FilterRange(r.Min, r.Max);

        private static List<string> Clean(List<string>? values) =>
            (values ?? new()).Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct().ToList();
    }
}
