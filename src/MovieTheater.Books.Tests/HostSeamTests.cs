using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieTheater.Books.Db;
using MovieTheater.Books.Identity;
using MovieTheater.Books.Media;
using MovieTheater.BooksHost;
using MovieTheater.BooksHost.Web;
using MovieTheater.Core;

namespace MovieTheater.Books.Tests
{
    /// <summary>The host side of the R5 seam: the identity handler, the ceiling rule, the recorder, the media token and the thumb path confinement.</summary>
    public class HostSeamTests
    {
        private static BooksHostConfiguration Config(params (string key, string? value)[] pairs) =>
            new(new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>("Books:" + p.key, p.value))).Build());

        // ── identity + ceiling ──

        [Fact]
        public void Ceiling_fails_closed_without_a_claim_and_admins_are_unrestricted()
        {
            Assert.Equal(0, BooksIdentity.CeilingFor(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "x") }, "t"))));
            Assert.Equal(2, BooksIdentity.CeilingFor(BooksIdentity.Principal(1, "x", false, 2)));
            Assert.Equal(3, BooksIdentity.CeilingFor(BooksIdentity.Principal(1, "x", true, 0)));
            Assert.Equal(3, BooksIdentity.CeilingFor(BooksIdentity.Principal(1, "x", false, 9)));
        }

        private static async Task<AuthenticateResult> Authenticate(BooksHostConfiguration config, string? header, KnownIdentityRecorder? recorder = null, Action<IServiceCollection>? configure = null)
        {
            var collection = new ServiceCollection().AddLogging();
            configure?.Invoke(collection);
            var services = collection.BuildServiceProvider();
            var ctx = new DefaultHttpContext { RequestServices = services };
            if (header != null) ctx.Request.Headers[BooksIdentityToken.HeaderName] = header;
            var handler = new BooksIdentityAuthHandler(
                Options.Create(new AuthenticationSchemeOptions()) is var o ? new StaticOptionsMonitor(o.Value) : throw new InvalidOperationException(),
                services.GetRequiredService<ILoggerFactory>(), UrlEncoder.Default, config, recorder ?? new KnownIdentityRecorder());
            await handler.InitializeAsync(new AuthenticationScheme(BooksIdentity.AuthenticationScheme, null, typeof(BooksIdentityAuthHandler)), ctx);
            return await handler.AuthenticateAsync();
        }

        private sealed class StaticOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
        {
            public StaticOptionsMonitor(AuthenticationSchemeOptions v) => CurrentValue = v;
            public AuthenticationSchemeOptions CurrentValue { get; }
            public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
        }

        [Fact]
        public async Task The_header_becomes_the_principal_and_its_absence_is_no_result()
        {
            var config = Config(("IdentityTokenSecret", "s3cret"));
            var token = BooksIdentityToken.MintNow("s3cret", 42, "someone", isAdmin: false, maturityCeiling: 1);
            var ok = await Authenticate(config, token);
            Assert.True(ok.Succeeded);
            Assert.Equal(42, BooksIdentity.UserId(ok.Principal!));
            Assert.Equal("someone", BooksIdentity.Username(ok.Principal!));
            Assert.Equal(1, BooksIdentity.CeilingFor(ok.Principal!));
            Assert.False(BooksIdentity.IsAdmin(ok.Principal!));
            Assert.Equal("pwd", ok.Principal!.FindFirst("amr")!.Value);
            Assert.True((await Authenticate(config, null)).None);
            Assert.False((await Authenticate(config, token + "x")).Succeeded);
            Assert.False((await Authenticate(Config(("IdentityTokenSecret", "other")), token)).Succeeded);
        }

        [Fact]
        public async Task An_unconfigured_secret_refuses_everything()
        {
            var r = await Authenticate(Config(), BooksIdentityToken.MintNow("s", 1, "u", false, 3));
            Assert.False(r.Succeeded);
        }

        // ── recorder ──

        [Fact]
        public async Task A_failed_record_is_retried_and_never_fails_authentication()
        {
            // 2026-08-25, production: the first request hit a store that could not open (missing native SQLite),
            // the recorder had memoized BEFORE writing, and every later request skipped the write behind a 200.
            var dir = Path.Combine(Path.GetTempPath(), "books-recorder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var good = Path.Combine(dir, "books.db");
                using (var db = new BooksDb(BooksDbOptions.Hot(good))) db.Database.Migrate();
                var broken = Path.Combine(dir, "missing", "nope", "books.db"); // parent folder absent: cannot open
                var recorder = new KnownIdentityRecorder();
                var payload = new BooksIdentityToken.Payload(7, "u", true, 3, 0);

                var brokenServices = new ServiceCollection();
                brokenServices.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, broken));
                await using (var bp = brokenServices.BuildServiceProvider())
                using (var scope = bp.CreateScope())
                    await Assert.ThrowsAnyAsync<Exception>(() => recorder.RecordAsync(scope.ServiceProvider, payload));
                Assert.False(recorder.LastSeen.ContainsKey(7)); // not memoized as done

                var config = Config(("IdentityTokenSecret", "s3cret"));
                var token = BooksIdentityToken.MintNow("s3cret", 7, "u", isAdmin: true, maturityCeiling: 3);
                var result = await Authenticate(config, token, recorder, sc => sc.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, broken)));
                Assert.True(result.Succeeded); // a broken side effect is a warning, never a 500

                var goodServices = new ServiceCollection();
                goodServices.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, good));
                await using (var gp = goodServices.BuildServiceProvider())
                {
                    using (var scope = gp.CreateScope()) await recorder.RecordAsync(scope.ServiceProvider, payload);
                    using (var scope = gp.CreateScope())
                        Assert.Equal("u", scope.ServiceProvider.GetRequiredService<BooksDb>().KnownIdentities.Single(k => k.UserId == 7).Username);
                }
                Assert.True(recorder.LastSeen.ContainsKey(7));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public async Task The_recorder_writes_once_per_change()
        {
            var dir = Path.Combine(Path.GetTempPath(), "books-recorder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "books.db");
                using (var db = new BooksDb(BooksDbOptions.Hot(path))) db.Database.Migrate();
                var services = new ServiceCollection();
                services.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, path));
                await using var provider = services.BuildServiceProvider();
                var recorder = new KnownIdentityRecorder();
                var p1 = new BooksIdentityToken.Payload(1, "u", false, 3, 0);
                using (var scope = provider.CreateScope()) await recorder.RecordAsync(scope.ServiceProvider, p1);
                using (var scope = provider.CreateScope()) await recorder.RecordAsync(scope.ServiceProvider, p1);
                using (var scope = provider.CreateScope()) await recorder.RecordAsync(scope.ServiceProvider, p1 with { MaturityCeiling = 1 });
                using (var scope = provider.CreateScope())
                {
                    var rows = scope.ServiceProvider.GetRequiredService<BooksDb>().KnownIdentities.ToList();
                    Assert.Single(rows);
                    Assert.Equal(1, rows[0].MaturityCeiling);
                }
                Assert.Equal((false, 1), (recorder.LastSeen[1].IsAdmin, recorder.LastSeen[1].Ceiling));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        // ── media plane ──

        [Fact]
        public void Media_token_round_trips_with_a_strict_expiry_and_a_fixed_scope()
        {
            var token = BooksMediaToken.MintNow("m", 7, 2, isAdmin: false, out var exp);
            Assert.True(BooksMediaToken.TryValidate("m", token, out var p));
            Assert.Equal((7, 2, false, "read", exp), (p!.UserId, p.MaturityCeiling, p.IsAdmin, p.Scope, p.ExpiresUnixSeconds));
            Assert.False(BooksMediaToken.TryValidate("x", token, out _));
            var expired = BooksMediaToken.Mint("m", new BooksMediaToken.Payload(7, 2, false, "read", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1));
            Assert.False(BooksMediaToken.TryValidate("m", expired, out _));
            var wrongScope = BooksMediaToken.Mint("m", new BooksMediaToken.Payload(7, 2, false, "write", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60));
            Assert.False(BooksMediaToken.TryValidate("m", wrongScope, out _));
            // an identity token is not a media token even though both are five fields
            Assert.False(BooksMediaToken.TryValidate("m", BooksIdentityToken.MintNow("m", 7, "u", false, 2), out _));
        }

        [Fact]
        public void Thumb_paths_are_confined_to_the_cache()
        {
            var dir = Path.Combine(Path.GetTempPath(), "books-thumbs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.Equal(Path.Combine(Path.GetFullPath(dir), "12.webp"), BooksMediaRoutes.ResolveThumb(dir, "12"));
                Assert.Null(BooksMediaRoutes.ResolveThumb(dir, "../12"));
                Assert.Null(BooksMediaRoutes.ResolveThumb(dir, "12/../../x"));
                Assert.Null(BooksMediaRoutes.ResolveThumb(dir, "0"));
                Assert.Null(BooksMediaRoutes.ResolveThumb(dir, "abc"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Media_urls_are_built_from_the_public_base()
        {
            Assert.Equal("https://h.example/m/T/thumbs/5.webp", BooksMediaRoutes.ThumbUrl("https://h.example/", "T", 5));
            Assert.Equal("https://h.example/m/T/pages/5/3", BooksMediaRoutes.PageUrl("https://h.example", "T", 5, 3));
            Assert.Equal("https://h.example/m/T/epub/5/OEBPS/ch1.xhtml", BooksMediaRoutes.EpubResourceUrl("https://h.example", "T", 5, "/OEBPS/ch1.xhtml"));
        }
    }
}
