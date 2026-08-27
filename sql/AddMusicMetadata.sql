/*
    R9 S10 — the music metadata the Music rail, groups and sorts were designed around.

    The same DDL as the EF migration 20260827020000_AddMusicMetadata, written IDEMPOTENTLY so it can
    be applied by hand to the live database and re-run without harm (the dev connection IS the live
    prod DB — see the deploy-db-ops skill).

    ADDITIVE ONLY. Two nullable columns on MusicTrack, three on MusicAlbum, three new tables and
    their indexes. There is no UPDATE, no DELETE and no DROP anywhere in this file: an existing row
    is never touched, so applying it to a running site changes nothing until the CLIs and the API
    start writing the new columns.

    Applied 2026-08-27 through System.Data.SqlClient.SqlConnection, with a read-back.
*/

-- ── MusicTrack: the file's own genre frame, plus the negative cache that bounds the pass ────────
IF COL_LENGTH('dbo.MusicTrack', 'Genre') IS NULL
    ALTER TABLE dbo.MusicTrack ADD Genre nvarchar(200) NULL;
GO
IF COL_LENGTH('dbo.MusicTrack', 'GenreCheckedUtc') IS NULL
    ALTER TABLE dbo.MusicTrack ADD GenreCheckedUtc datetime2 NULL;
GO

-- ── MusicAlbum: the external popularity signal, its provenance and its negative cache ───────────
IF COL_LENGTH('dbo.MusicAlbum', 'Popularity') IS NULL
    ALTER TABLE dbo.MusicAlbum ADD Popularity int NULL;
GO
IF COL_LENGTH('dbo.MusicAlbum', 'PopularitySource') IS NULL
    ALTER TABLE dbo.MusicAlbum ADD PopularitySource nvarchar(32) NULL;
GO
IF COL_LENGTH('dbo.MusicAlbum', 'PopularityCheckedUtc') IS NULL
    ALTER TABLE dbo.MusicAlbum ADD PopularityCheckedUtc datetime2 NULL;
GO

-- ── MusicAlbumGenre: album x genre x SOURCE ─────────────────────────────────────────────────────
-- Source is part of the identity so the tag pass and the external passes each own (and each replace)
-- only their own rows for an album. That is what makes both passes idempotent.
IF OBJECT_ID('dbo.MusicAlbumGenre', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MusicAlbumGenre (
        Id          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MusicAlbumGenre PRIMARY KEY,
        AlbumId     int NOT NULL,
        Genre       nvarchar(100) NOT NULL,
        Source      nvarchar(32) NOT NULL,
        Weight      int NOT NULL CONSTRAINT DF_MusicAlbumGenre_Weight DEFAULT (0),
        CreatedUtc  datetime2 NOT NULL CONSTRAINT DF_MusicAlbumGenre_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_MusicAlbumGenre_MusicAlbum_AlbumId FOREIGN KEY (AlbumId)
            REFERENCES dbo.MusicAlbum (Id) ON DELETE CASCADE
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicAlbumGenre_AlbumId_Source_Genre' AND object_id = OBJECT_ID('dbo.MusicAlbumGenre'))
    CREATE UNIQUE INDEX IX_MusicAlbumGenre_AlbumId_Source_Genre ON dbo.MusicAlbumGenre (AlbumId, Source, Genre);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicAlbumGenre_Genre' AND object_id = OBJECT_ID('dbo.MusicAlbumGenre'))
    CREATE INDEX IX_MusicAlbumGenre_Genre ON dbo.MusicAlbumGenre (Genre);
GO

-- ── MusicArtistGenre: the artist's top few, rolled up from their albums ─────────────────────────
IF OBJECT_ID('dbo.MusicArtistGenre', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MusicArtistGenre (
        Id          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MusicArtistGenre PRIMARY KEY,
        ArtistId    int NOT NULL,
        Genre       nvarchar(100) NOT NULL,
        Source      nvarchar(32) NOT NULL,
        Weight      int NOT NULL CONSTRAINT DF_MusicArtistGenre_Weight DEFAULT (0),
        CreatedUtc  datetime2 NOT NULL CONSTRAINT DF_MusicArtistGenre_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_MusicArtistGenre_MusicArtist_ArtistId FOREIGN KEY (ArtistId)
            REFERENCES dbo.MusicArtist (Id) ON DELETE CASCADE
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicArtistGenre_ArtistId_Source_Genre' AND object_id = OBJECT_ID('dbo.MusicArtistGenre'))
    CREATE UNIQUE INDEX IX_MusicArtistGenre_ArtistId_Source_Genre ON dbo.MusicArtistGenre (ArtistId, Source, Genre);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicArtistGenre_Genre' AND object_id = OBJECT_ID('dbo.MusicArtistGenre'))
    CREATE INDEX IX_MusicArtistGenre_Genre ON dbo.MusicArtistGenre (Genre);
GO

-- ── MusicAlbumRating: one listener's 0-100 score for one album ──────────────────────────────────
-- 0 is a real score and unrated is NO ROW (the movie rating feature's rule, copied verbatim).
-- Restrict on User for the multiple-cascade-path reason MusicPlaylist documents; cascade from the
-- album, which is the row the rating is about.
IF OBJECT_ID('dbo.MusicAlbumRating', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MusicAlbumRating (
        Id          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MusicAlbumRating PRIMARY KEY,
        UserId      int NOT NULL,
        AlbumId     int NOT NULL,
        Score       int NOT NULL,
        CreatedUtc  datetime2 NOT NULL CONSTRAINT DF_MusicAlbumRating_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedUtc  datetime2 NOT NULL CONSTRAINT DF_MusicAlbumRating_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_MusicAlbumRating_MusicAlbum_AlbumId FOREIGN KEY (AlbumId)
            REFERENCES dbo.MusicAlbum (Id) ON DELETE CASCADE,
        CONSTRAINT FK_MusicAlbumRating_Users_UserId FOREIGN KEY (UserId)
            REFERENCES dbo.Users (UserID)
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicAlbumRating_UserId_AlbumId' AND object_id = OBJECT_ID('dbo.MusicAlbumRating'))
    CREATE UNIQUE INDEX IX_MusicAlbumRating_UserId_AlbumId ON dbo.MusicAlbumRating (UserId, AlbumId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicAlbumRating_AlbumId' AND object_id = OBJECT_ID('dbo.MusicAlbumRating'))
    CREATE INDEX IX_MusicAlbumRating_AlbumId ON dbo.MusicAlbumRating (AlbumId);
GO
