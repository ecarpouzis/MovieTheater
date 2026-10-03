-- Arcade codec program (2026-10-02): ArcadeSession.VideoCodec / ControllerScheme / CodecProbe (all NULL).
-- VideoCodec + ControllerScheme make the room's per-room choices survive a pod restart's rehydrate (they were
-- in-memory only, so joiners of a rehydrated H.264 room were handed the default AV1 track). CodecProbe is the
-- creator browser's Auto-codec probe answer — observability only.
--
-- The same DDL as the EF migration 20261002120000_AddArcadeSessionCodec, written IDEMPOTENTLY so it can be
-- applied by hand to the live database and re-run without harm (the dev connection IS the live prod DB --
-- see the deploy-db-ops skill). Run it through SqlConnection split on GO, batch by batch, and read the
-- PRINT output back. Apply BEFORE deploying the site build that maps these columns.

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeSession') AND name = N'VideoCodec')
BEGIN
    ALTER TABLE dbo.ArcadeSession ADD VideoCodec nvarchar(20) NULL;
    PRINT 'added ArcadeSession.VideoCodec';
END
ELSE PRINT 'ArcadeSession.VideoCodec already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeSession') AND name = N'ControllerScheme')
BEGIN
    ALTER TABLE dbo.ArcadeSession ADD ControllerScheme nvarchar(20) NULL;
    PRINT 'added ArcadeSession.ControllerScheme';
END
ELSE PRINT 'ArcadeSession.ControllerScheme already present';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeSession') AND name = N'CodecProbe')
BEGIN
    ALTER TABLE dbo.ArcadeSession ADD CodecProbe nvarchar(80) NULL;
    PRINT 'added ArcadeSession.CodecProbe';
END
ELSE PRINT 'ArcadeSession.CodecProbe already present';
GO

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261002120000_AddArcadeSessionCodec')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    SELECT N'20261002120000_AddArcadeSessionCodec', (SELECT TOP 1 ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC);
    PRINT 'recorded 20261002120000_AddArcadeSessionCodec';
END
ELSE PRINT 'migration already recorded';
GO
