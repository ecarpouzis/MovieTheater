-- Add ArcadeGameProfile.HwContext (W3 F1 — explicit per-game hardware-render context override).
--
-- Column: nvarchar(10) NULL. Values: 'gl' | 'vulkan' | NULL (null = defer to the worker's
-- renderer-option inference and the core config default). Exported to the worker's
-- game-overrides.json `hwContext` field by arcade-gameconfig-export; see
-- docs/arcade-vulkan-w3w4w5-spec.md and src/MovieTheater.Db/ArcadeGameProfile.cs.
--
-- ⚠ DO NOT auto-run. The live DB is the SHARED prod/dev database (appsettings.Development.json conn
-- is the single live DB) and it is baselined / drifts from the EF snapshot, so schema is applied by
-- hand, reviewed, not via `dotnet ef database update`. This script is idempotent and purely additive
-- (adds one nullable column — no data touched, no default backfill) so a re-run is a no-op.
--
-- Apply (after review):  sqlcmd -S <server> -d <db> -I -i scripts/add-arcadegameprofile-hwcontext.sql

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.ArcadeGameProfile') AND name = N'HwContext'
)
BEGIN
    ALTER TABLE dbo.ArcadeGameProfile ADD HwContext nvarchar(10) NULL;
    PRINT 'Added ArcadeGameProfile.HwContext (nvarchar(10) NULL).';
END
ELSE
    PRINT 'ArcadeGameProfile.HwContext already exists — no change.';
GO

-- Optional guard: keep the column to the two valid tokens (mirrors nanoarch.GameHwContext / the
-- exporter's NormalizeHwContext, which already drop anything else — this is belt-and-braces).
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ArcadeGameProfile_HwContext'
)
   AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.ArcadeGameProfile') AND name = N'HwContext'
)
BEGIN
    ALTER TABLE dbo.ArcadeGameProfile
        ADD CONSTRAINT CK_ArcadeGameProfile_HwContext
        CHECK (HwContext IS NULL OR HwContext IN (N'gl', N'vulkan'));
    PRINT 'Added CK_ArcadeGameProfile_HwContext.';
END
ELSE
    PRINT 'CK_ArcadeGameProfile_HwContext already present or column missing — no change.';
GO
