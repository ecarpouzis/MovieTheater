-- ============================================================================================
--  Family Photo Album — PHASE 6 (Google mesh) migration
--  docs/photos-plan.md §2.10.  EF migration: 20260812195305_AddPhotoGoogleMeshColumns
--
--  ⚠⚠  NOT APPLIED.  This script was GENERATED at design time and has never been executed
--      against any database.  The dev connection string in this repository IS the live shared
--      production database, so the standing rule is that DDL is run by the owner, deliberately,
--      under the migration-ops discipline — never by a build, a test, or an agent.
--
--  ⚠   Phase 6 is the FIRST photo phase since Phase 3 to need one.  Phases 4 and 5 shipped with
--      zero schema change by adding ENUM VALUES to existing int columns; this one could not,
--      because three facts §2.10 requires have nowhere on the row to live:
--
--        · MatchDistance   — the pHash Hamming distance a third-rung match was accepted at.  Its
--                            presence IS the "lower confidence" marker, and a match by
--                            resemblance whose number was thrown away cannot be reviewed.
--        · Disagreements   — which fields the sidecar disagreed with the local row about
--                            (§2.10's flag-but-write convention, in both directions).  The
--                            review surface counts these, so they must be rows in the shared
--                            database and not JSON on the CLI host — the Phase 3 lesson.
--        · DownloadedPath  — where the one additive NAS write put a Google-only item.  The
--                            destination is a function of a config value the site's pods cannot
--                            see, so it is recorded rather than re-derived.
--
--  PURELY ADDITIVE.  Three nullable columns on one table.  No ALTER of an existing column, no
--  DROP of anything, no data movement, no index change.  Every existing row reads NULL for all
--  three, which is exactly what "this item has not been through the Phase 6 mesh" means.
--
--  Run photos-export (§2.11) first, as with every migration once curation data exists.
-- ============================================================================================

-- Session settings, so a re-run needs no `sqlcmd -I`: SQL Server refuses filtered indexes and
-- indexed/computed columns under the OFF defaults some clients connect with (the QUOTED_IDENTIFIER
-- trap this repo has hit before). Prepended only -- no DDL below this line was changed.
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [PhotoGoogleItem] ADD [Disagreements] nvarchar(256) NULL;
GO

ALTER TABLE [PhotoGoogleItem] ADD [DownloadedPath] nvarchar(850) NULL;
GO

ALTER TABLE [PhotoGoogleItem] ADD [MatchDistance] int NULL;
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260812195305_AddPhotoGoogleMeshColumns', N'8.0.22');
GO

COMMIT;
GO
