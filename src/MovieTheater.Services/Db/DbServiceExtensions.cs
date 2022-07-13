using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace MovieTheater.Db
{
    public static class DbServiceExtensions
    {
        public static IServiceCollection AddMovieTheaterDb(this IServiceCollection services, string? sqlServerConnectionString)
        {
            if (string.IsNullOrEmpty(sqlServerConnectionString))
            {
                throw new ArgumentNullException(nameof(sqlServerConnectionString));
            }

            services.AddDbContext<MovieDb>(opt => opt.UseSqlServer(sqlServerConnectionString));
            services.AddPooledDbContextFactory<MovieDb>(x => x.UseSqlServer(sqlServerConnectionString));

            return services;
        }
    }
}
