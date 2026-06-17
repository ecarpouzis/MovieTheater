using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MovieTheater.Db
{
    public class MovieDb : DbContext
    {
        // SQL Server's datetime2 carries no timezone, so EF materializes these columns with
        // Kind=Unspecified — which System.Text.Json then serializes without a trailing 'Z',
        // making the browser read a UTC instant as local time. Stamp Kind=Utc on read so the
        // channel schedule times round-trip correctly to the client.
        private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
            new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Speeds both the review-queue read (ReviewBatch IS NOT NULL) and the browse
            // quarantine filter (ReviewBatch IS NULL) that now guards every public movie query.
            modelBuilder.Entity<Movie>()
                .HasIndex(m => m.ReviewBatch);

            // Browse list ordering. Every public movie list (type / rating / title / genre / letter /
            // person) is ORDER BY SimpleTitle, id over the un-quarantined set, then paged with
            // OFFSET/FETCH. A filtered composite index matching that predicate + sort lets each
            // infinite-scroll page seek and range-scan in order, instead of re-sorting the whole
            // table on every page fetch. The mode-specific predicates (NormalizedTitleType, age gate)
            // stay residual — the Movies bucket dominates the table, so little is scanned past.
            modelBuilder.Entity<Movie>()
                .HasIndex(m => new { m.SimpleTitle, m.id })
                .HasFilter("[ReviewBatch] IS NULL");

            // Coarse Browse "Type" bucket, derived from TitleType in the database so it is always
            // correct with no app-side syncing. Short/TvShort (2,3) ⇒ Short (2); everything else ⇒
            // Movies (0). Series-typed rows are excluded from public movie queries and live in the
            // Series table, so a Movie row never needs the Series/Misc buckets. Mirrors
            // TitleTypeExtensions.Normalize — keep the two in sync.
            modelBuilder.Entity<Movie>()
                .Property(m => m.NormalizedTitleType)
                .HasComputedColumnSql("CASE WHEN [TitleType] IN (2, 3) THEN 2 ELSE 0 END", stored: true);

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

            // ── Phase-4 cutover: files / progress / schedule attach to a Playable (movie or episode) ──
            // Movie ↔ Playable is 1:1 (Movie holds the FK); deleting a Playable is Restricted so a
            // movie/episode is never silently removed — reject flows delete the title then its Playable.
            modelBuilder.Entity<Movie>()
                .HasOne(m => m.Playable)
                .WithOne()
                .HasForeignKey<Movie>(m => m.PlayableId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MediaFile>()
                .HasIndex(f => f.PlayableId);

            modelBuilder.Entity<MediaFile>()
                .HasOne(f => f.Playable)
                .WithMany(p => p.Files)
                .HasForeignKey(f => f.PlayableId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Episode>()
                .HasIndex(e => new { e.SeriesMovieId, e.SeasonNumber, e.EpisodeNumber })
                .IsUnique();

            // Legacy link to the series' old Movie row (dropped at the Series-split flip).
            modelBuilder.Entity<Episode>()
                .HasOne(e => e.SeriesMovie)
                .WithMany()
                .HasForeignKey(e => e.SeriesMovieId)
                .OnDelete(DeleteBehavior.Cascade);

            // Canonical link to the standalone Series (Restrict avoids a 2nd cascade path into Episode).
            modelBuilder.Entity<Episode>()
                .HasOne(e => e.Series)
                .WithMany(s => s.Episodes)
                .HasForeignKey(e => e.SeriesId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Episode>()
                .HasOne(e => e.Playable)
                .WithOne()
                .HasForeignKey<Episode>(e => e.PlayableId)
                .OnDelete(DeleteBehavior.Restrict);

            // ── Series: a first-class title, peer of Movie (replaces its old aggregate-only form) ──
            modelBuilder.Entity<Series>()
                .HasIndex(s => s.ReviewBatch);

            // Series peer of the Movie browse-ordering index: the merged movie+series modes order each
            // table's keys by (SimpleTitle, Id) before the union, so the same filtered composite serves
            // the series side of every paged browse/search.
            modelBuilder.Entity<Series>()
                .HasIndex(s => new { s.SimpleTitle, s.Id })
                .HasFilter("[ReviewBatch] IS NULL");

            modelBuilder.Entity<SeriesGenre>()
                .HasKey(sg => new { sg.SeriesId, sg.GenreId });
            modelBuilder.Entity<SeriesGenre>()
                .HasOne(sg => sg.Series).WithMany(s => s.SeriesGenres)
                .HasForeignKey(sg => sg.SeriesId).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<SeriesGenre>()
                .HasOne(sg => sg.Genre).WithMany()
                .HasForeignKey(sg => sg.GenreId).OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SeriesCredit>()
                .HasIndex(c => new { c.SeriesId, c.PersonId, c.Role }).IsUnique();
            modelBuilder.Entity<SeriesCredit>()
                .HasOne(c => c.Series).WithMany(s => s.Credits)
                .HasForeignKey(c => c.SeriesId).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<SeriesCredit>()
                .HasOne(c => c.Person).WithMany()
                .HasForeignKey(c => c.PersonId).OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<SeriesPlotSummary>()
                .HasOne(s => s.Series).WithMany(x => x.PlotSummaries)
                .HasForeignKey(s => s.SeriesId).OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SeriesPosterDetails>()
                .HasOne(p => p.Series).WithOne(s => s.PosterDetails)
                .HasForeignKey<SeriesPosterDetails>(p => p.SeriesId).OnDelete(DeleteBehavior.Cascade);

            // A viewing targets a Movie OR a Series; Restrict avoids a multiple-cascade-path error from User.
            modelBuilder.Entity<Viewing>()
                .HasOne(v => v.Series).WithMany(s => s.Viewings)
                .HasForeignKey(v => v.SeriesId).OnDelete(DeleteBehavior.Restrict);

            // A misc video owns its Playable (Restrict, like Episode) and may relate to a Movie OR a
            // Series via two typed FKs (a bare id is ambiguous; see MiscVideo). Both relations Restrict
            // so reclassifying/removing a title never silently drops a misc video pointing at it.
            modelBuilder.Entity<MiscVideo>()
                .HasOne(mv => mv.Playable)
                .WithOne()
                .HasForeignKey<MiscVideo>(mv => mv.PlayableId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MiscVideo>()
                .HasOne(mv => mv.RelatedMovie)
                .WithMany()
                .HasForeignKey(mv => mv.RelatedMovieId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MiscVideo>()
                .HasOne(mv => mv.RelatedSeries)
                .WithMany()
                .HasForeignKey(mv => mv.RelatedSeriesId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MiscVideo>()
                .HasIndex(mv => mv.ReviewBatch);

            modelBuilder.Entity<MiscVideo>()
                .HasIndex(mv => mv.CollectionName);

            modelBuilder.Entity<MoviePlaybackProgress>()
                .HasIndex(p => new { p.UserID, p.PlayableId })
                .IsUnique();

            // Restrict on User to avoid SQL Server multiple-cascade-path errors
            // (Viewings already cascade from User in the live schema's spirit).
            modelBuilder.Entity<MoviePlaybackProgress>()
                .HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserID)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MoviePlaybackProgress>()
                .HasOne(p => p.Playable)
                .WithMany()
                .HasForeignKey(p => p.PlayableId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ChannelScheduleItem>()
                .HasIndex(i => new { i.ChannelId, i.StartUtc });

            modelBuilder.Entity<ChannelScheduleItem>()
                .Property(i => i.StartUtc).HasConversion(UtcConverter);
            modelBuilder.Entity<ChannelScheduleItem>()
                .Property(i => i.EndUtc).HasConversion(UtcConverter);
            modelBuilder.Entity<Channel>()
                .Property(c => c.AnchorUtc).HasConversion(UtcConverter);

            modelBuilder.Entity<ChannelScheduleItem>()
                .HasOne(i => i.Channel)
                .WithMany(c => c.ScheduleItems)
                .HasForeignKey(i => i.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ChannelScheduleItem>()
                .HasOne(i => i.Playable)
                .WithMany()
                .HasForeignKey(i => i.PlayableId)
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
        public DbSet<Person> People { get; set; }
        public DbSet<MovieCredit> MovieCredits { get; set; }
        public DbSet<Genre> Genres { get; set; }
        public DbSet<MovieGenre> MovieGenres { get; set; }
        public DbSet<MoviePlotSummary> MoviePlotSummaries { get; set; }
        public DbSet<MediaFile> MediaFiles { get; set; }
        public DbSet<Playable> Playables { get; set; }
        public DbSet<Episode> Episodes { get; set; }
        public DbSet<Series> Series { get; set; }
        public DbSet<SeriesGenre> SeriesGenres { get; set; }
        public DbSet<SeriesCredit> SeriesCredits { get; set; }
        public DbSet<SeriesPlotSummary> SeriesPlotSummaries { get; set; }
        public DbSet<SeriesPosterDetails> SeriesPosterDetails { get; set; }
        public DbSet<MiscVideo> MiscVideos { get; set; }
        public DbSet<MoviePlaybackProgress> MoviePlaybackProgresses { get; set; }
        public DbSet<Channel> Channels { get; set; }
        public DbSet<ChannelScheduleItem> ChannelScheduleItems { get; set; }

        public MovieDb(DbContextOptions<MovieDb> options)
            : base(options)
        {
        }
    }
}
