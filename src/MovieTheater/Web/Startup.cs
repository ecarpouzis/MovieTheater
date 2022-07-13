using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Db;
using MovieTheater.Gql;
using MovieTheater.Services;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Poster;
using MovieTheater.Services.Python;
using MovieTheater.Services.Tmdb;
using System;

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
            services.AddMovieTheaterGql();

            var proxyBuilder = services.AddReverseProxy();
            proxyBuilder.LoadFromConfig(config.RawConfiguration.GetSection("ReverseProxy"));

            services.AddMvc();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapGraphQL();
                endpoints.MapReverseProxy();
            });
        }
    }
}
