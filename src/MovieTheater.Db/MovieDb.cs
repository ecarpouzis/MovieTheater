using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    public class MovieDb : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Boardgame>()
                .HasOne(b => b.BaseGame)
                .WithMany(b => b.Expansions)
                .HasForeignKey(b => b.BaseGameId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);
        }

        public DbSet<Movie> Movies { get; set; }
        public DbSet<MoviePosterDetails> MoviePosterDetails { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Viewing> Viewings { get; set; }
        public DbSet<RatingMap> RatingMaps { get; set; }
        public DbSet<RatingMPA> RatingMpas { get; set; }
        public DbSet<UserSettings> UserSettings { get; set; }
        public DbSet<Boardgame> Boardgames { get; set; }
        public DbSet<BoardgameImageDetails> BoardgameImageDetails { get; set; }
        public DbSet<BoardgameExtraDetails> BoardgameExtraDetails { get; set; }

        public MovieDb(DbContextOptions<MovieDb> options)
            : base(options)
        {
        }
    }
}
