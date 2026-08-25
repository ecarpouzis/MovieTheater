using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Db;
using MovieTheater.Photos;
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
        // Dev runs plain HTTP on localhost; every other environment is served over HTTPS via the
        // TLS-terminating ingress. Used to decide whether the auth cookie must carry Secure.
        private static bool IsDevelopment =>
            string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Development",
                StringComparison.OrdinalIgnoreCase);

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
                    // Always Secure outside dev. TLS terminates at the ingress and HTTP is forwarded to
                    // Kestrel, so Request.IsHttps is false inside the app and SameAsRequest would emit the
                    // 30-day auth cookie WITHOUT Secure in prod. Force it on everywhere but local HTTP dev.
                    options.Cookie.SecurePolicy = IsDevelopment
                        ? CookieSecurePolicy.SameAsRequest
                        : CookieSecurePolicy.Always;

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

                // Family photo album (photos-plan.md §2.1). Unlike StreamingUser this one cannot be a
                // pure claim check: membership is a UserSettings row that an admin can revoke, and a
                // 30-day cookie would otherwise carry a stale grant for a month. So it reads the flag
                // per request — see FamilyAlbumGate for the memoization that keeps that to one query.
                Photos.FamilyAlbumGate.AddPolicy(options);
            });
            services.AddFamilyAlbumServices();
            // Family video playback (photos-plan.md §2.3): mints a gateway capability for ONE video
            // behind the gate above. Scoped like the controller that uses it; a host with no Jellyfin
            // still resolves it and simply reports itself unconfigured, which the UI renders as no play
            // button rather than a button that 501s.
            services.AddScoped<Photos.IPhotoVideoPlayback, Photos.JellyfinPhotoVideoPlayback>();

            var proxyBuilder = services.AddReverseProxy();
            proxyBuilder.LoadFromConfig(config.RawConfiguration.GetSection("ReverseProxy"));

            services.AddMemoryCache(opts => opts.SizeLimit = 200 * 1024 * 1024); // 200 MB cap, evicts LRU when full

            // Compress API JSON (the channel guide is large) so a slow mobile connection isn't left showing
            // "Updating…" while an uncompressed body downloads. JSON only — the SPA assets are served (and
            // compressed) by the UI container, so this doesn't wrap the reverse proxy's responses.
            services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true; // guide/browse JSON is public and reflects no secret (BREACH n/a)
                options.MimeTypes = new[] { "application/json" };
            });

            // The sync job's last mile. Registered against the interface too, so the runner in the
            // Services assembly can finish the whole operation without that assembly knowing about
            // the title cascade, the normalizers or the poster store.
            services.AddScoped<Ingest.SyncCandidateResolver>();
            services.AddScoped<MovieTheater.Services.Jellyfin.ISyncCandidateResolver>(
                sp => sp.GetRequiredService<Ingest.SyncCandidateResolver>());

            services.AddScoped<Channels.ChannelScheduleService>();
            services.AddSingleton<Channels.ChannelSkipService>();
            // Durable channel-viewing telemetry: the /Now poll records beats in memory; this service
            // flushes one ChannelViewStat row per user/channel/day every few minutes. Registered as
            // both the injectable accumulator and the background flusher (same instance).
            services.AddSingleton<Channels.ChannelViewTelemetryService>();
            services.AddHostedService(sp => sp.GetRequiredService<Channels.ChannelViewTelemetryService>());
            // Watch-party lobby state (presence + ready) is in-memory like the skip service; the reaper deletes
            // finished parties (docs/playlists-watchparty-plan.md).
            services.AddSingleton<Channels.WatchpartyService>();
            services.AddHostedService<Channels.WatchpartyReaperService>();
            // Live arcade room state (seats, bind, presence) is in-memory + shared across requests, like the
            // channel skip service; the reaper closes out rooms that have gone empty (ArcadeSession.EndedUtc).
            services.AddSingleton<Arcade.ArcadeRoomService>();
            services.AddHostedService<Arcade.ArcadeRoomReaperService>();
            // Tracks active play sessions for the streaming concurrency guard (Jellyfin's /Sessions can't
            // tell our viewers apart — they share one DeviceId). Singleton so it spans all requests.
            services.AddSingleton<Streaming.TranscodeSessionRegistry>();
            // Materializes channel schedules + warms rating-ceiling caches in bounded background batches,
            // so the viewer read paths (List / Now / grid guide) stay cheap as channel count grows.
            services.AddHostedService<Channels.ChannelScheduleMaintenanceService>();

            // Keeps each user's personalized "For You" recommendations fresh in bounded background batches.
            // Staleness is detected from the user's rating stamp, so a new rating refreshes within a couple
            // of minutes with no explicit wiring from the rate endpoint.
            services.AddHostedService<Recommendations.RecommendationMaintenanceService>();

            services.AddHostedService<BoardgameSimilarityStartupService>();

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
            // Honor X-Forwarded-Proto/-For from the ingress so Request.IsHttps and the client IP reflect
            // the original TLS request rather than the plaintext hop to Kestrel. The pod is only reachable
            // through the ingress, so we trust the header (clear the default loopback-only restriction).
            var forwardedOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
            {
                ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                    | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor,
            };
            forwardedOptions.KnownIPNetworks.Clear();
            forwardedOptions.KnownProxies.Clear();
            app.UseForwardedHeaders(forwardedOptions);

            // Baseline security headers on every response.
            app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "SAMEORIGIN";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                if (!IsDevelopment)
                    headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                await next();
            });

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

            app.UseResponseCompression();
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
