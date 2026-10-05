-- ArcadeGame.Controls (nvarchar(24) NULL, 2026-10-05): the cabinet's player-1 control profile the touch pad's arcade
-- presets read -- kind/buttons[/sf], e.g. joy4/1 (Pac-Man), joy8/6/sf (Street Fighter II), trackball/1 (Centipede).
-- Written from MAME's -listxml by the arcade-controls CLI (src/MovieTheater/Arcade/ArcadeControlProfile.cs).
--
-- The same DDL as the EF migration 20261005120000_AddArcadeGameControls, written IDEMPOTENTLY so it can be applied by
-- hand to the live database and re-run without harm (the dev connection IS the live prod DB -- deploy-db-ops skill).
-- Run it through SqlConnection split on GO. Apply BEFORE the site deploy that maps the column: EF selects every mapped
-- column, so every ArcadeGame read would fail on a database without it. NULL keeps the running (older) site working.

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ArcadeGame') AND name = N'Controls')
BEGIN
    ALTER TABLE dbo.ArcadeGame ADD Controls nvarchar(24) NULL;
    PRINT 'added ArcadeGame.Controls';
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261005120000_AddArcadeGameControls')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    SELECT N'20261005120000_AddArcadeGameControls', (SELECT TOP 1 ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC);
    PRINT 'recorded 20261005120000_AddArcadeGameControls';
END
GO
