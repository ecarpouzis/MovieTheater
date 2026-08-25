using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieTheater.Books;
using MovieTheater.Core;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The Books access gate — the family-album gate's twin (see FamilyAlbumGateTests for the harness's
    /// rationale). The policy is evaluated by the REAL authorization middleware against the declaration
    /// <c>Startup</c> uses, with only the UserSettings read substituted (the configured connection string
    /// IS the live database). The endpoint is a stand-in for the Yarp route, which carries the policy by
    /// name (<see cref="BooksAccessGate.PolicyName"/>) rather than an attribute.
    /// </summary>
    public class BooksAccessGateTests
    {
        private const string TestScheme = "BooksTestScheme";
        private const int MemberUserId = 11;
        private const int OutsiderUserId = 22;

        private sealed class FakeMembership : IBooksMembership
        {
            private readonly Dictionary<int, int> members;
            public FakeMembership(params (int userId, int ceiling)[] grants) => members = grants.ToDictionary(g => g.userId, g => g.ceiling);
            public Task<BooksGrant> GetAsync(int userId) =>
                Task.FromResult(members.TryGetValue(userId, out var c) ? new BooksGrant(true, c) : BooksGrant.None);
        }

        private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public const string PrincipalItemKey = "__testPrincipal";
            public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }
            protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
                Task.FromResult(Context.Items.TryGetValue(PrincipalItemKey, out var stored) && stored is ClaimsPrincipal principal
                    ? AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name))
                    : AuthenticateResult.NoResult());
        }

        private static ServiceProvider BuildServices(params (int userId, int ceiling)[] grants)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthentication(TestScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestScheme, _ => { })
                // the Basic policy names this scheme; in the test it is the same pass-through handler
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(OpdsBasicAuthenticationHandler.SchemeName, _ => { });
            services.AddAuthorization(BooksAccessGate.AddPolicies);
            services.AddBooksAccessServices();
            services.AddScoped<IBooksMembership>(_ => new FakeMembership(grants));
            return services.BuildServiceProvider();
        }

        private static ClaimsPrincipal SignedInAs(int userId, bool passwordVerified = true, string scheme = TestScheme)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, "user" + userId) };
            if (passwordVerified) claims.Add(new Claim("amr", "pwd"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
        }

        private static async Task<int> StatusCodeFor(string policy, ClaimsPrincipal? user, params (int userId, int ceiling)[] grants)
        {
            await using var provider = BuildServices(grants);
            using var scope = provider.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Path = policy == BooksAccessGate.BasicPolicyName ? "/opds/ping" : "/API/Books/ping";
            if (user != null) context.Items[TestAuthHandler.PrincipalItemKey] = user;
            var metadata = new EndpointMetadataCollection(new AuthorizeAttribute { Policy = policy });
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "books-route"));
            var authorization = new AuthorizationMiddleware(_ => { context.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; },
                scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>());
            var authentication = new AuthenticationMiddleware(_ => authorization.Invoke(context), scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>());
            await authentication.Invoke(context);
            return context.Response.StatusCode;
        }

        [Fact]
        public async Task Member_with_a_password_verified_session_is_let_through() =>
            Assert.Equal(200, await StatusCodeFor(BooksAccessGate.PolicyName, SignedInAs(MemberUserId), (MemberUserId, 3)));

        [Fact]
        public async Task Member_without_a_password_is_forbidden() =>
            Assert.Equal(403, await StatusCodeFor(BooksAccessGate.PolicyName, SignedInAs(MemberUserId, passwordVerified: false), (MemberUserId, 3)));

        [Fact]
        public async Task Logged_in_non_member_is_forbidden() =>
            Assert.Equal(403, await StatusCodeFor(BooksAccessGate.PolicyName, SignedInAs(OutsiderUserId), (MemberUserId, 3)));

        [Fact]
        public async Task Anonymous_caller_is_challenged() =>
            Assert.Equal(401, await StatusCodeFor(BooksAccessGate.PolicyName, null, (MemberUserId, 3)));

        [Fact]
        public async Task Nobody_is_a_member_when_nobody_is_flagged() =>
            Assert.Equal(403, await StatusCodeFor(BooksAccessGate.PolicyName, SignedInAs(MemberUserId)));

        [Fact]
        public async Task A_session_with_no_user_id_claim_is_refused()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "someone"), new Claim("amr", "pwd") }, TestScheme));
            Assert.Equal(403, await StatusCodeFor(BooksAccessGate.PolicyName, principal, (MemberUserId, 3)));
        }

        [Fact]
        public async Task The_Basic_policy_needs_membership_but_no_amr_claim()
        {
            // the OpdsBasic scheme verified the password on this very request; membership is still required
            Assert.Equal(200, await StatusCodeFor(BooksAccessGate.BasicPolicyName, SignedInAs(MemberUserId, passwordVerified: false, OpdsBasicAuthenticationHandler.SchemeName), (MemberUserId, 3)));
            Assert.Equal(403, await StatusCodeFor(BooksAccessGate.BasicPolicyName, SignedInAs(OutsiderUserId, passwordVerified: false, OpdsBasicAuthenticationHandler.SchemeName), (MemberUserId, 3)));
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("True", true)]
        [InlineData("false", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("https://old.example", false)] // the legacy ComicSiteAccess VALUE was a URL — it must never read as a grant
        public void Only_the_true_setting_value_grants(string? value, bool expected) => Assert.Equal(expected, BooksAccessGate.IsGrant(value));

        [Theory]
        [InlineData(null, 3)] [InlineData("", 3)] [InlineData("0", 0)] [InlineData("2", 2)] [InlineData("9", 3)] [InlineData("-1", 0)] [InlineData("x", 3)]
        public void The_ceiling_defaults_to_unrestricted_and_clamps(string? value, int expected) => Assert.Equal(expected, BooksAccessGate.ParseCeiling(value));

        // ── the identity header the transform mints for the host ──

        [Fact]
        public void Identity_token_round_trips_and_carries_the_four_facts()
        {
            var token = BooksIdentityToken.MintNow("s3cret", 1, "someone", isAdmin: true, maturityCeiling: 2);
            Assert.True(BooksIdentityToken.TryValidate("s3cret", token, out var p));
            Assert.Equal((1, "someone", true, 2), (p!.UserId, p.Username, p.IsAdmin, p.MaturityCeiling));
            Assert.False(BooksIdentityToken.TryValidate("other", token, out _));
            Assert.False(BooksIdentityToken.TryValidate("s3cret", token + "x", out _));
        }

        [Fact]
        public void Identity_token_honours_the_grace_and_not_a_second_more()
        {
            var expired = BooksIdentityToken.Mint("s", new BooksIdentityToken.Payload(1, "u", false, 3, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 20));
            Assert.True(BooksIdentityToken.TryValidate("s", expired, out _));                         // 20 s late < 30 s grace
            Assert.False(BooksIdentityToken.TryValidate("s", expired, TimeSpan.Zero, out _));          // strict check refuses
            var tooLate = BooksIdentityToken.Mint("s", new BooksIdentityToken.Payload(1, "u", false, 3, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 31));
            Assert.False(BooksIdentityToken.TryValidate("s", tooLate, out _));
        }

        [Fact]
        public void Identity_token_refuses_a_shifted_or_widened_payload()
        {
            // six fields signed correctly is still not an identity token
            var six = CapabilityEnvelope.Mint("s", "1", "u", "0", "3", "extra", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60).ToString());
            Assert.False(BooksIdentityToken.TryValidate("s", six, out _));
            var badCeiling = CapabilityEnvelope.Mint("s", "1", "u", "0", "7", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60).ToString());
            Assert.False(BooksIdentityToken.TryValidate("s", badCeiling, out _));
        }
    }
}
