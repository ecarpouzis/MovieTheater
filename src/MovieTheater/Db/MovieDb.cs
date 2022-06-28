using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    public class MovieDb : DbContext
    {
        public DbSet<Movie> Movies { get; set; }
        public DbSet<MoviePoster> MoviePosters { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Viewing> Viewings { get; set; }

        public MovieDb(DbContextOptions<MovieDb> options)
            : base(options)
        {

        }
    }
}
