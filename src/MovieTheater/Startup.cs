using System;
using System.IO;
using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MovieTheater.Db;
using MovieTheater.Services;
using Serilog;

namespace MovieTheater
{
    public class Startup
    {
        private readonly IConfiguration config;

        public Startup()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder();
            builder.AddJsonFile("appsettings.json");

            var aspEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (!string.IsNullOrEmpty(aspEnv))
            {
                builder.AddJsonFile($"appsettings.{aspEnv}.json");
            }

            config = builder.Build();
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<LocalImageHandlerOptions>(options =>
            {
                string posterImagesPath = config["MoviePostersDir"];

                if (string.IsNullOrEmpty(posterImagesPath))
                {
                    throw new DirectoryNotFoundException("Movie posters directory is invalid. Should be set via environment variable `MOVIE_POSTERSDIR`");
                }

                DirectoryInfo postersDir = new DirectoryInfo(posterImagesPath);

                if (!postersDir.Exists)
                {
                    throw new DirectoryNotFoundException("Movie posters directory is invalid. Should be set via environment variable `MOVIE_POSTERSDIR`");
                }

                string pyPath = config["PY_PATH"];

                if (string.IsNullOrEmpty(pyPath))
                {
                    options.PyPath = "python";
                }
                else
                {
                    options.PyPath = pyPath;
                }

                options.LocalStorageFileDirectory = postersDir.FullName;
            });

            services.AddTransient<IImageHandler, LocalImageHandler>();

            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();

            services.AddDbContext<MovieDb>(opt =>
            {
                var conStr = config["DbConnectionString"];

                if (conStr == null)
                {
                    throw new NullReferenceException("DbConnectionString is null, make sure this is set in your config.");
                }

                opt.UseSqlServer(conStr);
            });

            var serilog = new LoggerConfiguration()
                .WriteTo.Console(outputTemplate: "[{SourceContext}][{Timestamp:yyyy-MM-dd HH:mm:ss}][{Level:u3}] {Message:l}{NewLine}{Exception}")
                .CreateLogger();

            services.AddLogging(log => log.AddSerilog(logger: serilog));

            var proxyBuilder = services.AddReverseProxy();
            proxyBuilder.LoadFromConfig(config.GetSection("ReverseProxy"));

            services.AddMvc();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            app.UseStaticFiles();
            app.UseCookiePolicy();
            app.UseAuthentication();

            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapReverseProxy();
            });
        }
    }
}
