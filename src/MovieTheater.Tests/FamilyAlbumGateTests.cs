using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieTheater.Controllers;
using MovieTheater.Photos;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The family photo album's access gate (docs/photos-plan.md §2.1) — Phase 0's acceptance test:
    /// a non-family user gets 403 on every /API/Photos route.
    ///
    /// <para>These run the REAL <see cref="AuthorizationMiddleware"/> over an endpoint whose metadata is
    /// read off the shipped <see cref="PhotosController"/>, against the policy as
    /// <see cref="FamilyAlbumGate.AddPolicy"/> declares it — the same method <c>Startup</c> calls. So
    /// the status codes below are the framework's own, not a re-implementation, and deleting the
    /// controller's <c>[Authorize]</c> attribute fails the suite instead of silently opening the
    /// album.</para>
    ///
    /// <para>Only the UserSettings lookup is substituted. It has to be: the configured connection
    /// string IS the live shared production database, so no test may open it.</para>
    /// </summary>
    public class FamilyAlbumGateTests
    {
        private const string TestScheme = "FamilyAlbumTestScheme";
        private const int FamilyUserId = 11;
        private const int OutsiderUserId = 22;

        // ── Harness ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Stands in for the UserSettings read. Membership is a set of user ids here for the
        /// same reason it is a settings row in production: nothing else grants it — notably not being
        /// an administrator, who is deliberately not a member (§2.1).</summary>
        private sealed class FakeMembership : IFamilyAlbumMembership
        {
            private readonly HashSet<int> members;
            public FakeMembership(params int[] memberIds) => members = memberIds.ToHashSet();
            public Task<bool> IsMemberAsync(int userId) => Task.FromResult(members.Contains(userId));
        }

        /// <summary>Authenticates whoever the test put in <c>HttpContext.Items</c>, and otherwise
        /// returns no result. Its inherited challenge/forbid behavior is the plain 401/403 the site's
        /// cookie handler is configured to produce for /API paths.</summary>
        private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public const string PrincipalItemKey = "__testPrincipal";

            public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder)
            {
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                if (Context.Items.TryGetValue(PrincipalItemKey, out var stored) && stored is ClaimsPrincipal principal)
                    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));

                return Task.FromResult(AuthenticateResult.NoResult());
            }
        }

        private static ServiceProvider BuildServices(params int[] familyMemberIds)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthentication(TestScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestScheme, _ => { });

            // The production policy declaration and the production handler registration…
            services.AddAuthorization(FamilyAlbumGate.AddPolicy);
            services.AddFamilyAlbumServices();
            // …with only the database read replaced (last registration wins).
            services.AddScoped<IFamilyAlbumMembership>(_ => new FakeMembership(familyMemberIds));

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// A principal shaped like a PASSWORD-VERIFIED session: the id claim the membership handler
        /// reads, plus the <c>amr=pwd</c> claim the policy requires (§3 Phase 0 addendum). Site login is
        /// passwordless, so this is two separate facts and the tests keep them separable.
        /// </summary>
        private static ClaimsPrincipal SignedInAs(int userId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim("amr", "pwd"),
            }, TestScheme));

        /// <summary>A logged-in session that never proved a password — what a bare username login
        /// produces on this site.</summary>
        private static ClaimsPrincipal SignedInWithoutPasswordAs(int userId) =>
            new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, TestScheme));

        /// <summary>
        /// Drives a request at PhotosController through the authorization middleware and reports the
        /// status code the caller would see. The endpoint carries the controller's OWN attributes, so
        /// what is under test is the shipped gating, not a description of it.
        /// </summary>
        private static async Task<int> StatusCodeFor(ClaimsPrincipal? user, params int[] familyMemberIds)
        {
            await using var provider = BuildServices(familyMemberIds);
            using var scope = provider.CreateScope();

            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Path = "/API/Photos/Status";
            if (user != null) context.Items[TestAuthHandler.PrincipalItemKey] = user;

            var metadata = new EndpointMetadataCollection(
                typeof(PhotosController).GetCustomAttributes(inherit: true));
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, nameof(PhotosController)));

            var authorization = new AuthorizationMiddleware(
                _ =>
                {
                    // Reached only when the policy passed; stands in for the action running.
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return Task.CompletedTask;
                },
                // The two-argument constructor deliberately: the richer overload builds a policy cache
                // keyed on the routing EndpointDataSource, which only exists inside a real host. The
                // policy evaluation path is identical either way.
                scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>());

            // UseAuthentication() before UseAuthorization(), exactly as Startup orders them — and not
            // optional here: a policy that names no scheme is evaluated against HttpContext.User, which
            // is the authentication middleware's job to populate. Skipping it makes every caller look
            // anonymous and would turn this suite green for the wrong reason.
            var authentication = new AuthenticationMiddleware(
                _ => authorization.Invoke(context),
                scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>());

            await authentication.Invoke(context);
            return context.Response.StatusCode;
        }

        // ── The gate ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Family_flagged_user_with_a_password_verified_session_is_let_through()
        {
            Assert.Equal(StatusCodes.Status200OK,
                await StatusCodeFor(SignedInAs(FamilyUserId), FamilyUserId));
        }

        [Fact]
        public async Task Family_flagged_user_WITHOUT_a_password_is_forbidden()
        {
            // The §3 Phase 0 addendum: login here is passwordless, so a username alone must not open
            // the family album — the same posture the streaming surfaces take. Membership is present
            // and still not enough, which is the point of the test.
            Assert.Equal(StatusCodes.Status403Forbidden,
                await StatusCodeFor(SignedInWithoutPasswordAs(FamilyUserId), FamilyUserId));
        }

        [Fact]
        public async Task Logged_in_user_without_the_flag_is_forbidden()
        {
            // The Phase 0 acceptance criterion. 403, not 404 or an empty payload: the user exists and
            // is authenticated, they are simply not in the album.
            Assert.Equal(StatusCodes.Status403Forbidden,
                await StatusCodeFor(SignedInAs(OutsiderUserId), FamilyUserId));
        }

        [Fact]
        public async Task Anonymous_caller_is_challenged()
        {
            // No session at all: 401. The site maps this to a status code rather than an HTML login
            // redirect for /API paths, so the SPA sees it as a status too.
            Assert.Equal(StatusCodes.Status401Unauthorized,
                await StatusCodeFor(user: null, familyMemberIds: FamilyUserId));
        }

        [Fact]
        public async Task Nobody_is_a_member_when_nobody_is_flagged()
        {
            // Guards against the gate defaulting open on a fresh install — with no membership rows at
            // all, the previously-admitted user must now be refused.
            Assert.Equal(StatusCodes.Status403Forbidden,
                await StatusCodeFor(SignedInAs(FamilyUserId)));
        }

        [Fact]
        public async Task A_session_with_no_user_id_claim_is_refused()
        {
            // Authenticated but unidentifiable — the handler must fail closed rather than parse its
            // way to user 0.
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "someone"),
                new Claim("amr", "pwd"),
            }, TestScheme));
            Assert.Equal(StatusCodes.Status403Forbidden, await StatusCodeFor(principal, FamilyUserId));
        }

        // ── The membership rule itself ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("true", true)]
        [InlineData("True", true)]   // case-insensitive: the admin surface writes the value verbatim
        [InlineData("false", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("yes", false)]   // only the exact grant value counts
        public void Only_the_true_setting_value_grants_membership(string? settingValue, bool expected)
        {
            Assert.Equal(expected,
                string.Equals(settingValue, FamilyAlbumGate.SettingValue, StringComparison.OrdinalIgnoreCase));
        }

        // ── Invariants the controller must keep as later phases add routes ──────────────────────

        [Fact]
        public void The_policy_is_declared_once_on_the_controller_itself()
        {
            // Class-level, so a route added in a later phase inherits the gate instead of having to
            // remember it (§2.1: the UI is never the gate).
            var authorize = typeof(PhotosController)
                .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .ToList();

            Assert.Single(authorize);
            Assert.Equal(FamilyAlbumGate.PolicyName, authorize[0].Policy);
        }

        [Fact]
        public void No_photo_route_opts_out_of_the_gate()
        {
            var opened = typeof(PhotosController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) != null)
                .Select(m => m.Name)
                .ToList();

            Assert.Empty(opened);
        }

        [Fact]
        public void No_photo_route_is_exposed_through_odata()
        {
            // §6's privacy invariant: photo tables join nothing global. OData is opt-in per action via
            // [EnableQuery] in this app, so the invariant is kept by that attribute never appearing here.
            var queryable = typeof(PhotosController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes(inherit: true)
                    .Any(a => a.GetType().Name == "EnableQueryAttribute"))
                .Select(m => m.Name)
                .ToList();

            Assert.Empty(queryable);
        }
    }
}
