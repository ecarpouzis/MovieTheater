using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Db;

namespace MovieTheater
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;

        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            services.AddDbContext<MovieDb>(opt =>
            {
                var server = Environment.GetEnvironmentVariable("MOVIE_DBSERVER");
                var database = Environment.GetEnvironmentVariable("MOVIE_DATABASE");
                var username = Environment.GetEnvironmentVariable("MOVIE_DBUSER");
                var password = Environment.GetEnvironmentVariable("MOVIE_DBPASSWORD");

                if(server == null || database == null || username == null || password == null)
                {
                    throw new NullReferenceException("One of your database environment variables is set incorrectly. Ensure these are set to the proper connection details.");
                }

                opt.UseSqlServer($"Server={server};Database={database};User Id={username};Password={password};Encrypt=yes;TrustServerCertificate=true;");
            });

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

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Movie}/{action=Home}");
            });
        }
    }
}
