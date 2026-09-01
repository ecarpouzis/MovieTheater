-- Multi-source track popularity (2026-08-31): one row per (track, source), plus the consensus
-- columns the library-wide ranking is read from.
--
-- Why a table and not more columns: the Source value is part of the unique key, so each pass owns
-- and REPLACES only its own rows and any number of services coexist. That is the same shape
-- MusicAlbumGenre already uses for exactly the same reason.
--
-- Idempotent and purely additive. Existing rows keep NULL, which is "no source has scored this yet".

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF OBJECT_ID('dbo.MusicTrackScore') IS NULL
BEGIN
    CREATE TABLE dbo.MusicTrackScore (
        Id           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MusicTrackScore PRIMARY KEY,
        MusicTrackId int           NOT NULL,
        Source       nvarchar(32)  NOT NULL,
        Score        int           NOT NULL,
        RawValue     bigint        NULL,
        CheckedUtc   datetime2     NOT NULL,
        CONSTRAINT FK_MusicTrackScore_MusicTrack FOREIGN KEY (MusicTrackId)
            REFERENCES dbo.MusicTrack (Id) ON DELETE CASCADE
    );
    PRINT 'created dbo.MusicTrackScore';
END
ELSE PRINT 'dbo.MusicTrackScore already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicTrackScore_MusicTrackId_Source' AND object_id = OBJECT_ID('dbo.MusicTrackScore'))
BEGIN
    CREATE UNIQUE INDEX IX_MusicTrackScore_MusicTrackId_Source ON dbo.MusicTrackScore (MusicTrackId, Source);
    PRINT 'created IX_MusicTrackScore_MusicTrackId_Source';
END
ELSE PRINT 'IX_MusicTrackScore_MusicTrackId_Source already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicTrackScore_Source' AND object_id = OBJECT_ID('dbo.MusicTrackScore'))
BEGIN
    CREATE INDEX IX_MusicTrackScore_Source ON dbo.MusicTrackScore (Source);
    PRINT 'created IX_MusicTrackScore_Source';
END
ELSE PRINT 'IX_MusicTrackScore_Source already present';
GO

IF COL_LENGTH('dbo.MusicTrack', 'PopularityRank') IS NULL
BEGIN
    ALTER TABLE dbo.MusicTrack ADD PopularityRank int NULL;
    PRINT 'added MusicTrack.PopularityRank';
END
ELSE PRINT 'MusicTrack.PopularityRank already present';
GO

-- NOT NULL with a default: "no sources" is 0, never unknown, so a consensus can always be judged.
IF COL_LENGTH('dbo.MusicTrack', 'PopularityRankSources') IS NULL
BEGIN
    ALTER TABLE dbo.MusicTrack ADD PopularityRankSources int NOT NULL
        CONSTRAINT DF_MusicTrack_PopularityRankSources DEFAULT 0;
    PRINT 'added MusicTrack.PopularityRankSources';
END
ELSE PRINT 'MusicTrack.PopularityRankSources already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicTrack_PopularityRank' AND object_id = OBJECT_ID('dbo.MusicTrack'))
BEGIN
    CREATE INDEX IX_MusicTrack_PopularityRank ON dbo.MusicTrack (PopularityRank);
    PRINT 'created IX_MusicTrack_PopularityRank';
END
ELSE PRINT 'IX_MusicTrack_PopularityRank already present';
GO

SELECT (SELECT COUNT(*) FROM dbo.MusicTrackScore) AS ScoreRows,
       (SELECT COUNT(*) FROM dbo.MusicTrack WHERE PopularityRank IS NOT NULL) AS Ranked;
GO
