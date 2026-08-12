using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Db;
using System.Security.Claims;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The family-album access gate (docs/photos-plan.md §2.1). Membership is one
    /// <see cref="UserSettings"/> row — <c>FamilyAlbum = "true"</c> — granted only from the admin
    /// user-settings surface, and it is a hard member/non-member test: there is no age or rating logic,
    /// and <b>a site administrator is NOT implicitly a member</b>. Administering the site and being in
    /// the family photos are separate facts, so nothing here consults the admin list.
    ///
    /// <para>The React nav hides <c>/photos</c> for non-members, but the UI is never the gate: the
    /// policy is applied to the whole <c>PhotosController</c> and re-checked on every request, and it
    /// will guard token minting and stream starts as those land in later phases (§2.1). Photo bytes are
    /// gated too, not just metadata — unlike movie posters, which are served openly from /Image.</para>
    /// </summary>
    public static class FamilyAlbumGate
    {
        /// <summary>Name of the ASP.NET authorization policy. Referenced by <c>[Authorize(Policy = …)]</c>.</summary>
        public const string PolicyName = "RequireFamilyAlbum";

        /// <summary>The <see cref="UserSettings.SettingKey"/> that carries membership.</summary>
        public const string SettingKey = "FamilyAlbum";

        /// <summary>The only <see cref="UserSettings.SettingValue"/> that grants it. Compared
        /// case-insensitively; anything else (including a missing row) is a non-member.</summary>
        public const string SettingValue = "true";

        /// <summary>
        /// Registers the policy and the services behind it. Called from <c>Startup</c>; the tests call
        /// the same two methods with a substituted <see cref="IFamilyAlbumMembership"/>, so the thing
        /// under test is the real policy rather than a re-declaration of it.
        /// </summary>
        /// <remarks>
        /// Two conditions, both required (§3 Phase 0 addendum). <c>amr=pwd</c> is the streaming
        /// surfaces' posture: site login is passwordless, so a username alone must not open the family
        /// album — a member needs a password set and this session must have proved it. The membership
        /// row is checked on top of that, and cannot be a claim: an admin can revoke it, and a 30-day
        /// cookie would otherwise carry a stale grant for a month.
        /// <para>Relaxing this back to membership-only is ONE line: drop the RequireClaim below.</para>
        /// </remarks>
        public static void AddPolicy(AuthorizationOptions options)
        {
            options.AddPolicy(PolicyName, policy =>
                policy.RequireAuthenticatedUser()
                      .RequireClaim("amr", "pwd")
                      .AddRequirements(new FamilyAlbumRequirement()));
        }

        /// <summary>Handler + membership lookup. Scoped: the lookup memoizes per request.</summary>
        public static IServiceCollection AddFamilyAlbumServices(this IServiceCollection services)
        {
            services.AddScoped<IFamilyAlbumMembership, FamilyAlbumMembership>();
            services.AddScoped<IAuthorizationHandler, FamilyAlbumHandler>();
            return services;
        }
    }

    /// <summary>Carries no state — the flag it stands for is per-user and read at request time.</summary>
    public class FamilyAlbumRequirement : IAuthorizationRequirement
    {
    }

    /// <summary>
    /// Answers "is this user in the family album?". A seam, so the gate can be proven without a SQL
    /// Server — the connection string this app runs against is the live shared database, which no test
    /// may touch.
    /// </summary>
    public interface IFamilyAlbumMembership
    {
        Task<bool> IsMemberAsync(int userId);
    }

    /// <summary>
    /// The real lookup: one indexed <see cref="UserSettings"/> read, memoized for the lifetime of the
    /// request (this service is scoped) so a request that hits the policy more than once — controller
    /// plus a later token mint — still costs a single round-trip. Mirrors the memoization the age-gate
    /// read already does per request.
    /// </summary>
    public class FamilyAlbumMembership : IFamilyAlbumMembership
    {
        private readonly MovieDb movieDb;
        private int cachedUserId = -1;
        private bool cachedResult;

        public FamilyAlbumMembership(MovieDb movieDb)
        {
            this.movieDb = movieDb;
        }

        public async Task<bool> IsMemberAsync(int userId)
        {
            if (cachedUserId == userId) return cachedResult;

            var value = await movieDb.UserSettings
                .Where(s => s.UserID == userId && s.SettingKey == FamilyAlbumGate.SettingKey)
                .Select(s => s.SettingValue)
                .FirstOrDefaultAsync();

            cachedUserId = userId;
            cachedResult = string.Equals(value, FamilyAlbumGate.SettingValue, System.StringComparison.OrdinalIgnoreCase);
            return cachedResult;
        }
    }

    /// <summary>
    /// Evaluates <see cref="FamilyAlbumRequirement"/>. Fails closed: an unparseable or absent user id,
    /// or any value other than "true", leaves the requirement unmet, which ASP.NET turns into a 403 for
    /// an authenticated caller and a 401 for an anonymous one (the /API status-code mapping in
    /// <c>Startup</c>'s cookie events).
    /// </summary>
    /// <remarks>
    /// This handler answers the MEMBERSHIP half only. The password-verification half (<c>amr=pwd</c>)
    /// is a claim requirement on the policy itself in <see cref="FamilyAlbumGate.AddPolicy"/> — an
    /// in-memory check that costs no query and short-circuits before this handler's read.
    /// </remarks>
    public class FamilyAlbumHandler : AuthorizationHandler<FamilyAlbumRequirement>
    {
        private readonly IFamilyAlbumMembership membership;

        public FamilyAlbumHandler(IFamilyAlbumMembership membership)
        {
            this.membership = membership;
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, FamilyAlbumRequirement requirement)
        {
            var idClaim = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(idClaim, out var userId)) return;

            if (await membership.IsMemberAsync(userId))
                context.Succeed(requirement);
        }
    }
}
