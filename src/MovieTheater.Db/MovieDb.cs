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
            // correct with no app-side syncing. Short/TvShort (2,3) ⇒ Short (2). IMDb also tags many
            // short films "video" (TitleType 8) — e.g. Pixar/Marvel one-shots released as Blu-ray
            // extras — so a Video UNDER 45 min also ⇒ Short; longer videos (concert films, direct-to-
            // video features) stay Movies. Everything else ⇒ Movies (0). Series-typed rows live in the
            // Series table, so a Movie row never needs the Series/Misc buckets.
            modelBuilder.Entity<Movie>()
                .Property(m => m.NormalizedTitleType)
                .HasComputedColumnSql("CASE WHEN [TitleType] IN (2, 3) THEN 2 WHEN [TitleType] = 8 AND [RuntimeMinutes] IS NOT NULL AND [RuntimeMinutes] < 45 THEN 2 ELSE 0 END", stored: true);

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
                .HasIndex(e => new { e.SeriesId, e.SeasonNumber, e.EpisodeNumber })
                .IsUnique();

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

            // A viewing targets a Movie OR a Series OR a MiscVideo; Restrict avoids a multiple-cascade-path
            // error from User and keeps a viewing from silently vanishing when its target is reclassified.
            modelBuilder.Entity<Viewing>()
                .HasOne(v => v.Series).WithMany(s => s.Viewings)
                .HasForeignKey(v => v.SeriesId).OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Viewing>()
                .HasOne(v => v.MiscVideo).WithMany(mv => mv.Viewings)
                .HasForeignKey(v => v.MiscVideoId).OnDelete(DeleteBehavior.Restrict);

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

            // A code-catalog channel is identified by CatalogKey (so it survives a rename); the index is
            // filtered so the many NULL keys of hand-made channels don't collide. Mirrors Person.ImdbNameId.
            modelBuilder.Entity<Channel>()
                .HasIndex(c => c.CatalogKey)
                .IsUnique()
                .HasFilter("[CatalogKey] IS NOT NULL");

            // ── User playlists & watch parties (docs/playlists-watchparty-plan.md; additive) ──
            modelBuilder.Entity<Channel>()
                .Property(c => c.WatchpartyStartedUtc).HasConversion(UtcConverter);
            // A watch party is reached by its token; unique, filtered so the many NULL tokens of normal
            // channels don't collide (same shape as CatalogKey).
            modelBuilder.Entity<Channel>()
                .HasIndex(c => c.WatchpartyToken)
                .IsUnique()
                .HasFilter("[WatchpartyToken] IS NOT NULL");

            modelBuilder.Entity<PlaylistItem>()
                .HasIndex(p => new { p.ChannelId, p.Position });
            modelBuilder.Entity<PlaylistItem>()
                .HasOne(p => p.Channel)
                .WithMany(c => c.PlaylistItems)
                .HasForeignKey(p => p.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<PlaylistItem>()
                .HasOne(p => p.Playable)
                .WithMany()
                .HasForeignKey(p => p.PlayableId)
                .OnDelete(DeleteBehavior.Restrict);

            // ── AI-inferred insights (model-sourced discovery metadata; additive side tables) ──
            // No DB-level FK to the subject: a TitleInsight points at a Movie OR a Series through the
            // shared id space (SubjectKind + SubjectId), exactly like Viewing/MiscVideo relations are
            // kept honest in app code rather than by a single-target FK. Its tags cascade with it.
            modelBuilder.Entity<TitleTag>()
                .HasOne(t => t.Insight)
                .WithMany(i => i.Tags)
                .HasForeignKey(t => t.TitleInsightId)
                .OnDelete(DeleteBehavior.Cascade);

            // Picking the "current" insight per subject (highest SpecVersion, then latest GeneratedUtc,
            // then Id) is a correlated "no strictly-better row exists" predicate run for every channel's
            // eligible set. This composite makes that probe an index seek. Supersedes the plain
            // (SubjectKind, SubjectId) annotation index for these lookups.
            modelBuilder.Entity<TitleInsight>()
                .HasIndex(ti => new { ti.SubjectKind, ti.SubjectId, ti.SpecVersion, ti.GeneratedUtc, ti.Id });

            // ── Arcade (arcade-plan.md §5; additive, own tables — games are not Movies) ──
            // One ROM per (system, path): the ingest upsert keys on this so a re-run is idempotent
            // and never double-inserts a title.
            modelBuilder.Entity<ArcadeGame>()
                .HasIndex(g => new { g.System, g.RomPath })
                .IsUnique();

            // Lobby paging over ~49k games: an index on the sort key (default view) and on system+sort
            // (the hot filter) so a page request seeks instead of sorting the whole catalog each time.
            modelBuilder.Entity<ArcadeGame>().HasIndex(g => g.SortTitle);
            modelBuilder.Entity<ArcadeGame>().HasIndex(g => new { g.System, g.SortTitle });
            // Default lens = deduped English releases (IsPrimary + Region/Variant), one card per game.
            modelBuilder.Entity<ArcadeGame>().HasIndex(g => new { g.IsPrimary, g.Variant, g.Region, g.SortTitle });
            // The dedupe CLI groups by (System, Title) to pick each game's primary.
            modelBuilder.Entity<ArcadeGame>().HasIndex(g => new { g.System, g.Title });
            // The lobby grid groups CARDS by (System, CollapseKey) — the punctuation/article-folded key —
            // so cosmetically-different dumps of one game collapse into a single card. This index backs both
            // the grouped paging query and the version fetch that follows it.
            modelBuilder.Entity<ArcadeGame>().HasIndex(g => new { g.System, g.CollapseKey });

            // One per-game profile per normalized identity; the export CLI upserts on this key.
            modelBuilder.Entity<ArcadeGameProfile>()
                .HasIndex(p => new { p.System, p.TitleKey })
                .IsUnique();

            modelBuilder.Entity<ArcadeSession>()
                .HasOne(s => s.ArcadeGame)
                .WithMany()
                .HasForeignKey(s => s.ArcadeGameId);

            // Durable per-user saves (docs/arcade-saves-plan.md). One row per (user, game, kind, slot) —
            // the save store's upsert key so a re-harvest updates in place instead of duplicating.
            modelBuilder.Entity<ArcadeSave>()
                .HasIndex(s => new { s.UserId, s.ArcadeGameId, s.Kind, s.SlotId })
                .IsUnique();
            modelBuilder.Entity<ArcadeSave>()
                .HasOne(s => s.ArcadeGame)
                .WithMany()
                .HasForeignKey(s => s.ArcadeGameId);

            // Imported community cheat codes, one row per (ROM, position-in-source-file). The unique key is
            // what makes arcade-cheats-import idempotent; the cascade drops a game's cheats with the game.
            modelBuilder.Entity<ArcadeCheat>()
                .HasIndex(c => new { c.ArcadeGameId, c.Ordinal })
                .IsUnique();
            modelBuilder.Entity<ArcadeCheat>()
                .HasOne(c => c.ArcadeGame)
                .WithMany()
                .HasForeignKey(c => c.ArcadeGameId)
                .OnDelete(DeleteBehavior.Cascade);

            // Heavy lane (docs/arcade-heavy-lane-plan.md §7.3): paired Moonlight devices → site users.
            // ClientName is the join key against Apollo's client name; unique so re-pairing re-owns.
            modelBuilder.Entity<HeavyClient>()
                .HasIndex(c => c.ClientName)
                .IsUnique();

            // RetroAchievements mirror (the RA-backed achievements/leaderboards feature). Source of truth is
            // retroachievements.org (rcheevos submits under each player's own account); these are our copy for
            // site UI + friends boards, harvested via the secret-gated internal callbacks.
            // Softcore and hardcore unlocks are distinct on RA, so the dedupe key includes Hardcore — a
            // re-harvest of the same unlock updates in place instead of duplicating.
            modelBuilder.Entity<ArcadeAchievementUnlock>()
                .HasIndex(a => new { a.UserId, a.RaAchievementId, a.Hardcore })
                .IsUnique();
            modelBuilder.Entity<ArcadeAchievementUnlock>()
                .HasOne(a => a.ArcadeGame)
                .WithMany()
                .HasForeignKey(a => a.ArcadeGameId)
                .OnDelete(DeleteBehavior.SetNull);

            // One BEST row per (user, RA leaderboard) — the harvest keeps the better of the two by Format,
            // so the friends board ranks straight off these rows.
            modelBuilder.Entity<ArcadeLeaderboardEntry>()
                .HasIndex(e => new { e.UserId, e.RaLeaderboardId })
                .IsUnique();
            modelBuilder.Entity<ArcadeLeaderboardEntry>()
                .HasOne(e => e.ArcadeGame)
                .WithMany()
                .HasForeignKey(e => e.ArcadeGameId)
                .OnDelete(DeleteBehavior.SetNull);

            // One row per RA Web API request path — the durable RA response cache (fetch once for the whole
            // friend group, survive restarts). Unique on the key so an upsert can't duplicate.
            modelBuilder.Entity<ArcadeRaApiCache>()
                .HasIndex(c => c.CacheKey)
                .IsUnique();
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
        public DbSet<ChannelShelf> ChannelShelves { get; set; }
        public DbSet<PlaylistItem> PlaylistItems { get; set; }
        public DbSet<TitleInsight> TitleInsights { get; set; }
        public DbSet<TitleTag> TitleTags { get; set; }
        public DbSet<TitleRecommendation> TitleRecommendations { get; set; }
        public DbSet<UserTasteProfile> UserTasteProfiles { get; set; }
        public DbSet<ArcadeGame> ArcadeGames { get; set; }
        public DbSet<ArcadeSession> ArcadeSessions { get; set; }
        public DbSet<ArcadeSave> ArcadeSaves { get; set; }
        public DbSet<ArcadeCheat> ArcadeCheats { get; set; }
        public DbSet<ArcadeGameProfile> ArcadeGameProfiles { get; set; }
        public DbSet<ArcadeAchievementUnlock> ArcadeAchievementUnlocks { get; set; }
        public DbSet<ArcadeLeaderboardEntry> ArcadeLeaderboardEntries { get; set; }
        public DbSet<ArcadeRaApiCache> ArcadeRaApiCaches { get; set; }
        public DbSet<HeavyClient> HeavyClients { get; set; }

        public MovieDb(DbContextOptions<MovieDb> options)
            : base(options)
        {
        }
    }
}
