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
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

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
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = "/login";
                    options.ExpireTimeSpan = TimeSpan.FromDays(30);
                    options.SlidingExpiration = true;
                });

            var proxyBuilder = services.AddReverseProxy();
            proxyBuilder.LoadFromConfig(config.RawConfiguration.GetSection("ReverseProxy"));

            services.AddMemoryCache(opts => opts.SizeLimit = 200 * 1024 * 1024); // 200 MB cap, evicts LRU when full

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
