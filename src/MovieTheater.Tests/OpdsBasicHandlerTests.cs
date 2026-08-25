using System;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieTheater.Books;
using MovieTheater.Db;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The OPDS Basic scheme at the pod: the site's users + the site's password, honoured under /opds only,
    /// the decode failure a clean 401 (never a 500 dressed as one), and the credential memo sparing the
    /// hasher on the second request. The Users table is a throwaway SQLite file (never the live server).
    /// </summary>
    public class OpdsBasicHandlerTests : IDisposable
    {
        private readonly string dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opds-basic-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly ServiceProvider provider;
        private readonly CountingVerifier verifier = new();

        private sealed class CountingVerifier : IPasswordVerifier
        {
            public int Calls;
            public bool Verify(User user, string password) { Calls++; return password == "right"; }
        }

        public OpdsBasicHandlerTests()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache(o => o.SizeLimit = 1024);
            services.AddDbContext<MovieDb>(o => o.UseSqlite("Data Source=" + dbPath));
            services.AddSingleton<IPasswordVerifier>(verifier);
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, OpdsBasicAuthenticationHandler>(OpdsBasicAuthenticationHandler.SchemeName, _ => { });
            provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDb>();
            db.Database.EnsureCreated();
            db.Users.Add(new User { UserID = 7, Username = "reader", PasswordHash = "hash" });
            db.Users.Add(new User { UserID = 8, Username = "nopw", PasswordHash = null });
            db.SaveChanges();
        }

        private async Task<(AuthenticateResult result, DefaultHttpContext ctx)> AuthenticateAsync(string path, string? authorization)
        {
            using var scope = provider.CreateScope();
            var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            ctx.Request.Path = path;
            if (authorization != null) ctx.Request.Headers.Authorization = authorization;
            var handler = ActivatorUtilities.CreateInstance<OpdsBasicAuthenticationHandler>(scope.ServiceProvider);
            await handler.InitializeAsync(new AuthenticationScheme(OpdsBasicAuthenticationHandler.SchemeName, null, typeof(OpdsBasicAuthenticationHandler)), ctx);
            return (await handler.AuthenticateAsync(), ctx);
        }

        private static string Basic(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

        [Fact]
        public async Task Valid_credentials_yield_the_site_identity_with_a_password_claim()
        {
            var (r, _) = await AuthenticateAsync("/opds/root", Basic("reader", "right"));
            Assert.True(r.Succeeded);
            Assert.Equal("7", r.Principal!.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            Assert.Equal("pwd", r.Principal.FindFirst("amr")!.Value);
        }

        [Fact]
        public async Task The_memo_spares_the_hasher_on_the_next_page()
        {
            await AuthenticateAsync("/opds/root", Basic("reader", "right"));
            await AuthenticateAsync("/opds/page/2", Basic("reader", "right"));
            Assert.Equal(1, verifier.Calls);
        }

        [Fact]
        public async Task Wrong_password_or_no_password_fails_without_a_memo()
        {
            Assert.False((await AuthenticateAsync("/opds/root", Basic("reader", "wrong"))).result.Succeeded);
            Assert.False((await AuthenticateAsync("/opds/root", Basic("nopw", "right"))).result.Succeeded);
            Assert.False((await AuthenticateAsync("/opds/root", Basic("ghost", "right"))).result.Succeeded);
        }

        [Fact]
        public async Task Malformed_base64_is_a_failure_not_an_exception()
        {
            var (r, _) = await AuthenticateAsync("/opds/root", "Basic ###not-base64###");
            Assert.False(r.Succeeded);
            Assert.Null(r.Failure as FormatException);
        }

        [Fact]
        public async Task The_scheme_is_inert_off_the_opds_prefix_and_without_a_header()
        {
            Assert.True((await AuthenticateAsync("/API/Books/ping", Basic("reader", "right"))).result.None);
            Assert.True((await AuthenticateAsync("/opds/root", null)).result.None);
            Assert.True((await AuthenticateAsync("/opds/root", "Bearer abc")).result.None);
        }

        [Fact]
        public async Task The_challenge_names_the_realm()
        {
            using var scope = provider.CreateScope();
            var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            ctx.Request.Path = "/opds/root";
            var handler = ActivatorUtilities.CreateInstance<OpdsBasicAuthenticationHandler>(scope.ServiceProvider);
            await handler.InitializeAsync(new AuthenticationScheme(OpdsBasicAuthenticationHandler.SchemeName, null, typeof(OpdsBasicAuthenticationHandler)), ctx);
            await handler.ChallengeAsync(null);
            Assert.Equal(401, ctx.Response.StatusCode);
            Assert.Equal("Basic realm=\"Books\"", ctx.Response.Headers.WWWAuthenticate.ToString());
        }

        public void Dispose()
        {
            provider.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { System.IO.File.Delete(dbPath); } catch (System.IO.IOException) { }
        }
    }
}
