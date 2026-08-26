using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using MovieTheater.Books;
using MovieTheater.Books.Identity;
using MovieTheater.BooksHost.Web;
using MovieTheater.Core.Logging;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>web</c> — the service verb (nssm runs <c>MovieTheater.BooksHost.exe web</c>). Minimal hosting like the
    /// gateways: the identity scheme is the ONLY authentication, a fallback policy makes every endpoint require
    /// it unless it opts out (healthz, the media plane), CORS is the site origin only, and the catalog opens
    /// through the one <see cref="BooksDbOptions"/> opener when a path is configured.
    /// </summary>
    [Command("web", Description = "Run the Books host service.")]
    public class WebCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public WebCommand(BooksHostConfiguration config) => this.config = config;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (string.IsNullOrEmpty(config.IdentityTokenSecret)) throw new CommandException("Books:IdentityTokenSecret is required to run the host.");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(config.Urls ?? "http://localhost:2204");
            builder.Services.AddMovieTheaterLogging();
            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton<KnownIdentityRecorder>();
            // R6: the catalog surface — controllers, the projection, the browse caches and the change-driven
            // warmer — all live in MovieTheater.Books. AddBooks registers the BooksDb from the same path the R5
            // wiring used, so the host has one place a file is opened.
            builder.Services.AddBooks(new BooksOptions
            {
                DbPath = config.DbPath,
                // R6 slice 2: the readers, the thumbnail service and the URL builders in the item payloads all
                // live in the library, so the host's Books: settings are handed across here rather than reached
                // for from inside it. A null path means the feature degrades, never that startup fails.
                PublicBaseUrl = config.PublicBaseUrl,
                MediaTokenSecret = config.MediaTokenSecret,
                CacheDir = config.CacheDir,
                ArchiveCacheDir = config.ArchiveCacheDir,
                ArchiveCacheGb = config.ArchiveCacheGb,
                PageJpegQuality = config.PageJpegQuality,
                PageCacheLimitMb = config.PageCacheLimitMb,
                ThumbnailQuality = config.ThumbnailQuality,
                SevenZipPath = config.SevenZipPath,
                EnableTextRegions = config.EnableTextRegions,
                // R6 slice 5: the admin surface and the provider scrapers. The legs file is what the tag folds
                // and the provider response cache read; the ComicVine key is plain config (no key vault); the
                // overlay is the one file an admin's config PUT may write.
                LegsDbPath = config.LegsDbPath,
                ComicVineApiKey = config.ComicVineApiKey,
                SettingsOverlayPath = config.SettingsOverlayPath,
                CalibreLinkPath = config.CalibreLinkPath,
            });

            // The admin log panel is a ring buffer fed by an ILoggerProvider registered ALONGSIDE the console
            // one — never instead of it, so the host's own log file keeps everything the tail drops.
            var logStore = new MovieTheater.Books.Services.InMemoryLogStore();
            builder.Services.AddSingleton(logStore);
            builder.Logging.AddProvider(new MovieTheater.Books.Services.InMemoryLoggerProvider(logStore));

            builder.Services.AddAuthentication(BooksIdentity.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, BooksIdentityAuthHandler>(BooksIdentity.AuthenticationScheme, _ => { });
            builder.Services.AddAuthorization(options =>
            {
                // everything needs the site's identity unless it says otherwise (healthz, /m/**)
                options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(BooksIdentity.AuthenticationScheme)
                    .RequireAuthenticatedUser().Build();
                options.AddPolicy("admin", p => p.RequireRole(BooksIdentity.AdminRole));
            });

            var app = builder.Build();
            app.UseHostCors(config.SiteOrigin ?? "https://localhost");
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapHostEndpoints(config);
            app.MapBooks();

            await console.Output.WriteLineAsync($"Books host listening on {config.Urls ?? "http://localhost:2204"}; catalog {(config.DbPath ?? "(none)")}; media {(config.MediaTokenSecret == null ? "off" : "on")}");
            await app.RunAsync();
        }
    }
}
