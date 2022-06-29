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
using MovieTheater.Services.Poster;
using Serilog;

namespace MovieTheater
{
    public class Startup
    {
        private readonly IConfiguration config;
        private readonly HostedEnvironment environment;

        public Startup()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder();
            builder.AddJsonFile("appsettings.json");

            var aspEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (!string.IsNullOrEmpty(aspEnv))
            {
                builder.AddJsonFile($"appsettings.{aspEnv}.json");
            }

            if (aspEnv == "Production")
                environment = HostedEnvironment.Production;
            else
                environment = HostedEnvironment.Development;

            config = builder.Build();
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMovieLogging();
            services.AddMovieTheaterDb(config["DbConnectionString"]);
            services.AddPosterImageServices(config["MoviePostersDir"], environment);

            var proxyBuilder = services.AddReverseProxy();
            proxyBuilder.LoadFromConfig(config.GetSection("ReverseProxy"));

            services.AddMvc();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapReverseProxy();
            });
        }
    }
}
