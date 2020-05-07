using System;
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

namespace MovieTheater
{
    public class Startup
    {
        private IHostingEnvironment currentEnv;

        public Startup(IHostingEnvironment env)
        {
            IConfigurationBuilder builder = new ConfigurationBuilder().SetBasePath(env.ContentRootPath);
            builder.AddJsonFile("appsettings.json");

            currentEnv = env;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<LocalImageHandlerOptions>(options =>
            {
                string posterImagesPath = Environment.GetEnvironmentVariable("MOVIE_POSTERSDIR");

                if (string.IsNullOrEmpty(posterImagesPath))
                {
                    throw new System.IO.DirectoryNotFoundException("Movie posters directory is invalid. Should be set via environment variable `MOVIE_POSTERSDIR`");
                }

                System.IO.DirectoryInfo postersDir = new System.IO.DirectoryInfo(posterImagesPath);

                if (!postersDir.Exists)
                {
                    throw new System.IO.DirectoryNotFoundException("Movie posters directory is invalid. Should be set via environment variable `MOVIE_POSTERSDIR`");
                }

                string pyPath = Environment.GetEnvironmentVariable("PY_PATH");

                if (!string.IsNullOrEmpty(pyPath))
                {
                    options.PyPath = pyPath;
                }
                else
                {
                    options.PyPath = "python";
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
                var server = Environment.GetEnvironmentVariable("MOVIE_DBSERVER");
                var database = Environment.GetEnvironmentVariable("MOVIE_DATABASE");
                var username = Environment.GetEnvironmentVariable("MOVIE_DBUSER");
                var password = Environment.GetEnvironmentVariable("MOVIE_DBPASSWORD");


                if (server == null || database == null || username == null || password == null)
                {
                    throw new NullReferenceException("One of your database environment variables is set incorrectly. Ensure these are set to the proper connection details.");
                }

                opt.UseSqlServer($"Server={server};Database={database};User Id={username};Password={password};Encrypt=yes;TrustServerCertificate=true;");
            });

            services.AddLogging(log => log.AddConsole());
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
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseCookiePolicy();
            app.UseAuthentication();
            app.UseMvcWithDefaultRoute();
        }
    }
}
