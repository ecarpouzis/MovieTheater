using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Db;

namespace MovieTheater.Books
{
    /// <summary>
    /// The Books access gate — the family-album gate's shape (<see cref="Photos.FamilyAlbumGate"/>), applied
    /// to the Books vertical. Membership is one <see cref="UserSettings"/> row, <c>BooksAccess = "true"</c>,
    /// granted only from the admin user surface; an administrator is NOT implicitly a member.
    ///
    /// <para>Two policies share the requirement. <see cref="PolicyName"/> is the site session's: authenticated
    /// AND this session proved a password (<c>amr=pwd</c>) AND the row — what every proxied <c>/API/Books</c>
    /// request passes through. <see cref="BasicPolicyName"/> is the e-readers': the <c>OpdsBasic</c> scheme
    /// verified the password on THIS request, so no <c>amr</c> claim is asked for, only the row.</para>
    ///
    /// <para>The grant travels with a second per-user setting, <see cref="CeilingKey"/> (0–3, default
    /// <see cref="DefaultCeiling"/>), which the identity header carries to the host. Both are read in one
    /// memoized query per request (<see cref="IBooksMembership"/>).</para>
    /// </summary>
    public static class BooksAccessGate
    {
        public const string PolicyName = "RequireBooksAccess";
        public const string BasicPolicyName = "RequireBooksAccessBasic";
        public const string SettingKey = "BooksAccess";
        public const string SettingValue = "true";
        /// <summary>Per-user maturity ceiling for Books (0 all-ages … 3 unrestricted); admin-set only.</summary>
        public const string CeilingKey = "BooksMaturityCeiling";
        public const int DefaultCeiling = 3;
        /// <summary>Per-user kids-mode skin; self-service, surfaced by /API/Me, never sent to the host.</summary>
        public const string KidsStyleKey = "BooksKidsStyle";

        public static void AddPolicies(AuthorizationOptions options)
        {
            options.AddPolicy(PolicyName, policy =>
                policy.RequireAuthenticatedUser()
                      .RequireClaim("amr", "pwd")
                      .AddRequirements(new BooksAccessRequirement()));
            options.AddPolicy(BasicPolicyName, policy =>
                policy.AddAuthenticationSchemes(OpdsBasicAuthenticationHandler.SchemeName)
                      .RequireAuthenticatedUser()
                      .AddRequirements(new BooksAccessRequirement()));
        }

        public static IServiceCollection AddBooksAccessServices(this IServiceCollection services)
        {
            services.AddScoped<IBooksMembership, BooksMembership>();
            services.AddScoped<IAuthorizationHandler, BooksAccessHandler>();
            return services;
        }

        public static bool IsGrant(string? settingValue) =>
            string.Equals(settingValue, SettingValue, StringComparison.OrdinalIgnoreCase);

        public static int ParseCeiling(string? settingValue) =>
            int.TryParse(settingValue, out var c) ? Math.Clamp(c, 0, 3) : DefaultCeiling;
    }

    public sealed record BooksGrant(bool IsMember, int MaturityCeiling)
    {
        public static readonly BooksGrant None = new(false, BooksAccessGate.DefaultCeiling);
    }

    public class BooksAccessRequirement : IAuthorizationRequirement
    {
    }

    /// <summary>"Is this user in Books, and at what ceiling?" — a seam so the gate is provable without the
    /// live database (the configured connection string IS production).</summary>
    public interface IBooksMembership
    {
        Task<BooksGrant> GetAsync(int userId);
    }

    /// <summary>One indexed <see cref="UserSettings"/> read for both keys, memoized per request (scoped) so
    /// the policy and the identity transform that follows it cost a single round-trip together.</summary>
    public class BooksMembership : IBooksMembership
    {
        private readonly MovieDb movieDb;
        private int cachedUserId = -1;
        private BooksGrant cached = BooksGrant.None;

        public BooksMembership(MovieDb movieDb) => this.movieDb = movieDb;

        public async Task<BooksGrant> GetAsync(int userId)
        {
            if (cachedUserId == userId) return cached;
            var rows = await movieDb.UserSettings
                .Where(s => s.UserID == userId && (s.SettingKey == BooksAccessGate.SettingKey || s.SettingKey == BooksAccessGate.CeilingKey))
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToListAsync();
            var grant = BooksAccessGate.IsGrant(rows.FirstOrDefault(r => r.SettingKey == BooksAccessGate.SettingKey)?.SettingValue);
            var ceiling = BooksAccessGate.ParseCeiling(rows.FirstOrDefault(r => r.SettingKey == BooksAccessGate.CeilingKey)?.SettingValue);
            cachedUserId = userId;
            cached = new BooksGrant(grant, ceiling);
            return cached;
        }
    }

    /// <summary>Fails closed: no id claim, an unparseable one, or any value other than the grant leaves the
    /// requirement unmet (403 for an authenticated caller, 401 for an anonymous one).</summary>
    public class BooksAccessHandler : AuthorizationHandler<BooksAccessRequirement>
    {
        private readonly IBooksMembership membership;

        public BooksAccessHandler(IBooksMembership membership) => this.membership = membership;

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, BooksAccessRequirement requirement)
        {
            var idClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(idClaim, out var userId)) return;
            if ((await membership.GetAsync(userId)).IsMember) context.Succeed(requirement);
        }
    }
}
