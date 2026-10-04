-- Arcade congestion memory (worker patches 0049/0050, 2026-10-03): ArcadeLinkStat.Descents / HardDescents / Craters /
-- WallExits / WallSeeds / Plis (int NOT NULL DEFAULT 0) and OverFirstKbps / OverMaxKbps (int NOT NULL DEFAULT -1 = none)
-- -- the room's congestion-memory counters and the peer's PLI count, from the worker's close row.
--
-- The same DDL as the EF migration 20261003230000_AddArcadeLinkStatCongestion, written IDEMPOTENTLY so it can be
-- applied by hand to the live database and re-run without harm (the dev connection IS the live prod DB -- see the
-- deploy-db-ops skill). Run it through SqlConnection split on GO. Apply BEFORE the site deploy that maps these columns:
-- EF inserts every mapped column, so the LinkStat ingest would fail on a database without them. The defaults keep the
-- running (older) site working after this runs.
--
-- No new index: the LinkWall lookup filters UserId + DeviceId + CreatedUtc >= now-24h, which seeks the existing
-- (UserId, DeviceId, CreatedUtc) index from AddArcadeLinkStat; Path/Codec/SustainedKbps are residual on a handful of rows.

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'Descents')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD Descents int NOT NULL CONSTRAINT DF_ArcadeLinkStat_Descents DEFAULT 0;
    PRINT 'added ArcadeLinkStat.Descents';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'HardDescents')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD HardDescents int NOT NULL CONSTRAINT DF_ArcadeLinkStat_HardDescents DEFAULT 0;
    PRINT 'added ArcadeLinkStat.HardDescents';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'Craters')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD Craters int NOT NULL CONSTRAINT DF_ArcadeLinkStat_Craters DEFAULT 0;
    PRINT 'added ArcadeLinkStat.Craters';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'WallExits')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD WallExits int NOT NULL CONSTRAINT DF_ArcadeLinkStat_WallExits DEFAULT 0;
    PRINT 'added ArcadeLinkStat.WallExits';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'WallSeeds')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD WallSeeds int NOT NULL CONSTRAINT DF_ArcadeLinkStat_WallSeeds DEFAULT 0;
    PRINT 'added ArcadeLinkStat.WallSeeds';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'OverFirstKbps')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD OverFirstKbps int NOT NULL CONSTRAINT DF_ArcadeLinkStat_OverFirstKbps DEFAULT -1;
    PRINT 'added ArcadeLinkStat.OverFirstKbps';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'OverMaxKbps')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD OverMaxKbps int NOT NULL CONSTRAINT DF_ArcadeLinkStat_OverMaxKbps DEFAULT -1;
    PRINT 'added ArcadeLinkStat.OverMaxKbps';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'Plis')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD Plis int NOT NULL CONSTRAINT DF_ArcadeLinkStat_Plis DEFAULT 0;
    PRINT 'added ArcadeLinkStat.Plis';
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261003230000_AddArcadeLinkStatCongestion')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    SELECT N'20261003230000_AddArcadeLinkStatCongestion', (SELECT TOP 1 ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC);
    PRINT 'recorded 20261003230000_AddArcadeLinkStatCongestion';
END
GO
