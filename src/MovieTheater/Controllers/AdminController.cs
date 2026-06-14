using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Controllers
{
    /// <summary>
    /// Administrator-only user management (creating initial streaming passwords, granting edit
    /// rights, etc.). Who is an administrator is defined entirely by <see cref="MovieTheaterConfiguration.AdminUsernames"/>
    /// — it cannot be granted through the app, so admin rights can't be escalated in-band.
    ///
    /// Because the communal login is passwordless, a username alone proves nothing: anyone could
    /// type an admin's name at the login box. So every endpoint here additionally requires a
    /// password-verified session (the amr=pwd claim the streaming policy keys off, §3.1). The net
    /// effect is that an admin account must have a password set, and only the session that proved
    /// that password can administer.
    /// </summary>
    [Authorize]
    public class AdminController : Controller
    {
        private readonly MovieDb movieDb;
        private readonly MovieTheaterConfiguration config;

        // Reused for admin-set passwords so the hash format matches the login/SetPassword paths.
        private static readonly PasswordHasher<User> passwordHasher = new();

        public AdminController(MovieDb movieDb, MovieTheaterConfiguration config)
        {
            this.movieDb = movieDb;
            this.config = config;
        }

        private bool IsAdminUsername(string? username) =>
            !string.IsNullOrEmpty(username)
            && config.AdminUsernames.Any(a => string.Equals(a, username, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// True only when the caller is a config-designated admin AND this session is
        /// password-verified — see the class remarks for why both are required.
        /// </summary>
        private bool IsCurrentUserAdmin() =>
            User.FindFirst("amr")?.Value == "pwd"
            && IsAdminUsername(User.FindFirst(ClaimTypes.Name)?.Value);

        /// <summary>Every user with the bits an admin needs to manage them.</summary>
        [HttpGet("/API/Admin/Users")]
        public async Task<IActionResult> Users()
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            var users = await movieDb.Users
                .OrderBy(u => u.Username)
                .Select(u => new { u.UserID, u.Username, u.LastLogin, HasPassword = u.PasswordHash != null })
                .ToListAsync();

            // The editor bit lives in UserSettings; pull the lot in one query and join in memory.
            var editorIds = await movieDb.UserSettings
                .Where(s => s.SettingKey == "CanEditMovies" && s.SettingValue == "true")
                .Select(s => s.UserID)
                .ToListAsync();
            var editorSet = editorIds.ToHashSet();

            var result = users.Select(u => new
            {
                userId = u.UserID,
                username = u.Username,
                lastLogin = u.LastLogin,
                hasPassword = u.HasPassword,
                canEditMovies = editorSet.Contains(u.UserID),
                // Admin status is config-bound; surfaced read-only so the UI can badge it.
                isAdmin = IsAdminUsername(u.Username),
            });

            return Json(result);
        }

        public class SetPasswordRequest
        {
            public int UserId { get; set; }
            public string? NewPassword { get; set; }
        }

        /// <summary>
        /// Sets (or, with an empty password, clears) any user's password on admin authority — no
        /// current-password challenge. This is how a user gets their initial streaming password,
        /// since users can't create their own first password.
        /// </summary>
        [HttpPost("/API/Admin/SetPassword")]
        public async Task<IActionResult> SetUserPassword([FromBody] SetPasswordRequest request)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            var user = await movieDb.Users.FindAsync(request.UserId);
            if (user == null)
                return NotFound(new { success = false, message = "User not found." });

            if (string.IsNullOrEmpty(request.NewPassword))
            {
                user.PasswordHash = null;
            }
            else
            {
                if (request.NewPassword.Length < 8)
                    return BadRequest(new { success = false, message = "Password must be at least 8 characters." });

                user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
            }

            await movieDb.SaveChangesAsync();
            return Ok(new { success = true, hasPassword = user.PasswordHash != null });
        }

        public class SetUserSettingRequest
        {
            public int UserId { get; set; }
            public string? SettingKey { get; set; }
            public string? SettingValue { get; set; }
        }

        /// <summary>
        /// Upserts (or deletes, when SettingValue is null) a UserSettings row for any user — the
        /// admin-facing counterpart to the self-service /API/SetUserSetting. Used to grant editor
        /// rights and the like. Admin status itself isn't a UserSetting, so it can't be set here.
        /// </summary>
        [HttpPost("/API/Admin/SetUserSetting")]
        public async Task<IActionResult> SetUserSetting([FromBody] SetUserSettingRequest request)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            if (string.IsNullOrEmpty(request.SettingKey))
                return BadRequest(new { success = false, message = "SettingKey is required." });

            var user = await movieDb.Users.FindAsync(request.UserId);
            if (user == null)
                return NotFound(new { success = false, message = "User not found." });

            var existing = await movieDb.UserSettings
                .FirstOrDefaultAsync(s => s.UserID == request.UserId && s.SettingKey == request.SettingKey);

            if (request.SettingValue == null)
            {
                if (existing != null)
                {
                    movieDb.UserSettings.Remove(existing);
                    await movieDb.SaveChangesAsync();
                }
            }
            else if (existing != null)
            {
                existing.SettingValue = request.SettingValue;
                await movieDb.SaveChangesAsync();
            }
            else
            {
                var setting = new UserSettings
                {
                    UserID = request.UserId,
                    SettingKey = request.SettingKey,
                    SettingValue = request.SettingValue,
                };
                await movieDb.UserSettings.AddAsync(setting);
                // The navigation isn't loaded; tell EF not to try to insert a User alongside it.
                movieDb.Entry(setting).Reference(s => s.User).IsModified = false;
                await movieDb.SaveChangesAsync();
            }

            return Ok(new { success = true });
        }
    }
}
