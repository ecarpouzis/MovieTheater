-- Track-level popularity for the music vertical (2026-08-31).
--
-- The same DDL as the EF migration AddMusicTrackPopularity, written IDEMPOTENTLY so it can be run
-- against the live database more than once without failing — which is what makes it safe to re-run
-- after an interrupted apply rather than having to work out how far the last one got.
--
-- Purely additive: three nullable columns and two indexes. Nothing is rewritten, nothing is dropped,
-- and every existing row keeps NULL (= "never asked"), which is exactly the state the
-- music-track-popularity queue starts from.

SET QUOTED_IDENTIFIER ON;   -- sqlcmd defaults this OFF; this DB has indexes that require it ON.
SET NOCOUNT ON;

IF COL_LENGTH('dbo.MusicTrack', 'Popularity') IS NULL
BEGIN
    ALTER TABLE dbo.MusicTrack ADD Popularity int NULL;
    PRINT 'added MusicTrack.Popularity';
END
ELSE PRINT 'MusicTrack.Popularity already present';
GO

IF COL_LENGTH('dbo.MusicTrack', 'PopularitySource') IS NULL
BEGIN
    ALTER TABLE dbo.MusicTrack ADD PopularitySource nvarchar(32) NULL;
    PRINT 'added MusicTrack.PopularitySource';
END
ELSE PRINT 'MusicTrack.PopularitySource already present';
GO

IF COL_LENGTH('dbo.MusicTrack', 'PopularityCheckedUtc') IS NULL
BEGIN
    ALTER TABLE dbo.MusicTrack ADD PopularityCheckedUtc datetime2 NULL;
    PRINT 'added MusicTrack.PopularityCheckedUtc';
END
ELSE PRINT 'MusicTrack.PopularityCheckedUtc already present';
GO

-- The music-track-popularity queue: "never asked", in cursor order.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicTrack_PopularityCheckedUtc_Id' AND object_id = OBJECT_ID('dbo.MusicTrack'))
BEGIN
    CREATE INDEX IX_MusicTrack_PopularityCheckedUtc_Id ON dbo.MusicTrack (PopularityCheckedUtc, Id);
    PRINT 'created IX_MusicTrack_PopularityCheckedUtc_Id';
END
ELSE PRINT 'IX_MusicTrack_PopularityCheckedUtc_Id already present';
GO

-- The artist page's "Most popular" read: a top-10 over one artist.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicTrack_ArtistId_Popularity' AND object_id = OBJECT_ID('dbo.MusicTrack'))
BEGIN
    CREATE INDEX IX_MusicTrack_ArtistId_Popularity ON dbo.MusicTrack (ArtistId, Popularity);
    PRINT 'created IX_MusicTrack_ArtistId_Popularity';
END
ELSE PRINT 'IX_MusicTrack_ArtistId_Popularity already present';
GO

SELECT COUNT(*) AS Tracks,
       SUM(CASE WHEN PopularityCheckedUtc IS NULL THEN 1 ELSE 0 END) AS NeverAsked,
       SUM(CASE WHEN Popularity IS NOT NULL THEN 1 ELSE 0 END) AS Scored
FROM dbo.MusicTrack;
GO
