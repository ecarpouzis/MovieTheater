using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using MovieTheater.Books.Db;
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
            if (config.DbPath != null)
                builder.Services.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, config.DbPath));

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

            await console.Output.WriteLineAsync($"Books host listening on {config.Urls ?? "http://localhost:2204"}; catalog {(config.DbPath ?? "(none)")}; media {(config.MediaTokenSecret == null ? "off" : "on")}");
            await app.RunAsync();
        }
    }
}
