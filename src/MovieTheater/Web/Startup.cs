using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Db;
using Microsoft.AspNetCore.OData;
using MovieTheater.Services;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Poster;
using MovieTheater.Services.Python;
using MovieTheater.Services.Tmdb;
using System;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace MovieTheater
{
    public class Startup
    {
        private readonly MovieTheaterConfiguration config;

        public Startup(MovieTheaterConfiguration config)
        {
            this.config = config;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Persist the Data Protection key ring (which encrypts the auth cookie) to durable storage.
            // Without this the keys live in the container's ephemeral filesystem, so every redeploy or
            // pod restart mints new keys and invalidates every existing cookie — signing all users out.
            var keysDir = new System.IO.DirectoryInfo(ResolveDataProtectionKeysDir());
            keysDir.Create(); // no-op if it already exists
            services.AddDataProtection()
                // Stable name so the key ring is shared across pods/restarts rather than scoped per instance.
                .SetApplicationName("MovieTheater")
                .PersistKeysToFileSystem(keysDir);

            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = "/login";
                    options.ExpireTimeSpan = TimeSpan.FromDays(30);
                    options.SlidingExpiration = true;
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    // SameAsRequest so local HTTP dev still works; production is HTTPS-only so the cookie is Secure there.
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

                    // The SPA expects real status codes from API endpoints, not redirects to an HTML login page.
                    options.Events.OnRedirectToLogin = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/API") || context.Request.Path.StartsWithSegments("/odata"))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return System.Threading.Tasks.Task.CompletedTask;
                        }
                        context.Response.Redirect(context.RedirectUri);
                        return System.Threading.Tasks.Task.CompletedTask;
                    };
                    options.Events.OnRedirectToAccessDenied = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/API") || context.Request.Path.StartsWithSegments("/odata"))
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return System.Threading.Tasks.Task.CompletedTask;
                        }
                        context.Response.Redirect(context.RedirectUri);
                        return System.Threading.Tasks.Task.CompletedTask;
                    };

                    // Bounded revocation for streaming (§3.1): cookies live 30 days, so a
                    // session claiming amr=pwd is re-checked against the DB every ~5 minutes.
                    // If the account no longer has a password (or is gone), the principal is
                    // rejected and any in-progress stream dies at the next authorized call.
                    options.Events.OnValidatePrincipal = async context =>
                    {
                        if (context.Principal?.FindFirst("amr")?.Value != "pwd")
                            return;

                        const string checkedAtKey = "amrCheckedAt";
                        var checkedAt = context.Properties.GetString(checkedAtKey);
                        if (checkedAt != null
                            && DateTimeOffset.TryParse(checkedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                            && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(5))
                            return;

                        var idClaim = context.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                        bool stillValid = false;
                        if (int.TryParse(idClaim, out var userId))
                        {
                            var db = context.HttpContext.RequestServices.GetRequiredService<MovieDb>();
                            var passwordHash = await db.Users
                                .Where(u => u.UserID == userId)
                                .Select(u => u.PasswordHash)
                                .FirstOrDefaultAsync();
                            stillValid = passwordHash != null;
                        }

                        if (!stillValid)
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                            return;
                        }

                        context.Properties.SetString(checkedAtKey, DateTimeOffset.UtcNow.ToString("O"));
                        context.ShouldRenew = true; // persist the check timestamp into the cookie
                    };
                });

            // One policy guards every streaming surface (§3.1): authenticated AND this
            // session verified a password. Claim check is in-memory — no DB on the hot path.
            services.AddAuthorization(options =>
            {
                options.AddPolicy("StreamingUser", policy =>
                    policy.RequireAuthenticatedUser().RequireClaim("amr", "pwd"));
            });

            var proxyBuilder = services.AddReverseProxy();
            proxyBuilder.LoadFromConfig(config.RawConfiguration.GetSection("ReverseProxy"));

            services.AddMemoryCache(opts => opts.SizeLimit = 200 * 1024 * 1024); // 200 MB cap, evicts LRU when full

            services.AddScoped<Channels.ChannelScheduleService>();
            services.AddSingleton<Channels.ChannelSkipService>();

            services.AddHostedService<BoardgameSimilarityStartupService>();
            services.AddHostedService<PlaceholderPosterCleanupStartupService>();

            services.AddMvc()
                .AddJsonOptions(opts =>
                {
                    var enumConverter = new JsonStringEnumConverter();
                    opts.JsonSerializerOptions.Converters.Add(enumConverter);
                    opts.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
                    opts.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString;
                })
                .AddOData(opts => opts.Select().Filter().OrderBy().Expand().SetMaxTop(null));
        }

        // Where the Data Protection key ring lives. Prefer an explicit config value; otherwise put it on
        // the same persistent mount as the posters (which already survives redeploys in prod). Falls back
        // to a local folder for dev, where key persistence across restarts doesn't matter.
        private string ResolveDataProtectionKeysDir()
        {
            if (!string.IsNullOrWhiteSpace(config.DataProtectionKeysDir))
                return config.DataProtectionKeysDir;

            if (!string.IsNullOrWhiteSpace(config.MoviePostersDir))
            {
                var postersDir = new System.IO.DirectoryInfo(config.MoviePostersDir);
                var mountRoot = postersDir.Parent?.FullName ?? postersDir.FullName;
                return System.IO.Path.Combine(mountRoot, "dataprotection-keys");
            }

            return System.IO.Path.Combine(AppContext.BaseDirectory, "dataprotection-keys");
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            // CRITICAL: Don't intercept .well-known paths - let them 404 so cert-manager's ingress can handle ACME challenges
            // We catch all /.well-known/* paths to ensure ACME challenges work, even though we only care about /.well-known/acme-challenge/*
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/.well-known"))
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("Not found - .well-known paths should be handled by cert-manager or other ingress rules");
                    return; // Don't call next() - stop the pipeline here and prevent reverse proxy from running
                }
                await next();
            });

            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapReverseProxy();
            });
        }
    }
}
