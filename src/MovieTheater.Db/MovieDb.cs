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

            // The restore lookup: a sync re-point asks "do we hold a banked keyframe list for these
            // bytes" by the row's fingerprint. Filtered to stamped rows — most of the table is stamped,
            // but a partial index keeps the null tail free.
            modelBuilder.Entity<MediaFile>()
                .HasIndex(f => f.ContentFingerprint)
                .HasFilter("[ContentFingerprint] IS NOT NULL");

            modelBuilder.Entity<MediaFile>()
                .HasOne(f => f.Playable)
                .WithMany(p => p.Files)
                .HasForeignKey(f => f.PlayableId)
                .OnDelete(DeleteBehavior.Cascade);

            // The review queue reads Pending/Ingested rows; the sync's upsert pass reads everything
            // non-superseded. Status leads so both stay index-served as the table accretes history.
            modelBuilder.Entity<SyncCandidate>()
                .HasIndex(c => new { c.Status, c.Kind });

            // ClientSetNull, not SetNull: SQL Server refuses two SET NULL paths into Movie from one
            // table ("may cause cycles or multiple cascade paths"). The DB gets NO ACTION and
            // DeleteMovieSubtreeAsync clears/redirects candidate references before removing a movie.
            modelBuilder.Entity<SyncCandidate>()
                .HasOne(c => c.TargetMovie)
                .WithMany()
                .HasForeignKey(c => c.TargetMovieId)
                .OnDelete(DeleteBehavior.ClientSetNull);

            modelBuilder.Entity<SyncCandidate>()
                .HasOne(c => c.CreatedMovie)
                .WithMany()
                .HasForeignKey(c => c.CreatedMovieId)
                .OnDelete(DeleteBehavior.ClientSetNull);

            // Same NO ACTION treatment as the Movie FKs above: DeleteSeriesSubtreeAsync clears candidate
            // references before removing a series, so a rejected show's episode candidates come back
            // Pending instead of the delete throwing.
            modelBuilder.Entity<SyncCandidate>()
                .HasOne(c => c.TargetSeries)
                .WithMany()
                .HasForeignKey(c => c.TargetSeriesId)
                .OnDelete(DeleteBehavior.ClientSetNull);

            // The review tool's series lane always reads by folder group.
            modelBuilder.Entity<SyncCandidate>()
                .HasIndex(c => new { c.Status, c.SeriesFolder });

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
            // …and this one COVERS that grouping, which is what makes the UNFILTERED lobby usable. Without it
            // the grid's page query has to read Title/SortTitle/RatingWeighted/Year/MaxPlayers, none of which
            // the index above carries, so SQL Server scans the whole 100 MB table and hash-aggregates three
            // nvarchar(400) columns over ~39k rows — a grant it can't hold, so it SPILLS TO TEMPDB. Measured
            // 2026-07-31: 13k logical reads on the table plus ~10k on the workfile, 450 ms of CPU stretched
            // into 4.5–11 s of waiting. One console selected was 0.37 s, because that filter is what kept the
            // scan small. Keyed on IsEnabled first (every lobby query starts there) and ordered by the group
            // key, the aggregate can stream instead of hashing.
            modelBuilder.Entity<ArcadeGame>()
                .HasIndex(g => new { g.IsEnabled, g.System, g.CollapseKey })
                .IncludeProperties(g => new { g.SortTitle, g.Title, g.RatingWeighted, g.Year, g.MaxPlayers });

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
            // Run legitimacy is OBSERVED, not asserted: `Clean` is a PERSISTED COMPUTED column over the three
            // taints, so the database itself derives it and no callback or backfill can ever record a "clean"
            // run the taints contradict. (The old `Hardcore` column asserted the room's competitive MODE and
            // was ANDed into legitimacy, which meant a perfectly clean casual run counted for nothing and a
            // competitive room's boot-seeded state counted for everything. Room mode is now kept purely as
            // provenance in `Competitive`.)
            modelBuilder.Entity<ArcadeAchievementUnlock>()
                .Property(a => a.Clean)
                .HasComputedColumnSql(
                    "CASE WHEN [Cheat] = 0 AND [Savescum] = 0 AND [Timeplay] = 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END",
                    stored: true);
            modelBuilder.Entity<ArcadeLeaderboardEntry>()
                .Property(e => e.Clean)
                .HasComputedColumnSql(
                    "CASE WHEN [Cheat] = 0 AND [Savescum] = 0 AND [Timeplay] = 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END",
                    stored: true);

            // The dedupe key includes Clean: a re-harvest of the same unlock updates in place, but earning it
            // CLEANLY after a dirty unlock is a genuine first and gets its own row.
            modelBuilder.Entity<ArcadeAchievementUnlock>()
                .HasIndex(a => new { a.UserId, a.RaAchievementId, a.Clean })
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

            // ── Music (docs/music-plan.md §2.2; additive, own tables — tracks are not Movies) ──
            // Identity comes from the curated folder tree: artist = top-level folder, album = its
            // first-level subfolder, track = file. Each level's folder/path is the unique upsert key
            // that makes music-ingest idempotent.
            modelBuilder.Entity<MusicArtist>()
                .HasIndex(a => a.FolderName)
                .IsUnique();
            modelBuilder.Entity<MusicArtist>()
                .HasIndex(a => a.SortName);

            modelBuilder.Entity<MusicAlbum>()
                .HasIndex(a => a.FolderPath)
                .IsUnique();
            modelBuilder.Entity<MusicAlbum>()
                .HasIndex(a => new { a.ArtistId, a.Year });
            modelBuilder.Entity<MusicAlbum>()
                .HasOne(a => a.Artist)
                .WithMany()
                .HasForeignKey(a => a.ArtistId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MusicTrack>()
                .HasIndex(t => t.RelativePath)
                .IsUnique();
            // Album tracklist order and the artist/search lookups the browse endpoints run.
            modelBuilder.Entity<MusicTrack>()
                .HasIndex(t => new { t.AlbumId, t.DiscNo, t.TrackNo });
            modelBuilder.Entity<MusicTrack>()
                .HasIndex(t => t.ArtistId);
            modelBuilder.Entity<MusicTrack>()
                .HasIndex(t => t.Title);
            // Restrict both parents: a track row must never vanish because its artist/album row was
            // touched — reconcile flags MissingSinceUtc instead (same stance as MediaFile).
            modelBuilder.Entity<MusicTrack>()
                .HasOne(t => t.Artist)
                .WithMany()
                .HasForeignKey(t => t.ArtistId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<MusicTrack>()
                .HasOne(t => t.Album)
                .WithMany()
                .HasForeignKey(t => t.AlbumId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MusicTrackLyrics>()
                .HasOne(l => l.Track)
                .WithOne()
                .HasForeignKey<MusicTrackLyrics>(l => l.TrackId)
                .OnDelete(DeleteBehavior.Cascade);

            // ── Music metadata (R9 S10): genre, popularity, site ratings ──
            // The SOURCE is part of a genre row's identity: the tag pass and the external passes each
            // own their own rows for an album, so a re-run replaces one pass's output and leaves the
            // other's alone. That unique index is what makes both passes idempotent.
            modelBuilder.Entity<MusicAlbumGenre>()
                .HasIndex(g => new { g.AlbumId, g.Source, g.Genre })
                .IsUnique();
            // The rail's Genre facet asks the other question — "which albums are Jazz?" — so the join
            // is indexed from both ends.
            modelBuilder.Entity<MusicAlbumGenre>()
                .HasIndex(g => g.Genre);
            // Cascade from the album: a genre row is a statement ABOUT the album and means nothing
            // without it. (MusicTrack stays Restrict — a track is content, this is a label.)
            modelBuilder.Entity<MusicAlbumGenre>()
                .HasOne(g => g.Album)
                .WithMany()
                .HasForeignKey(g => g.AlbumId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MusicArtistGenre>()
                .HasIndex(g => new { g.ArtistId, g.Source, g.Genre })
                .IsUnique();
            modelBuilder.Entity<MusicArtistGenre>()
                .HasIndex(g => g.Genre);
            modelBuilder.Entity<MusicArtistGenre>()
                .HasOne(g => g.Artist)
                .WithMany()
                .HasForeignKey(g => g.ArtistId)
                .OnDelete(DeleteBehavior.Cascade);

            // One opinion per listener per album — the double-tap guard, and what makes the POST an
            // upsert rather than an append.
            modelBuilder.Entity<MusicAlbumRating>()
                .HasIndex(r => new { r.UserId, r.AlbumId })
                .IsUnique();
            // "What does the house think of this album?" — the library blend's read.
            modelBuilder.Entity<MusicAlbumRating>()
                .HasIndex(r => r.AlbumId);
            // Restrict on User for the multiple-cascade-path reason MusicPlaylist documents above;
            // cascade from the album, which is the row the rating is about.
            modelBuilder.Entity<MusicAlbumRating>()
                .HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<MusicAlbumRating>()
                .HasOne(r => r.Album)
                .WithMany()
                .HasForeignKey(r => r.AlbumId)
                .OnDelete(DeleteBehavior.Cascade);

            // Read newest-first, and "is this still happening?" is a count by kind over a date range.
            modelBuilder.Entity<MusicPlaybackIncident>()
                .HasIndex(i => new { i.CreatedUtc, i.Kind });

            // Same index for the same two questions on the video side — and it is the index the
            // insert-time retention prune walks (everything older than the cutoff, oldest first).
            modelBuilder.Entity<VideoPlaybackIncident>()
                .HasIndex(i => new { i.CreatedUtc, i.Kind });

            modelBuilder.Entity<MusicPlaylist>()
                .HasIndex(p => p.UserId);
            // At most ONE favorites list per user. Filtered so it constrains only the flagged rows —
            // an ordinary unique index on (UserId, IsFavorites) would cap everyone at a single normal
            // playlist too. The heart's get-or-create races with itself on a double click, and this is
            // what makes the loser fail loudly instead of quietly minting a second Favorites.
            // ⚠ A filtered index requires SET QUOTED_IDENTIFIER ON for any session that writes to this
            // table — fine for EF/SqlClient, which sets it, but sqlcmd defaults it OFF, so a hand-run
            // INSERT/UPDATE against MusicPlaylist needs the SET first.
            modelBuilder.Entity<MusicPlaylist>()
                .HasIndex(p => p.UserId, "IX_MusicPlaylist_Favorites")
                .HasFilter("[IsFavorites] = 1")
                .IsUnique();
            // Restrict on User to avoid multiple-cascade-path errors (same stance as MoviePlaybackProgress).
            modelBuilder.Entity<MusicPlaylist>()
                .HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // One share per (playlist, user): the pair is the identity, so a repeat "share with Bob"
            // is a no-op rather than a second row. Cascade from the playlist (deleting it should take
            // its grants with it) but Restrict on User, for the same multiple-cascade-path reason as
            // MusicPlaylist above.
            modelBuilder.Entity<MusicPlaylistShare>()
                .HasIndex(s => new { s.PlaylistId, s.UserId })
                .IsUnique();
            modelBuilder.Entity<MusicPlaylistShare>()
                .HasIndex(s => s.UserId);
            modelBuilder.Entity<MusicPlaylistShare>()
                .HasOne(s => s.Playlist)
                .WithMany()
                .HasForeignKey(s => s.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<MusicPlaylistShare>()
                .HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MusicPlaylistItem>()
                .HasIndex(i => new { i.PlaylistId, i.Position });
            modelBuilder.Entity<MusicPlaylistItem>()
                .HasOne(i => i.Playlist)
                .WithMany(p => p.Items)
                .HasForeignKey(i => i.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<MusicPlaylistItem>()
                .HasOne(i => i.Track)
                .WithMany()
                .HasForeignKey(i => i.TrackId)
                .OnDelete(DeleteBehavior.Restrict);

            // ArcadeLinkStat is append-only observability, so NOT unique — many sessions per device is the
            // point. The index matches the only query shape planned for it: "the most recent rows for this
            // user on THIS device", newest first, which is how a warm-start value gets computed (min of the
            // last few sessions, ignoring anything older than ~12h).
            modelBuilder.Entity<ArcadeLinkStat>()
                .HasIndex(s => new { s.UserId, s.DeviceId, s.CreatedUtc });

            ConfigurePhotos(modelBuilder);
        }

        /// <summary>
        /// Family photo album (docs/photos-plan.md §3). Additive, own tables, and — per §6's privacy
        /// invariant — joined to NOTHING global: no OData entity set, no search index, no AI-insight,
        /// recommendation, channel or poster-mosaic input. These rows are reachable only through the
        /// family-gated /API/Photos routes. Any future feature that wants photo data amends §6 first.
        ///
        /// <para>Delete behavior is Restrict on every edge into <see cref="PhotoAsset"/>, and Cascade
        /// only from an aggregate root to its own child rows (group→members, album→entries). Curation
        /// is years of irreplaceable human labor (§2.11), so nothing here removes rows as a side effect
        /// — a vanished file is flagged, not deleted (§2.5).</para>
        /// </summary>
        private static void ConfigurePhotos(ModelBuilder modelBuilder)
        {
            // Content is identity, path is location (§2.5): Path is unique so the walk can upsert on it,
            // and MUTABLE so a NAS folder reorganization re-points the existing row instead of orphaning
            // every tag, date and album entry hanging off its id.
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => a.Path)
                .IsUnique();

            // The timeline is the primary browse surface (§1), and its page query is
            // "WHERE Hidden = 0 ORDER BY TakenAt DESC" over the whole collection. Keyed to match that
            // predicate + sort so a page SEEKS and range-scans in order, and covering the card columns
            // (the INCLUDE rule already in use for the arcade lobby grid) so the page never leaves the
            // index for the base table. Without the INCLUDE this is a full scan of a table that is
            // planned to hold 50k–150k rows with a raw-metadata blob on each one.
            //   ⚠ §3 names this column TakenAtUtc; it is TakenAt, because §2.7 settled the column on
            //   naive local wall-clock and the UTC readout lives beside it in TakenAtUtcRaw.
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => new { a.Hidden, a.TakenAt })
                .IsDescending(false, true)
                .IncludeProperties(a => new { a.Path, a.Kind, a.Width, a.Height, a.DurationSec, a.TakenAtSource, a.MissingSinceUtc });

            // Phase 7 (§2.12): the timeline now also says `AND Shelf = Timeline`, and the index above
            // does not carry Shelf in its key OR its INCLUDE — so that predicate would become a residual
            // the server can only evaluate after fetching the row, on the hottest query in the section.
            //
            // The obvious repair is to add Shelf to the existing key. It is not available: an index key
            // cannot be extended in place, so that repair is a DROP and a CREATE, and Phase 7's migration
            // is required to be purely additive against a live shared database. This is the additive
            // spelling of the same fix — a SECOND covering index, keyed and INCLUDE-ing exactly as the
            // first, FILTERED to the shelf the timeline reads. Three properties earn it:
            //   · it matches the timeline/undated/person-page predicate exactly, so those pages seek;
            //   · it SHRINKS as the archive grows, which is the same reason the three ingest queues are
            //     filtered indexes rather than plain ones;
            //   · the original stays for the surfaces that do NOT filter by shelf — the folder tree, and
            //     an admin browsing with show-hidden on.
            // The cost is honest: two covering indexes on one table means the metadata pass maintains
            // both. That is a bounded, one-time-per-photo write against an unbounded, every-page read.
            //   ⚠ Filtered index ⇒ SET QUOTED_IDENTIFIER ON in any session WRITING PhotoAsset. That
            //   constraint already binds this table (three ingest queues above), so Phase 7 adds no new
            //   operational rule — only one more index that depends on the one already in force.
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => new { a.Hidden, a.TakenAt }, "IX_PhotoAsset_TimelineShelf")
                .IsDescending(false, true)
                .HasFilter("[Shelf] = 0")
                .IncludeProperties(a => new { a.Path, a.Kind, a.Width, a.Height, a.DurationSec, a.TakenAtSource, a.MissingSinceUtc });

            // Re-pairing moved files and exact-dupe grouping both start here. NOT unique — equal hashes
            // are the entire point of §2.6 — and nullable until the hash pass has run over the row.
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => a.Sha256);

            // Near-dupe grouping buckets on a hash prefix per run; this is what makes reading the
            // hashed population cheap enough to build the BK-tree from.
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => a.PHash);

            // The ingest-batch review queue (the ReviewBatch convention), and the drift report.
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => a.IngestBatch);
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => a.MissingSinceUtc);

            // photos-sync-jellyfin stamps this by path and the gated stream-start reads it back (§2.3).
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => a.JellyfinItemId);

            // The three ingest queues (§2.5 phases 2–4). Each pass asks the same two questions every
            // batch — "the next N rows I have not stamped" and "how many are left" — so each gets a
            // FILTERED index keyed on Id: it matches the queue predicate exactly, orders by the same
            // column the cursor pages on, and SHRINKS TO EMPTY as the queue drains, which is the whole
            // point (an unfiltered index on the stamp would stay full-sized forever to answer a question
            // that ends up having no rows). Id ascending is the cursor ordering AND the query ordering —
            // the cheats-import cursor bug rule, restated in code.
            //   ⚠ Filtered indexes require SET QUOTED_IDENTIFIER ON in any session WRITING PhotoAsset.
            //   EF/SqlClient set it; sqlcmd defaults it OFF, so a hand-run INSERT/UPDATE needs the SET.
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => a.Id, "IX_PhotoAsset_MetadataQueue")
                .HasFilter("[MetadataUpdatedUtc] IS NULL");
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => a.Id, "IX_PhotoAsset_HashQueue")
                .HasFilter("[HashUpdatedUtc] IS NULL");
            modelBuilder.Entity<PhotoAsset>()
                .HasIndex(a => a.Id, "IX_PhotoAsset_ThumbQueue")
                .HasFilter("[ThumbsUpdatedUtc] IS NULL");

            modelBuilder.Entity<FamilyPerson>()
                .HasIndex(p => p.Name);
            // Suggestion fan-out looks the cluster up by its Immich id; unique so naming the same
            // cluster twice can't quietly mint a second person.
            modelBuilder.Entity<FamilyPerson>()
                .HasIndex(p => p.ImmichPersonId, "IX_FamilyPerson_ImmichPersonId")
                .HasFilter("[ImmichPersonId] IS NOT NULL")
                .IsUnique();
            modelBuilder.Entity<FamilyPerson>()
                .HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<FamilyPerson>()
                .HasOne(p => p.CoverAsset)
                .WithMany()
                .HasForeignKey(p => p.CoverAssetId)
                .OnDelete(DeleteBehavior.Restrict);

            // One tag per (asset, person): a suggestion that a human confirms is the SAME row changing
            // Source, never a second row beside it.
            modelBuilder.Entity<PhotoPersonTag>()
                .HasIndex(t => new { t.PhotoAssetId, t.FamilyPersonId })
                .IsUnique();
            // Person pages: "photos of X, newest first" — and the tag queue reads the pending rows by
            // source. Both start from the person, so the person leads the key.
            modelBuilder.Entity<PhotoPersonTag>()
                .HasIndex(t => new { t.FamilyPersonId, t.Source });
            modelBuilder.Entity<PhotoPersonTag>()
                .HasOne(t => t.PhotoAsset)
                .WithMany()
                .HasForeignKey(t => t.PhotoAssetId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<PhotoPersonTag>()
                .HasOne(t => t.FamilyPerson)
                .WithMany()
                .HasForeignKey(t => t.FamilyPersonId)
                .OnDelete(DeleteBehavior.Restrict);

            // The review queue is "groups still pending, oldest first".
            modelBuilder.Entity<PhotoDupeGroup>()
                .HasIndex(g => new { g.Status, g.Kind });
            modelBuilder.Entity<PhotoDupeGroup>()
                .HasOne(g => g.ResolvedByUser)
                .WithMany()
                .HasForeignKey(g => g.ResolvedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<PhotoDupeMember>()
                .HasIndex(m => new { m.PhotoDupeGroupId, m.PhotoAssetId })
                .IsUnique();
            // Browse collapses to masters, so the hot question is "is this asset a non-master?" — asked
            // per card, which is why the asset leads here rather than the group.
            modelBuilder.Entity<PhotoDupeMember>()
                .HasIndex(m => new { m.PhotoAssetId, m.IsMaster });
            // Exactly one master per group, enforced rather than trusted: the master pick is written
            // from a review UI where a double-click is a race. Filtered so it constrains only the
            // flagged rows (the MusicPlaylist favorites precedent).
            //   ⚠ A filtered index requires SET QUOTED_IDENTIFIER ON for any session WRITING this table.
            //   EF/SqlClient set it; sqlcmd defaults it OFF, so a hand-run INSERT/UPDATE needs the SET.
            modelBuilder.Entity<PhotoDupeMember>()
                .HasIndex(m => m.PhotoDupeGroupId, "IX_PhotoDupeMember_Master")
                .HasFilter("[IsMaster] = 1")
                .IsUnique();
            modelBuilder.Entity<PhotoDupeMember>()
                .HasOne(m => m.PhotoDupeGroup)
                .WithMany(g => g.Members)
                .HasForeignKey(m => m.PhotoDupeGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<PhotoDupeMember>()
                .HasOne(m => m.PhotoAsset)
                .WithMany()
                .HasForeignKey(m => m.PhotoAssetId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<PhotoAlbum>()
                .HasIndex(a => a.Slug)
                .IsUnique();
            // Phase 7 (§2.12) splits the album index in two by Shelf, and deliberately adds NO index for
            // it: PhotoAlbum holds tens of rows, both indexes read the whole table either way, and an
            // index whose only effect is to be maintained is a cost with no reader. Said out loud so the
            // absence reads as a decision rather than an oversight.
            modelBuilder.Entity<PhotoAlbum>()
                .HasOne(a => a.CoverAsset)
                .WithMany()
                .HasForeignKey(a => a.CoverAssetId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<PhotoAlbum>()
                .HasOne(a => a.CreatedByUser)
                .WithMany()
                .HasForeignKey(a => a.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // An asset belongs to an album once; adding it again is a no-op, not a second row.
            modelBuilder.Entity<PhotoAlbumEntry>()
                .HasIndex(e => new { e.PhotoAlbumId, e.PhotoAssetId })
                .IsUnique();
            // The album page's own ordering.
            modelBuilder.Entity<PhotoAlbumEntry>()
                .HasIndex(e => new { e.PhotoAlbumId, e.SortOrder });
            // "Which albums is this photo in?" — the lightbox asks it for every photo opened.
            modelBuilder.Entity<PhotoAlbumEntry>()
                .HasIndex(e => e.PhotoAssetId);
            modelBuilder.Entity<PhotoAlbumEntry>()
                .HasOne(e => e.PhotoAlbum)
                .WithMany(a => a.Entries)
                .HasForeignKey(e => e.PhotoAlbumId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<PhotoAlbumEntry>()
                .HasOne(e => e.PhotoAsset)
                .WithMany()
                .HasForeignKey(e => e.PhotoAssetId)
                .OnDelete(DeleteBehavior.Restrict);

            // (file name, taken time, size) is the Takeout identity — sidecars carry no stable Google
            // id (§2.10). Unique, so re-running the mesh over next quarter's archive upserts these rows
            // instead of duplicating them.
            //   ⚠ Two of the three columns are nullable, so EF emits this with a
            //   "WHERE TakenAtUtc IS NOT NULL AND SizeBytes IS NOT NULL" filter — an item whose sidecar
            //   supplied neither is NOT constrained by the database. The mesh's upsert must therefore
            //   look the row up before inserting rather than leaning on the index to catch a repeat.
            modelBuilder.Entity<PhotoGoogleItem>()
                .HasIndex(i => new { i.TakeoutFileName, i.TakenAtUtc, i.SizeBytes })
                .IsUnique();
            // The Google-only review list, and the "has the match pass fully drained?" check the
            // download lane refuses to run without.
            modelBuilder.Entity<PhotoGoogleItem>()
                .HasIndex(i => i.Status);
            modelBuilder.Entity<PhotoGoogleItem>()
                .HasOne(i => i.MatchedPhotoAsset)
                .WithMany()
                .HasForeignKey(i => i.MatchedPhotoAssetId)
                .OnDelete(DeleteBehavior.Restrict);

            // Curation review state (§2.5 quarantine, §2.9 hide proposals). Phase 3 moved this out of
            // JSON under PhotosReportDir and into rows, because prod's site pods cannot read the CLI
            // host's report directory — a JSON-backed review surface is simply empty there.
            // One batch per (kind, id): re-running a proposal pass with the same --batch-id APPENDS to
            // the batch rather than minting a second one, and approving an ingest twice is a no-op.
            modelBuilder.Entity<PhotoCurationBatch>()
                .HasIndex(b => new { b.Kind, b.BatchId })
                .IsUnique();
            // The review surfaces ask exactly one question — "what is still pending, newest first".
            modelBuilder.Entity<PhotoCurationBatch>()
                .HasIndex(b => new { b.Kind, b.Status, b.CreatedUtc });
            modelBuilder.Entity<PhotoCurationBatch>()
                .HasOne(b => b.DecidedByUser)
                .WithMany()
                .HasForeignKey(b => b.DecidedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // An asset appears in a batch once; a re-run of the same pass must not stack duplicates.
            modelBuilder.Entity<PhotoCurationBatchItem>()
                .HasIndex(i => new { i.PhotoCurationBatchId, i.PhotoAssetId })
                .IsUnique();
            modelBuilder.Entity<PhotoCurationBatchItem>()
                .HasIndex(i => i.PhotoAssetId);
            modelBuilder.Entity<PhotoCurationBatchItem>()
                .HasOne(i => i.PhotoCurationBatch)
                .WithMany(b => b.Items)
                .HasForeignKey(i => i.PhotoCurationBatchId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<PhotoCurationBatchItem>()
                .HasOne(i => i.PhotoAsset)
                .WithMany()
                .HasForeignKey(i => i.PhotoAssetId)
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
        public DbSet<MediaKeyframes> MediaKeyframes { get; set; }
        public DbSet<SyncCandidate> SyncCandidates { get; set; }
        public DbSet<Playable> Playables { get; set; }
        public DbSet<Episode> Episodes { get; set; }
        public DbSet<Series> Series { get; set; }
        public DbSet<SeriesGenre> SeriesGenres { get; set; }
        public DbSet<SeriesCredit> SeriesCredits { get; set; }
        public DbSet<SeriesPlotSummary> SeriesPlotSummaries { get; set; }
        public DbSet<SeriesPosterDetails> SeriesPosterDetails { get; set; }
        public DbSet<MiscVideo> MiscVideos { get; set; }
        public DbSet<MoviePlaybackProgress> MoviePlaybackProgresses { get; set; }
        /// <summary>The video players' self-reports (mirrors <see cref="MusicPlaybackIncidents"/>) —
        /// written only by /API/Stream/Incident, read by hand when chasing "it stopped".</summary>
        public DbSet<VideoPlaybackIncident> VideoPlaybackIncidents { get; set; }
        public DbSet<Channel> Channels { get; set; }
        public DbSet<ChannelScheduleItem> ChannelScheduleItems { get; set; }
        public DbSet<ChannelShelf> ChannelShelves { get; set; }
        public DbSet<ChannelViewStat> ChannelViewStats { get; set; }
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
        public DbSet<ArcadeLinkStat> ArcadeLinkStats { get; set; }
        public DbSet<HeavyClient> HeavyClients { get; set; }
        public DbSet<MusicArtist> MusicArtists { get; set; }
        public DbSet<MusicAlbum> MusicAlbums { get; set; }
        public DbSet<MusicTrack> MusicTracks { get; set; }
        public DbSet<MusicTrackLyrics> MusicTrackLyrics { get; set; }
        public DbSet<MusicPlaylist> MusicPlaylists { get; set; }
        public DbSet<MusicPlaylistItem> MusicPlaylistItems { get; set; }
        public DbSet<MusicPlaylistShare> MusicPlaylistShares { get; set; }
        public DbSet<MusicPlaybackIncident> MusicPlaybackIncidents { get; set; }
        public DbSet<MusicAlbumGenre> MusicAlbumGenres { get; set; }
        public DbSet<MusicArtistGenre> MusicArtistGenres { get; set; }
        public DbSet<MusicAlbumRating> MusicAlbumRatings { get; set; }

        // ── Family photo album (docs/photos-plan.md §3) ──────────────────────────────────────────
        // §6 privacy invariant: these sets exist for the family-gated /API/Photos routes ONLY. They are
        // not exposed through OData (the app registers no EDM entity sets — OData is opt-in per action
        // via [EnableQuery] — so adding a DbSet here publishes nothing, and no photo action may ever
        // carry that attribute).
        public DbSet<PhotoAsset> PhotoAssets { get; set; }
        public DbSet<FamilyPerson> FamilyPeople { get; set; }
        public DbSet<PhotoPersonTag> PhotoPersonTags { get; set; }
        public DbSet<PhotoDupeGroup> PhotoDupeGroups { get; set; }
        public DbSet<PhotoDupeMember> PhotoDupeMembers { get; set; }
        public DbSet<PhotoAlbum> PhotoAlbums { get; set; }
        public DbSet<PhotoAlbumEntry> PhotoAlbumEntries { get; set; }
        public DbSet<PhotoGoogleItem> PhotoGoogleItems { get; set; }
        public DbSet<PhotoCurationBatch> PhotoCurationBatches { get; set; }
        public DbSet<PhotoCurationBatchItem> PhotoCurationBatchItems { get; set; }

        public MovieDb(DbContextOptions<MovieDb> options)
            : base(options)
        {
        }
    }
}
