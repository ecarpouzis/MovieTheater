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
        public class LoginRequest
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }

        // Password comes in the JSON body, never the query string, so it can't leak into request logs.
        [HttpPost("/API/Login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var givenUser = request?.Username?.Trim();

            if (string.IsNullOrEmpty(givenUser))
            {
                return NotFound();
            }

            var user = await movieDb.Users.SingleOrDefaultAsync(d => d.Username == givenUser);

            // Set when this session proved control of the account with a password — the
            // trust boundary for streaming (§3.1 of the streaming plan). Mere
            // authentication is not it: unknown usernames still auto-create accounts.
            bool passwordVerified = false;

            if (user == null)
            {
                user = new User()
                {
                    Username = givenUser
                };

                await movieDb.Users.AddAsync(user);
            }
            else if (user.PasswordHash != null)
            {
                if (string.IsNullOrEmpty(request.Password))
                {
                    return Unauthorized(new { requiresPassword = true, message = "This account is password-protected." });
                }

                var failKey = $"LoginFailures:{user.UserID}";
                if (memoryCache.TryGetValue(failKey, out int failures) && failures >= 5)
                {
                    return StatusCode(StatusCodes.Status429TooManyRequests,
                        new { requiresPassword = true, message = "Too many failed attempts. Try again in 15 minutes." });
                }

                var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
                if (verification == PasswordVerificationResult.Failed)
                {
                    memoryCache.Set(failKey, failures + 1, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
                        Size = 1
                    });
                    return Unauthorized(new { requiresPassword = true, message = "Incorrect password." });
                }

                memoryCache.Remove(failKey);
                passwordVerified = true;

                if (verification == PasswordVerificationResult.SuccessRehashNeeded)
                {
                    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
                }
            }

            user.LastLogin = DateTime.UtcNow;
            await movieDb.SaveChangesAsync();

            await SignInWithSessionClaims(user, passwordVerified);

            return Json(await BuildUserPayload(user));
        }

        // Issues (or re-issues) the auth cookie. amr=pwd marks a password-verified
        // session; the StreamingUser policy keys off it (§3.1).
        private async Task SignInWithSessionClaims(User user, bool passwordVerified)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserID.ToString()),
                new Claim(ClaimTypes.Name, user.Username)
            };
            if (passwordVerified)
            {
                claims.Add(new Claim("amr", "pwd"));
            }

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);
        }

        // Restores a session from the auth cookie without re-running login. Required for
        // password-protected accounts: the SPA can no longer silently re-login on page load.
        [HttpGet("/API/Me")]
        public async Task<IActionResult> Me()
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var user = await movieDb.Users.FindAsync(userId.Value);
            if (user == null)
            {
                return Unauthorized();
            }

            user.LastLogin = DateTime.UtcNow;
            await movieDb.SaveChangesAsync();

            // Never cache the session payload. A stale cached GET here once served a user an empty
            // ratings list while the server actually had 200+ — the Rate page then looked wiped.
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";

            return Json(await BuildUserPayload(user));
        }

        public class SetPasswordRequest
        {
            public string CurrentPassword { get; set; }
            public string NewPassword { get; set; }
        }

        [HttpPost("/API/SetPassword")]
        public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest request)
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var user = await movieDb.Users.FindAsync(userId.Value);
            if (user == null)
            {
                return Unauthorized();
            }

            if (user.PasswordHash != null)
            {
                if (string.IsNullOrEmpty(request?.CurrentPassword) ||
                    passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
                {
                    return Unauthorized(new { success = false, message = "Current password is incorrect." });
                }
            }
            else if (!string.IsNullOrEmpty(request?.NewPassword) && !IsAdminUsername(user.Username))
            {
                // Creating a *first* password is restricted: streaming access is provisioned by an
                // admin, so a user can't self-grant it. (An account that already has a password can
                // still freely change or remove it above.) Config admins are the one exception, so
                // they can bootstrap their own password and unlock the admin tools.
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { success = false, message = "An administrator must set your initial password." });
            }

            if (string.IsNullOrEmpty(request?.NewPassword))
            {
                // Empty new password removes the password, returning the account to passwordless login.
                user.PasswordHash = null;
            }
            else
            {
                if (request.NewPassword.Length < 8)
                {
                    return BadRequest(new { success = false, message = "Password must be at least 8 characters." });
                }

                user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
            }

            await movieDb.SaveChangesAsync();

            // Re-issue the cookie so the session's amr claim tracks the account state:
            // setting a password from this session proves account control (claim added);
            // removing the password drops streaming rights immediately for this session.
            await SignInWithSessionClaims(user, passwordVerified: user.PasswordHash != null);

            return Ok(new { success = true, hasPassword = user.PasswordHash != null });
        }

        private async Task<object> BuildUserPayload(User user)
        {
            // One round-trip for all of this user's viewings; the kinds are split in memory below
            // (previously four separate Viewings queries — Seen / Want / misc-Seen / Rated).
            var viewings = await movieDb.Viewings
                .Where(v => v.UserID == user.UserID)
                .Select(v => new { v.ViewingType, v.MovieID, v.SeriesId, v.MiscVideoId, v.ViewingData })
                .ToListAsync();

            // Seen / Want lists carry both movie and series ids (a viewing targets one or the other; the
            // shared id space + the card's Kind disambiguate). MovieID ?? SeriesId yields the id either way.
            var moviesSeen = viewings.Where(d => d.ViewingType == "Seen")
                .Select(d => d.MovieID ?? d.SeriesId).Where(x => x != null).Select(x => x!.Value).ToList();
            var moviesToWatch = viewings.Where(d => d.ViewingType == "WantToWatch")
                .Select(d => d.MovieID ?? d.SeriesId).Where(x => x != null).Select(x => x!.Value).ToList();

            // Watched MiscVideo ids (their own id space, so kept separate from moviesSeen). The Rate page
            // fetches their cards via GetMiscByIds.
            var miscSeen = viewings.Where(d => d.ViewingType == "Seen" && d.MiscVideoId != null)
                .Select(d => d.MiscVideoId!.Value).ToList();

            // User's own 0–100 ratings. Legacy + new ratings both live on Viewing as ViewingType=="Rated"
            // with the score in ViewingData. Keyed by a composite "{kind}:{id}" because MiscVideo has its own
            // id space that can collide with a movie id. Non-numeric / out-of-range values are treated as
            // unrated and skipped, so only real scores surface.
            var ratings = new Dictionary<string, int>();
            foreach (var r in viewings.Where(v => v.ViewingType == "Rated" && v.ViewingData != null))
            {
                if (!int.TryParse(r.ViewingData, out var score) || score < 0 || score > 100) continue;
                string? key = r.MovieID != null ? $"movie:{r.MovieID.Value}"
                            : r.SeriesId != null ? $"series:{r.SeriesId.Value}"
                            : r.MiscVideoId != null ? $"misc:{r.MiscVideoId.Value}"
                            : null;
                if (key != null) ratings[key] = score;
            }

            // One round-trip for all of this user's settings; each is picked by key in memory below
            // (previously ~8 separate UserSettings queries).
            var settings = await movieDb.UserSettings
                .Where(u => u.UserID == user.UserID)
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToListAsync();
            string? Setting(string key) => settings.FirstOrDefault(s => s.SettingKey == key)?.SettingValue;

            // Rate-page anchors — per-user JSON; parsed defensively. Bare JSON array [{ "id":"a1","value":30 }].
            System.Text.Json.JsonElement ratingAnchors;
            try
            {
                var anchorsRaw = Setting("RatingAnchors");
                ratingAnchors = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    string.IsNullOrWhiteSpace(anchorsRaw) ? "[]" : anchorsRaw);
            }
            catch (System.Text.Json.JsonException)
            {
                ratingAnchors = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("[]");
            }

            int? ageRestriction = int.TryParse(Setting("AgeRestriction"), out int parsedAgeRestriction) ? parsedAgeRestriction : (int?)null;
            var cardStyle = Setting("CardStyle") ?? "standard";
            var canEditMovies = Setting("CanEditMovies") == "true";
            bool enablePagination = bool.TryParse(Setting("EnablePagination"), out var parsedEnablePagination) && parsedEnablePagination;
            bool showBoardgameExpansions = bool.TryParse(Setting("ShowBoardgameExpansions"), out var parsedShowExpansions) && parsedShowExpansions;
            var comicSiteAccess = Setting("ComicSiteAccess");
            // Family photo album membership (photos-plan.md §2.1). Surfaced only so the nav can hide
            // /photos for non-members — the real gate is the RequireFamilyAlbum policy, re-checked
            // server-side on every /API/Photos request. Not self-grantable: the key is absent from
            // SelfServiceSettingKeys, so it can only be set through the admin surface.
            var familyAlbum = string.Equals(
                Setting(MovieTheater.Photos.FamilyAlbumGate.SettingKey),
                MovieTheater.Photos.FamilyAlbumGate.SettingValue,
                StringComparison.OrdinalIgnoreCase);

            // favorite channels — SettingValue is a JSON int array; parse defensively (empty on malformed)
            int[] favoriteChannels;
            try
            {
                var favRaw = Setting("FavoriteChannels");
                favoriteChannels = string.IsNullOrWhiteSpace(favRaw)
                    ? Array.Empty<int>()
                    : (System.Text.Json.JsonSerializer.Deserialize<int[]>(favRaw) ?? Array.Empty<int>());
            }
            catch (System.Text.Json.JsonException) { favoriteChannels = Array.Empty<int>(); }

            var hasPassword = user.PasswordHash != null;

            // Drives whether the SPA shows the admin tools. Mirrors the server gate: a config admin
            // who has a password (and so can become password-verified). A passwordless admin gets
            // false here, which is correct — they must set their password before they can administer.
            var isAdmin = IsAdminUsername(user.Username) && hasPassword;

            return new { user.Username, moviesSeen, moviesToWatch, miscSeen, ratings, ratingAnchors, ageRestriction, cardStyle, canEditMovies, enablePagination, showBoardgameExpansions, comicSiteAccess, favoriteChannels, hasPassword, isAdmin, familyAlbum };
        }

        [HttpPost("/API/Logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Ok(new { Success = true });
        }
    }
}
