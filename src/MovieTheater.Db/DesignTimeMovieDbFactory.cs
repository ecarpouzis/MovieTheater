using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MovieTheater.Db
{
    /// <summary>
    /// Lets the EF Core CLI (`dotnet ef migrations add` / `database update`) construct a
    /// <see cref="MovieDb"/> at design time, since the runtime constructor needs options
    /// supplied by DI. `migrations add` does not touch the database, so the connection
    /// string only matters for `database update` — supply the real one via the
    /// MOVIESITE_DB environment variable or the `--connection` flag.
    /// </summary>
    public class DesignTimeMovieDbFactory : IDesignTimeDbContextFactory<MovieDb>
    {
        public MovieDb CreateDbContext(string[] args)
        {
            var connectionString =
                Environment.GetEnvironmentVariable("MOVIESITE_DB")
                ?? "Server=localhost;Database=MovieSite;Trusted_Connection=True;TrustServerCertificate=true;";

            var options = new DbContextOptionsBuilder<MovieDb>()
                .UseSqlServer(connectionString, DbServiceExtensions.SqlServerOptions)
                .Options;

            return new MovieDb(options);
        }
    }
}
