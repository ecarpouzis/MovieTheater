/*
    R9 closing pass — play telemetry for the music vertical: the data behind "Most played".

    Until now the vertical recorded NO plays at all (no column, no table, no beacon), so the sort the
    R9 plan asked for had nothing to sort on. This is the one table that fixes it.

    The same DDL as the EF migration 20260827030000_AddMusicPlayStats, written IDEMPOTENTLY so it can
    be applied by hand to the live database and re-run without harm (the dev connection IS the live
    prod DB — see the deploy-db-ops skill).

    ADDITIVE ONLY. One new table and its two indexes. There is no UPDATE, no DELETE and no DROP
    anywhere in this file: no existing row is touched, and until the player's beacon ships the table
    simply stays empty (an empty table reads as "nothing played yet", which is the honest answer).

    Applied 2026-08-27 through System.Data.SqlClient.SqlConnection, with a read-back.
*/

-- ── MusicPlayStat: one listener x one track = a count and two stamps ────────────────────────────
-- An AGGREGATE, not an event log, and deliberately: "most played album/artist" must be cheap enough
-- to ride the shelf rows the browse already fetches, so it has to be a SUM over a table bounded by
-- listeners x tracks-ever-played rather than a COUNT over a row per play forever.
--
-- LastStartedUtc is the IDEMPOTENCY KEY: the client sends the moment playback started, floored to
-- the minute, and a report carrying a minute already recorded is a no-op. That is what lets the
-- beacon be fire-and-forget across retries, a pagehide flush and two tabs.
--
-- Both foreign keys RESTRICT: on Users for the multiple-cascade-path reason MusicPlaylist documents,
-- and on MusicTrack for the reason MusicTrack's own parents are restricted — a play is a fact about
-- listening and must not vanish because a reconcile touched a content row.
IF OBJECT_ID('dbo.MusicPlayStat', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MusicPlayStat (
        Id              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MusicPlayStat PRIMARY KEY,
        UserId          int NOT NULL,
        MusicTrackId    int NOT NULL,
        PlayCount       int NOT NULL CONSTRAINT DF_MusicPlayStat_PlayCount DEFAULT (0),
        LastPlayedUtc   datetime2 NOT NULL CONSTRAINT DF_MusicPlayStat_LastPlayedUtc DEFAULT (SYSUTCDATETIME()),
        LastStartedUtc  datetime2 NOT NULL CONSTRAINT DF_MusicPlayStat_LastStartedUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_MusicPlayStat_Users_UserId FOREIGN KEY (UserId)
            REFERENCES dbo.Users (UserID),
        CONSTRAINT FK_MusicPlayStat_MusicTrack_MusicTrackId FOREIGN KEY (MusicTrackId)
            REFERENCES dbo.MusicTrack (Id)
    );
END
GO
-- The upsert's key: one row per listener per track, so a repeated report cannot mint a second.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicPlayStat_UserId_MusicTrackId' AND object_id = OBJECT_ID('dbo.MusicPlayStat'))
    CREATE UNIQUE INDEX IX_MusicPlayStat_UserId_MusicTrackId ON dbo.MusicPlayStat (UserId, MusicTrackId);
GO
-- The library-wide roll-ups read from the TRACK end and want both numbers off the index leaf.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MusicPlayStat_MusicTrackId' AND object_id = OBJECT_ID('dbo.MusicPlayStat'))
    CREATE INDEX IX_MusicPlayStat_MusicTrackId ON dbo.MusicPlayStat (MusicTrackId) INCLUDE (PlayCount, LastPlayedUtc);
GO
