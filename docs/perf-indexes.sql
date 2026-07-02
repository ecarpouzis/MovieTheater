-- Optional performance indexes surfaced by the 2026-07-01 audit (backend perf, item #10).
--
-- STATUS: NOT applied automatically. The dev connection string points at the SHARED LIVE DB, and both
-- target columns are nvarchar(max), which SQL Server cannot index without first narrowing the column
-- type. Review and run this by hand during a maintenance window. Impact is LOW at current scale (a
-- handful of users; the imdbID dup-check only runs during admin ingest), so this is a nice-to-have.
--
-- Idempotent: every step is guarded, so re-running is safe.
--
-- Run with QUOTED_IDENTIFIER ON (sqlcmd -I) if your session doesn't default it on.

SET NOCOUNT ON;

------------------------------------------------------------------------------------------------------
-- 1) Users.Username  — Login scans by Username on every sign-in / session restore.
--    nvarchar(max) can't be indexed; narrow to nvarchar(256) first (usernames are short).
------------------------------------------------------------------------------------------------------
IF EXISTS (
    SELECT 1 FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.Users') AND c.name = N'Username'
      AND t.name = N'nvarchar' AND c.max_length = -1  -- -1 == (max)
)
BEGIN
    -- Guard: refuse to narrow if any existing value would be truncated.
    IF EXISTS (SELECT 1 FROM dbo.Users WHERE LEN(Username) > 256)
        THROW 50000, 'A Username exceeds 256 chars; widen the target length before narrowing.', 1;

    ALTER TABLE dbo.Users ALTER COLUMN Username nvarchar(256) NULL;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_Username' AND object_id = OBJECT_ID(N'dbo.Users'))
    CREATE INDEX IX_Users_Username ON dbo.Users (Username);

------------------------------------------------------------------------------------------------------
-- 2) Movie.imdbID — dup-checks during ingest scan the whole Movie table by imdbID.
--    Narrow to nvarchar(32) (an IMDb id like 'tt0000000' is short), then a filtered index over non-nulls.
------------------------------------------------------------------------------------------------------
IF EXISTS (
    SELECT 1 FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.Movies') AND c.name = N'imdbID'
      AND t.name = N'nvarchar' AND c.max_length = -1
)
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Movies WHERE LEN(imdbID) > 32)
        THROW 50000, 'An imdbID exceeds 32 chars; widen the target length before narrowing.', 1;

    ALTER TABLE dbo.Movies ALTER COLUMN imdbID nvarchar(32) NULL;
END;

-- Filtered index (skips the many NULL/imdb-less rows) — needs QUOTED_IDENTIFIER ON to create.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Movies_imdbID' AND object_id = OBJECT_ID(N'dbo.Movies'))
    CREATE INDEX IX_Movies_imdbID ON dbo.Movies (imdbID) WHERE imdbID IS NOT NULL;
