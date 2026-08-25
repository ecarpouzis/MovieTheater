using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Db
{
    public static class DbServiceExtensions
    {
        public static IServiceCollection AddMovieTheaterDb(this IServiceCollection services, string? sqlServerConnectionString)
        {
            services.AddDbContext<MovieDb>(opt => opt.UseSqlServer(sqlServerConnectionString ?? throw new ArgumentNullException(sqlServerConnectionString), SqlServerOptions));
            services.AddPooledDbContextFactory<MovieDb>(x => x.UseSqlServer(sqlServerConnectionString ?? throw new ArgumentNullException(sqlServerConnectionString), SqlServerOptions));

            return services;
        }

        /// <summary>
        /// Provider options shared by the scoped context, the pooled factory and the design-time
        /// factory. EF Core 10 changed the default translation of parameterized collections
        /// (`ids.Contains(x.Id)`) from one JSON array parameter (OPENJSON) to one scalar parameter
        /// per element. Several browse paths take user-sized id lists (Seen / Want dense searches,
        /// GetMoviesByIds), which would hit SQL Server's 2,100-parameter cap, and the JSON shape is
        /// the plan the live database has been running since EF 8 — so the EF 8 behaviour is kept
        /// explicitly. Opt individual queries into the new mode with EF.MultipleParameters(...).
        /// </summary>
        public static void SqlServerOptions(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder o)
            => o.UseParameterizedCollectionMode(ParameterTranslationMode.Parameter);
    }
}
