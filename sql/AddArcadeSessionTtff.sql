-- Arcade perf program P1 (2026-09-05): ArcadeSession.TtffMs (int NULL) -- the room's time-to-first-frame
-- in ms, reported once by the creator's browser on a heartbeat. Observability only.
--
-- The same DDL as the EF migration 20260905120000_AddArcadeSessionTtff, written IDEMPOTENTLY so it can be
-- applied by hand to the live database and re-run without harm (the dev connection IS the live prod DB --
-- see the deploy-db-ops skill). Run it through SqlConnection split on GO, batch by batch, and read the
-- PRINT output back.
--
-- What it does, in order:
--   1. ArcadeSession.TtffMs int NULL (every existing row reads NULL = never reported).
--   2. The __EFMigrationsHistory row.

-- 1. The column.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeSession') AND name = N'TtffMs')
BEGIN
    ALTER TABLE dbo.ArcadeSession ADD TtffMs int NULL;
    PRINT 'added ArcadeSession.TtffMs';
END
ELSE PRINT 'ArcadeSession.TtffMs already present';
GO

-- 2. Tell EF the migration ran (ProductVersion = the latest row's).
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20260905120000_AddArcadeSessionTtff')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    SELECT N'20260905120000_AddArcadeSessionTtff', (SELECT TOP 1 ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC);
    PRINT 'recorded 20260905120000_AddArcadeSessionTtff';
END
ELSE PRINT 'migration already recorded';
GO
