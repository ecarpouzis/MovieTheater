-- Provenance on the lists (2026-09-04): who placed a mark, and when.
--
-- The same DDL as the EF migration 20260904120000_AddViewingProvenance, written IDEMPOTENTLY so it can
-- be applied by hand to the live database and re-run without harm (the dev connection IS the live
-- prod DB -- see the deploy-db-ops skill). Run it through SqlConnection split on GO, batch by batch,
-- and read the PRINT / SELECT output back.
--
-- !! NOT APPLIED YET. Replace this banner with the applied date + the read-back counts once it has run.
--
-- What it does, in order:
--   1. Viewing.ViewingType nvarchar(max) -> nvarchar(32), guarded by a MAX(LEN()) check (aborts if a
--      value is longer -- none is: the three values are 4-11 characters).
--   2. Viewing.CreatedUtc / Viewing.CreatedByUserId (both NULL for every existing row), the FK to
--      Users (NO_ACTION, like the table's other FKs on the live DB), the (UserID, ViewingType) index.
--   3. dbo.ViewingEvent -- the append-only journal -- and its two indexes.
--   4. THE DATA STEP (once): every WantToWatch row on an account other than Eric's was one of his
--      suggestions, so CreatedByUserId = Eric is backfilled where it is NULL, with one 'Migrated'
--      journal row each. No type is renamed -- a Want placed by a friend IS the suggestion. Before the
--      UPDATE the script prints a per-user count and copies the affected rows to
--      dbo._viewing_pre_suggested; the snapshot's existence is also the re-run guard.
--   5. The __EFMigrationsHistory row.
--
-- Known and accepted: the UPDATE leaves the Viewing row COUNT unchanged, so the catalog warmer's
-- fingerprint does not move and a live `my=want` group index can stay stale for up to its TTL (20 min).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- 1. Narrow ViewingType so it can be indexed.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Viewing') AND name = 'ViewingType' AND max_length = -1)
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Viewing WHERE LEN(ViewingType) > 32)
        RAISERROR('Viewing.ViewingType holds a value longer than 32 characters; not narrowing.', 16, 1);
    ELSE
    BEGIN
        ALTER TABLE dbo.Viewing ALTER COLUMN ViewingType nvarchar(32) NULL;
        PRINT 'narrowed Viewing.ViewingType to nvarchar(32)';
    END
END
ELSE PRINT 'Viewing.ViewingType already nvarchar(32)';
GO

-- 2. Provenance columns + FK + the list index.
IF COL_LENGTH('dbo.Viewing', 'CreatedUtc') IS NULL
BEGIN
    ALTER TABLE dbo.Viewing ADD CreatedUtc datetime2 NULL;
    PRINT 'added Viewing.CreatedUtc';
END
ELSE PRINT 'Viewing.CreatedUtc already present';
GO

IF COL_LENGTH('dbo.Viewing', 'CreatedByUserId') IS NULL
BEGIN
    ALTER TABLE dbo.Viewing ADD CreatedByUserId int NULL;
    PRINT 'added Viewing.CreatedByUserId';
END
ELSE PRINT 'Viewing.CreatedByUserId already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Viewing_Users_CreatedByUserId')
BEGIN
    ALTER TABLE dbo.Viewing WITH CHECK ADD CONSTRAINT FK_Viewing_Users_CreatedByUserId
        FOREIGN KEY (CreatedByUserId) REFERENCES dbo.Users (UserID) ON DELETE NO ACTION;
    PRINT 'added FK_Viewing_Users_CreatedByUserId';
END
ELSE PRINT 'FK_Viewing_Users_CreatedByUserId already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Viewing_CreatedByUserId' AND object_id = OBJECT_ID('dbo.Viewing'))
BEGIN
    CREATE INDEX IX_Viewing_CreatedByUserId ON dbo.Viewing (CreatedByUserId);
    PRINT 'created IX_Viewing_CreatedByUserId';
END
ELSE PRINT 'IX_Viewing_CreatedByUserId already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Viewing_UserID_ViewingType' AND object_id = OBJECT_ID('dbo.Viewing'))
BEGIN
    CREATE INDEX IX_Viewing_UserID_ViewingType ON dbo.Viewing (UserID, ViewingType)
        INCLUDE (MovieID, SeriesId, MiscVideoId, CreatedByUserId, CreatedUtc);
    PRINT 'created IX_Viewing_UserID_ViewingType';
END
ELSE PRINT 'IX_Viewing_UserID_ViewingType already present';
GO

-- 3. The journal.
IF OBJECT_ID('dbo.ViewingEvent') IS NULL
BEGIN
    CREATE TABLE dbo.ViewingEvent (
        Id          bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ViewingEvent PRIMARY KEY,
        UserId      int          NOT NULL,
        ActorUserId int          NULL,
        MovieID     int          NULL,
        SeriesId    int          NULL,
        MiscVideoId int          NULL,
        ViewingType nvarchar(32) NOT NULL,
        [Action]    nvarchar(16) NOT NULL,
        Data        nvarchar(64) NULL,
        AtUtc       datetime2    NOT NULL,
        Source      nvarchar(16) NOT NULL
    );
    PRINT 'created dbo.ViewingEvent';
