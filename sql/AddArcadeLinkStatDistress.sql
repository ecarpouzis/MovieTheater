-- Arcade codec program (2026-10-02): ArcadeLinkStat.DistressWeakTicks / DistressStrongTicks / ScaleDowns
-- (int NOT NULL DEFAULT 0) -- each peer's decoder-distress record for the room, from the worker's close row.
--
-- The same DDL as the EF migration 20261002130000_AddArcadeLinkStatDistress, written IDEMPOTENTLY so it can be
-- applied by hand to the live database and re-run without harm (the dev connection IS the live prod DB --
-- see the deploy-db-ops skill). Run it through SqlConnection split on GO. Apply BEFORE the site deploy that
-- maps these columns (existing rows read 0 = no distress recorded).

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'DistressWeakTicks')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD DistressWeakTicks int NOT NULL CONSTRAINT DF_ArcadeLinkStat_DistressWeakTicks DEFAULT 0;
    PRINT 'added ArcadeLinkStat.DistressWeakTicks';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'DistressStrongTicks')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD DistressStrongTicks int NOT NULL CONSTRAINT DF_ArcadeLinkStat_DistressStrongTicks DEFAULT 0;
    PRINT 'added ArcadeLinkStat.DistressStrongTicks';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeLinkStat') AND name = N'ScaleDowns')
BEGIN
    ALTER TABLE dbo.ArcadeLinkStat ADD ScaleDowns int NOT NULL CONSTRAINT DF_ArcadeLinkStat_ScaleDowns DEFAULT 0;
    PRINT 'added ArcadeLinkStat.ScaleDowns';
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261002130000_AddArcadeLinkStatDistress')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    SELECT N'20261002130000_AddArcadeLinkStatDistress', (SELECT TOP 1 ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC);
    PRINT 'recorded 20261002130000_AddArcadeLinkStatDistress';
END
GO
