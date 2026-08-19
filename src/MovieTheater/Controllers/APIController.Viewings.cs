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
        [HttpGet("/API/ImdbApiLookupImdbID")]
        public async Task<Movie> ImdbApiLookupImdbID(string imdbID)
        {
            return await imdb.ImdbApiLookupImdbID(imdbID);
        }



        [HttpGet("/API/ImdbApiLookupName")]
        public async Task<Movie> ImdbApiLookupName(string name)
        {
            return await imdb.ImdbApiLookupName(name);
        }

        [HttpGet("/API/TMDBLookupImdbID")]
        public async Task<MovieDto> TmdbLookupImdbID(string imdbID)
        {
            return await tmdb.GetMovie(imdbID);
        }

        [HttpGet("/API/TMDBLookupName")]
        public async Task<MovieDto> TmdbLookupName(string name)
        {
            return await tmdb.GetMovieByName(name);
        }

        [HttpGet("/API/OMDBLookupName")]
        public async Task<Movie> OmdbLookupName(string name)
        {
            return await omdb.GetMovieByName(name);
        }

        [HttpGet("/API/OMDBLookupImdbID")]
        public async Task<Movie> OmdbLookupImdbID(string imdbID)
        {
            return await omdb.GetMovieByImdbId(imdbID);
        }

        [HttpPost("/API/SetViewingState")]
        public async Task<IActionResult> SetViewingState([FromBody] ViewingState viewingState)
        {
            if (viewingState == null)
            {
                return BadRequest(new { Success = false, Message = "No User Movie Data Provided." });
            }

            // Act on the authenticated cookie identity, never the client-supplied username —
            // otherwise anyone could edit a password-protected user's lists without the password.
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return Unauthorized(new { Success = false, Message = "Not logged in." });
            }

            var user = await movieDb.Users.FindAsync(currentUserId.Value);
            if (user == null)
            {
                return Unauthorized(new { Success = false, Message = "No User Found." });
            }

            var action = viewingState.Action == ViewingType.SetWatched ? "Seen" : "WantToWatch";
            // movie/series share an id space; misc videos have their own. The card's Kind says which
            // target the id refers to, and which typed FK on Viewing to read/write.
            bool isSeries = string.Equals(viewingState.Kind, "series", StringComparison.OrdinalIgnoreCase);
            bool isMisc = string.Equals(viewingState.Kind, "misc", StringComparison.OrdinalIgnoreCase);
            int id = viewingState.MovieID;

            if (isSeries)
            {
                if (!await movieDb.Series.AnyAsync(s => s.Id == id))
                    return BadRequest(new { Success = false, Message = "Invalid Series ID." });
            }
            else if (isMisc)
            {
                if (!await movieDb.MiscVideos.AnyAsync(mv => mv.Id == id))
                    return BadRequest(new { Success = false, Message = "Invalid MiscVideo ID." });
            }
            else if (!await movieDb.Movies.AnyAsync(m => m.id == id))
            {
                return BadRequest(new { Success = false, Message = "Invalid Movie ID." });
            }

            var existingViewing = isSeries
                ? await movieDb.Viewings.FirstOrDefaultAsync(e => e.UserID == user.UserID && e.SeriesId == id && e.ViewingType == action)
                : isMisc
                    ? await movieDb.Viewings.FirstOrDefaultAsync(e => e.UserID == user.UserID && e.MiscVideoId == id && e.ViewingType == action)
                    : await movieDb.Viewings.FirstOrDefaultAsync(e => e.UserID == user.UserID && e.MovieID == id && e.ViewingType == action);
            bool shouldCreateNew = existingViewing == null && viewingState.SetActive;
            bool shouldDeleteExisting = existingViewing != null && !viewingState.SetActive;

            if (shouldCreateNew)
            {
                var newViewing = new Viewing
                {
                    MovieID = (isSeries || isMisc) ? (int?)null : id,
                    SeriesId = isSeries ? id : (int?)null,
                    MiscVideoId = isMisc ? id : (int?)null,
                    UserID = user.UserID,
                    ViewingType = action,
                };
                await movieDb.Viewings.AddAsync(newViewing);
            }
            if (shouldDeleteExisting)
            {
                movieDb.Viewings.Remove(existingViewing);
            }

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true });
        }

        public class RatingItem
        {
            public int Id { get; set; }
            /// <summary>"movie" (default), "series", or "misc" — selects which typed FK on Viewing to write.</summary>
            public string Kind { get; set; } = "movie";
            /// <summary>0–100 score, or null to clear the rating (remove the row — "unranked").</summary>
            public int? Value { get; set; }
        }

        public class SetRatingsRequest
        {
            public List<RatingItem> Items { get; set; } = new();
        }

        // Upsert a user's own 0–100 ratings. Stored on Viewing as ViewingType=="Rated" with the score in
        // ViewingData (the same rows the legacy rating feature used). Mirrors SetViewingState's cookie-identity
        // and kind→FK dispatch. Bounded + idempotent: one capped chunk per call, writes only changed rows, and
        // re-sending the same value is a no-op — the Rate page's autosave drives the chunk loop to completion.
        [HttpPost("/API/SetRatings")]
        public async Task<IActionResult> SetRatings([FromBody] SetRatingsRequest request)
        {
            var items = request?.Items;
            if (items == null || items.Count == 0)
                return Ok(new { Success = true, updated = 0, skipped = 0, deleted = 0 });

            // Bounded write (project rule): the caller sends capped chunks and drives the loop to completion.
            if (items.Count > 200)
                return BadRequest(new { Success = false, Message = "Too many items; send at most 200 per call." });

            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
                return Unauthorized(new { Success = false, Message = "Not logged in." });
            int uid = currentUserId.Value;

            static string NormKind(string? k) =>
                string.Equals(k, "series", StringComparison.OrdinalIgnoreCase) ? "series"
                : string.Equals(k, "misc", StringComparison.OrdinalIgnoreCase) ? "misc"
                : "movie";

            var movieIds = items.Where(i => NormKind(i.Kind) == "movie").Select(i => i.Id).Distinct().ToList();
            var seriesIds = items.Where(i => NormKind(i.Kind) == "series").Select(i => i.Id).Distinct().ToList();
            var miscIds = items.Where(i => NormKind(i.Kind) == "misc").Select(i => i.Id).Distinct().ToList();

            // Validate targets exist (one set-load per kind).
            var validMovies = movieIds.Count == 0 ? new HashSet<int>()
                : (await movieDb.Movies.Where(m => movieIds.Contains(m.id)).Select(m => m.id).ToListAsync()).ToHashSet();
            var validSeries = seriesIds.Count == 0 ? new HashSet<int>()
                : (await movieDb.Series.Where(s => seriesIds.Contains(s.Id)).Select(s => s.Id).ToListAsync()).ToHashSet();
            var validMisc = miscIds.Count == 0 ? new HashSet<int>()
                : (await movieDb.MiscVideos.Where(mv => miscIds.Contains(mv.Id)).Select(mv => mv.Id).ToListAsync()).ToHashSet();

            // Load the user's existing "Rated" rows for just these targets (one query).
            var existingRows = await movieDb.Viewings
                .Where(v => v.UserID == uid && v.ViewingType == "Rated" &&
                    ((v.MovieID != null && movieIds.Contains(v.MovieID.Value)) ||
                     (v.SeriesId != null && seriesIds.Contains(v.SeriesId.Value)) ||
                     (v.MiscVideoId != null && miscIds.Contains(v.MiscVideoId.Value))))
                .ToListAsync();
            Viewing? Find(string kind, int id) => existingRows.FirstOrDefault(v =>
                kind == "series" ? v.SeriesId == id : kind == "misc" ? v.MiscVideoId == id : v.MovieID == id);

            int updated = 0, skipped = 0, deleted = 0;
            bool rescored = false;
            foreach (var item in items)
            {
                var kind = NormKind(item.Kind);
                bool exists = kind == "series" ? validSeries.Contains(item.Id)
                            : kind == "misc" ? validMisc.Contains(item.Id)
                            : validMovies.Contains(item.Id);
                if (!exists) { skipped++; continue; }

                var existing = Find(kind, item.Id);

                if (item.Value == null)
                {
                    if (existing != null) { movieDb.Viewings.Remove(existing); existingRows.Remove(existing); deleted++; }
                    else skipped++;
                    continue;
                }

                var data = Math.Clamp(item.Value.Value, 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (existing == null)
                {
                    var row = new Viewing
                    {
                        MovieID = kind == "movie" ? item.Id : (int?)null,
                        SeriesId = kind == "series" ? item.Id : (int?)null,
                        MiscVideoId = kind == "misc" ? item.Id : (int?)null,
                        UserID = uid,
                        ViewingType = "Rated",
                        ViewingData = data,
                    };
                    await movieDb.Viewings.AddAsync(row);
                    existingRows.Add(row);
                    updated++;
                }
                else if (existing.ViewingData != data) { existing.ViewingData = data; rescored = true; updated++; }
                else skipped++;
            }

            // A re-score edits the row in place: ViewingID and row count both survive, so the
            // recommendation staleness stamp (max ViewingID : count : …) cannot see it. Blank the
            // stored stamp — a blank never matches a computed stamp, so the maintenance loop picks
            // this user up on its next pass. New rows and deletes move the stamp on their own.
            if (rescored)
            {
                var profile = await movieDb.UserTasteProfiles.FirstOrDefaultAsync(p => p.UserId == uid);
                if (profile != null) profile.RatingsStamp = "";
            }

            await movieDb.SaveChangesAsync();
            return Ok(new { Success = true, updated, skipped, deleted });
        }

        [HttpGet("/API/API_UserList")]
        public IActionResult API_UserList()
        {
            var userList = movieDb.Users
                .OrderByDescending(u => u.LastLogin.HasValue)
                .ThenByDescending(u => u.LastLogin)
                .Select(d => d.Username)
                .ToList();
            return Json(userList);
        }

        private static bool IsValidImdbId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var id = input.Trim();
            // IMDB title IDs are typically "tt" followed by 7-9 digits (e.g., tt1234567)
            return System.Text.RegularExpressions.Regex.IsMatch(id, @"^tt\d{7,9}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static bool TryParseBggThingId(string input, out int bggThingId)
        {
            bggThingId = 0;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var trimmed = input.Trim();
            if (int.TryParse(trimmed, out bggThingId) && bggThingId > 0)
                return true;

            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"(?:boardgame|boardgameexpansion)/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
                match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\b(\d{3,})\b");

            return match.Success && int.TryParse(match.Groups[1].Value, out bggThingId) && bggThingId > 0;
        }

        [HttpGet("/API/GetMoviesByRating")]
        public async Task<IActionResult> GetMoviesByRating(string ratingIds, int page = 1, int pageSize = 60, string? types = null, string? sort = null, int seed = 0)
        {
            int ageRestriction = 100;
            var currentUserId = GetCurrentUserId();
            if (currentUserId.HasValue)
            {
                var setRestriction = await movieDb.UserSettings
                    .FirstOrDefaultAsync(u => u.SettingKey == "AgeRestriction" && u.UserID == currentUserId.Value);
                if (setRestriction != null && int.TryParse(setRestriction.SettingValue, out int parsedRestriction))
                    ageRestriction = parsedRestriction;
            }

            // Browse by the rating ITSELF — clicking "PG-13" asks for the PG-13 movies, not "PG-13 and
            // everything tamer" (which is what this endpoint used to do, as a rating CAP). A title is
            // filed under the rating that actually gates it: real certificate → legacy → inferred
            // (RatingGate.MovieEffectiveBucketIn), so a movie shows up under one button and only one.
            //
            // A SET of buckets, not a single id, because one button can stand for more than one: NC-17
            // covers NC-17(5) and X(6), which are one certificate to anyone browsing.
            //
            // The age gate still applies on top: asking for a bucket above the viewer's restriction is
            // simply an empty grid — the two predicates intersect to nothing, no special-casing.
            // Order at the DB (nulls last, then collation — digit-titles sort before letters) and page
            // there, so the infinite-scroll client's repeated page fetches don't each re-materialize +
            // re-sort the whole rating set.
            var buckets = (ratingIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (buckets.Count == 0)
                return Ok(await PageCardsAsync(movieDb.Movies.Where(m => false).Select(ToCardDto), page, pageSize));

            var baseQuery = movieDb.Movies
                .Where(m => m.ReviewBatch == null)
                .Where(Web.RatingGate.MovieVisibleAtAge(movieDb, ageRestriction))
                .Where(Web.RatingGate.MovieEffectiveBucketIn(movieDb, buckets));

            // Rating browse is movie-only, so apply just the Movie-bucket part of the Type scope.
            // (A scope without any movie bucket — e.g. Series-only — yields no rating results.)
            var scope = ParseTypeScope(types);
            if (scope.Count > 0)
            {
                var movieBuckets = scope.Where(t => t == NormalizedTitleType.Movies || t == NormalizedTitleType.Short).ToList();
                baseQuery = movieBuckets.Count > 0
                    ? baseQuery.Where(m => movieBuckets.Contains(m.NormalizedTitleType))
                    : baseQuery.Where(m => false);
            }

            // Order at the DB by the chosen sort, then page there.
            var query = SortMovies(baseQuery, NormalizeSort(sort), seed).Select(ToCardDto);

            return Ok(await PageCardsAsync(query, page, pageSize));
        }

        // Small, rarely-changing lookup tables (genres, MPA ratings, total count) fetched by every client
        // on load — cache briefly so they aren't re-queried per visit. Size 1 satisfies the cache's SizeLimit.
        private static readonly TimeSpan LookupCacheTtl = TimeSpan.FromMinutes(5);

        private async Task<T> GetOrCacheLookupAsync<T>(string key, Func<Task<T>> load)
        {
            if (memoryCache.TryGetValue(key, out T cached))
                return cached;
            var value = await load();
            memoryCache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = LookupCacheTtl, Size = 1 });
            return value;
        }

        // Distinct genre names from the normalized Genre table, for the browse genre filter.
        [HttpGet("/API/GetGenres")]
        public async Task<IActionResult> GetGenres()
        {
            var genres = await GetOrCacheLookupAsync("lookup:genres", () => movieDb.Genres
                .OrderBy(g => g.Name)
                .Select(g => g.Name)
                .ToListAsync());
            return Ok(genres);
        }

        [HttpGet("/API/GetMPARatings")]
        public async Task<IActionResult> GetMPARatings()
        {
            var result = await GetOrCacheLookupAsync("lookup:mparatings", async () =>
            {
                var ratingIds = await movieDb.RatingMaps
                    .Select(rm => rm.MPARatingID)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToListAsync();

                var mpaNames = await movieDb.RatingMpas
                    .ToDictionaryAsync(mpa => mpa.RatingID, mpa => mpa.MPAName);

                return ratingIds.Select(id => new
                {
                    id,
                    name = mpaNames.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : id.ToString()
                }).ToList();
            });

            return Ok(result);
        }

        public class UserSettingRequest
        {
            public string SettingKey { get; set; }
            public string SettingValue { get; set; }
        }

        // Keys a user is allowed to set on their own account through the self-service endpoint.
        // Default-deny: anything not listed here (notably the privileged access grants
        // "CanEditMovies" and "ComicSiteAccess") can only be set via /API/Admin/SetUserSetting,
        // which requires a password-verified config admin. Without this allow-list any logged-in
        // user could grant themselves editor rights by POSTing CanEditMovies=true.
        private static readonly HashSet<string> SelfServiceSettingKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "AgeRestriction",
            "CardStyle",
            "EnablePagination",
            "ShowBoardgameExpansions",
            "RatingAnchors",
            "FavoriteChannels",
        };

        [HttpPost("/API/SetUserSetting")]
        public async Task<IActionResult> SetUserSetting([FromBody] UserSettingRequest request)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
                return Unauthorized(new { Success = false, Message = "Not logged in." });

            if (string.IsNullOrEmpty(request?.SettingKey))
                return BadRequest(new { Success = false, Message = "SettingKey is required." });

            if (!SelfServiceSettingKeys.Contains(request.SettingKey))
                return Forbid();

            var existing = await movieDb.UserSettings
                .FirstOrDefaultAsync(u => u.UserID == currentUserId.Value && u.SettingKey == request.SettingKey);

            if (request.SettingValue == null)
            {
                if (existing != null)
                {
                    movieDb.UserSettings.Remove(existing);
                    await movieDb.SaveChangesAsync();
                }
            }
            else
            {
                if (existing != null)
                {
                    existing.SettingValue = request.SettingValue;
                }
                else
                {
                    var newSetting = new MovieTheater.Db.UserSettings
                    {
                        UserID = currentUserId.Value,
                        SettingKey = request.SettingKey,
                        SettingValue = request.SettingValue,
                    };
                    await movieDb.UserSettings.AddAsync(newSetting);
                    movieDb.Entry(newSetting).Reference(s => s.User).IsModified = false;
                }
                await movieDb.SaveChangesAsync();
            }

            return Ok(new { Success = true });
        }
    }
}
