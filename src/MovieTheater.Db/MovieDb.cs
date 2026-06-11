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

            // A person can hold the same role on a movie only once.
            modelBuilder.Entity<MovieCredit>()
                .HasIndex(c => new { c.MovieID, c.PersonId, c.Role })
                .IsUnique();

            modelBuilder.Entity<MovieCredit>()
                .HasOne(c => c.Movie)
                .WithMany(m => m.Credits)
                .HasForeignKey(c => c.MovieID)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MovieCredit>()
                .HasOne(c => c.Person)
                .WithMany(p => p.Credits)
                .HasForeignKey(c => c.PersonId)
                .OnDelete(DeleteBehavior.Restrict);

            // Composite key for the Movie<->Genre join.
            modelBuilder.Entity<MovieGenre>()
                .HasKey(mg => new { mg.MovieID, mg.GenreId });

            modelBuilder.Entity<MovieGenre>()
                .HasOne(mg => mg.Movie)
                .WithMany(m => m.MovieGenres)
                .HasForeignKey(mg => mg.MovieID)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MovieGenre>()
                .HasOne(mg => mg.Genre)
                .WithMany(g => g.MovieGenres)
                .HasForeignKey(mg => mg.GenreId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MoviePlotSummary>()
                .HasOne(s => s.Movie)
                .WithMany(m => m.PlotSummaries)
                .HasForeignKey(s => s.MovieID)
                .OnDelete(DeleteBehavior.Cascade);
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
        public DbSet<Person> People { get; set; }
        public DbSet<MovieCredit> MovieCredits { get; set; }
        public DbSet<Genre> Genres { get; set; }
        public DbSet<MovieGenre> MovieGenres { get; set; }
        public DbSet<MoviePlotSummary> MoviePlotSummaries { get; set; }

        public MovieDb(DbContextOptions<MovieDb> options)
            : base(options)
        {
        }
    }
}
