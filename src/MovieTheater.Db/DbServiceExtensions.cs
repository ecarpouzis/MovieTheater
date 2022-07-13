using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Db
{
    public static class DbServiceExtensions
    {
        public static IServiceCollection AddMovieTheaterDb(this IServiceCollection services, string? sqlServerConnectionString)
        {
            services.AddDbContext<MovieDb>(opt => opt.UseSqlServer(sqlServerConnectionString ?? throw new ArgumentNullException(sqlServerConnectionString)));
            services.AddPooledDbContextFactory<MovieDb>(x => x.UseSqlServer(sqlServerConnectionString ?? throw new ArgumentNullException(sqlServerConnectionString)));

            return services;
        }
    }
}
