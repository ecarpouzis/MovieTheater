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
        //To-do: Create options class to inject environment information
        private readonly IHostingEnvironment currentEnv;
        private readonly IConfiguration config;

        public Startup(IHostingEnvironment env)
        {
            currentEnv = env;

            IConfigurationBuilder builder = new ConfigurationBuilder().SetBasePath(env.ContentRootPath);
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

                //$"Server={server};Database={database};User Id={username};Password={password};Encrypt=yes;TrustServerCertificate=true;"
                opt.UseSqlServer(conStr);
            });

            var serilog = new LoggerConfiguration()
                .WriteTo.Console(outputTemplate: "[{SourceContext}][{Timestamp:yyyy-MM-dd HH:mm:ss}][{Level:u3}] {Message:l}{NewLine}{Exception}")
                .CreateLogger();

            services.AddLogging(log => log.AddSerilog(logger: serilog));

            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_1);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
            }
            app.UseCors(x => x.AllowAnyMethod().AllowAnyHeader().AllowAnyOrigin());
            app.UseStaticFiles();
            app.UseCookiePolicy();
            app.UseAuthentication();
            app.UseMvcWithDefaultRoute();
            if (currentEnv.IsDevelopment())
            {
                //Disable server cert validation so we can connect and download posters from theater.carpouzis.com which has no cert installed
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

                //Want to add call to LocalImageHandler.Initialize here.
                app.ApplicationServices.GetService<IImageHandler>().Initialize().Wait();
            }
        }
    }
}