END
ELSE PRINT 'dbo.ViewingEvent already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ViewingEvent_UserId_AtUtc' AND object_id = OBJECT_ID('dbo.ViewingEvent'))
BEGIN
    CREATE INDEX IX_ViewingEvent_UserId_AtUtc ON dbo.ViewingEvent (UserId, AtUtc DESC);
    PRINT 'created IX_ViewingEvent_UserId_AtUtc';
END
ELSE PRINT 'IX_ViewingEvent_UserId_AtUtc already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ViewingEvent_AtUtc' AND object_id = OBJECT_ID('dbo.ViewingEvent'))
BEGIN
    CREATE INDEX IX_ViewingEvent_AtUtc ON dbo.ViewingEvent (AtUtc);
    PRINT 'created IX_ViewingEvent_AtUtc';
END
ELSE PRINT 'IX_ViewingEvent_AtUtc already present';
GO

-- 4. THE DATA STEP -- other people's WantToWatch rows were all Eric's suggestions: stamp him as the placer.
IF OBJECT_ID('dbo._viewing_pre_suggested') IS NOT NULL
    PRINT 'data step already applied (dbo._viewing_pre_suggested exists) -- skipping';
ELSE
BEGIN
    DECLARE @eric int = (SELECT TOP 1 UserID FROM dbo.Users WHERE Username = N'Eric');
    IF @eric IS NULL
        RAISERROR('No user named Eric -- refusing to attribute the suggestions.', 16, 1);
    ELSE
    BEGIN
        PRINT 'dry run -- WantToWatch rows per user with no placer yet (Eric = ' + CAST(@eric AS nvarchar(10)) + ' keeps his own):';
        DECLARE @line nvarchar(200);
        DECLARE c CURSOR LOCAL FAST_FORWARD FOR
            SELECT u.Username + N' (' + CAST(v.UserID AS nvarchar(10)) + N'): ' + CAST(COUNT(*) AS nvarchar(10))
            FROM dbo.Viewing v JOIN dbo.Users u ON u.UserID = v.UserID
            WHERE v.ViewingType = N'WantToWatch' AND v.CreatedByUserId IS NULL GROUP BY v.UserID, u.Username ORDER BY u.Username;
        OPEN c; FETCH NEXT FROM c INTO @line;
        WHILE @@FETCH_STATUS = 0 BEGIN PRINT '  ' + @line; FETCH NEXT FROM c INTO @line; END
        CLOSE c; DEALLOCATE c;

        SELECT * INTO dbo._viewing_pre_suggested FROM dbo.Viewing WHERE ViewingType = N'WantToWatch' AND CreatedByUserId IS NULL;
        PRINT 'snapshot dbo._viewing_pre_suggested: ' + CAST(@@ROWCOUNT AS nvarchar(10)) + ' rows';

        DECLARE @now datetime2 = SYSUTCDATETIME();
        INSERT INTO dbo.ViewingEvent (UserId, ActorUserId, MovieID, SeriesId, MiscVideoId, ViewingType, [Action], Data, AtUtc, Source)
        SELECT UserID, @eric, MovieID, SeriesId, MiscVideoId, N'WantToWatch', N'Migrated', NULL, @now, N'migration'
        FROM dbo.Viewing WHERE ViewingType = N'WantToWatch' AND CreatedByUserId IS NULL AND UserID <> @eric;
        PRINT 'journal rows written: ' + CAST(@@ROWCOUNT AS nvarchar(10));

        UPDATE dbo.Viewing SET CreatedByUserId = @eric
        WHERE ViewingType = N'WantToWatch' AND CreatedByUserId IS NULL AND UserID <> @eric;
        PRINT 'rows attributed to Eric: ' + CAST(@@ROWCOUNT AS nvarchar(10));
    END
END
GO

-- 5. Tell EF the migration ran (ProductVersion = the latest row's).
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20260904120000_AddViewingProvenance')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    SELECT N'20260904120000_AddViewingProvenance', (SELECT TOP 1 ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC);
    PRINT 'recorded 20260904120000_AddViewingProvenance';
END
ELSE PRINT 'migration already recorded';
GO

-- Read-back: per type, per user, and how many Want rows a friend placed.
SELECT u.Username, v.ViewingType, COUNT(*) AS Rows,
       SUM(CASE WHEN v.CreatedByUserId IS NOT NULL AND v.CreatedByUserId <> v.UserID THEN 1 ELSE 0 END) AS PlacedByAFriend
FROM dbo.Viewing v JOIN dbo.Users u ON u.UserID = v.UserID
WHERE v.ViewingType IN (N'Seen', N'WantToWatch', N'Rated')
GROUP BY u.Username, v.ViewingType ORDER BY u.Username, v.ViewingType;
GO
