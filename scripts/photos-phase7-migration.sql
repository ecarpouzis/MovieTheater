-- ============================================================================================
--  Family Photo Album — PHASE 7 (the Gallery shelf) migration
--  docs/photos-plan.md §2.12.  EF migration: 20260813141214_AddPhotoShelf
--
--  ⚠⚠  NOT APPLIED.  This script was GENERATED at design time and has never been executed
--      against any database.  The dev connection string in this repository IS the live shared
--      production database, so the standing rule is that DDL is run by the owner, deliberately,
--      under the migration-ops discipline — never by a build, a test, or an agent.
--
--  ⚠   NOTHING IN PHASE 7 MAY DEPLOY BEFORE THIS SCRIPT IS APPLIED.  Every query the phase adds
--      reads [Shelf]; the code carries NO runtime fallback for the column being absent, on
--      purpose — a fallback would be a second, untested set of query semantics that only ever
--      runs during a window nobody is watching.  Apply first, deploy second.
--
--  ⚠   ORDERING.  This migration was scaffolded on top of 20260813072858_AddMusicArtistKind
--      (an unrelated music change authored in the same working tree).  The two touch disjoint
--      tables, but __EFMigrationsHistory is ordered, so apply the music script first if it is
--      still outstanding — otherwise `dotnet ef` will consider the history to have a gap.
--
--  WHY A COLUMN AND NOT THE HIDDEN FLAG.  §1 catalogued art/meme/reference piles in the tree;
--  the owner's verdict is that they are "not album material … put them in another section".
--  Hidden was the nearest existing tool and is the WRONG one: since Phase 4 the hidden pile is
--  revealed only to an ADMIN, so hiding art would take it away from the family rather than
--  relocate it.  Shelf is orthogonal — it answers WHICH SECTION, while Hidden answers WHETHER A
--  NON-ADMIN MAY SEE IT AT ALL — and the two compose with Hidden winning.
--
--  PURELY ADDITIVE.  Three new columns and one new index.  No ALTER of an existing column, no
--  DROP of anything, no data movement, and NO CHANGE to any existing index.  Every existing row
--  reads Shelf = 0 (Timeline) and ArtistName = NULL, which is exactly what "this row was written
--  before the Gallery existed" means — so the pre-migration state and the post-migration state
--  describe the same album.
--
--  ABOUT THE INDEX.  The timeline's page query gains `AND Shelf = 0`.  The existing covering
--  index IX_PhotoAsset_Hidden_TakenAt carries Shelf in neither its key nor its INCLUDE, so that
--  predicate would become a residual on the hottest query in the section.  Extending that index
--  in place is impossible — an index key cannot be altered, only dropped and recreated — and a
--  DROP is exactly what this script is not allowed to contain.  So the additive spelling: a
--  SECOND covering index, keyed and INCLUDE-ing identically, FILTERED to the shelf the timeline
--  reads.  It matches the timeline/undated/person-page predicate exactly, it SHRINKS as the
--  archive grows (the same reasoning as the three filtered ingest-queue indexes), and the
--  original stays behind for the surfaces that do not filter by shelf — the folder tree and an
--  admin browsing with show-hidden on.  The cost is two covering indexes maintained by the
--  metadata pass: a bounded, once-per-photo write against an unbounded, every-page read.
--
--  ⚠ It is a FILTERED index, so any session WRITING [PhotoAsset] needs SET QUOTED_IDENTIFIER ON.
--    That constraint already binds this table (IX_PhotoAsset_MetadataQueue / _HashQueue /
--    _ThumbQueue), so Phase 7 adds no new operational rule — it adds one more index that depends
--    on the rule already in force.  EF/SqlClient set it; sqlcmd defaults it OFF, which is why the
--    header below exists.
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

ALTER TABLE [PhotoAsset] ADD [Shelf] int NOT NULL DEFAULT 0;
GO

ALTER TABLE [PhotoAlbum] ADD [ArtistName] nvarchar(256) NULL;
GO

ALTER TABLE [PhotoAlbum] ADD [Shelf] int NOT NULL DEFAULT 0;
GO

CREATE INDEX [IX_PhotoAsset_TimelineShelf] ON [PhotoAsset] ([Hidden], [TakenAt] DESC) INCLUDE ([Path], [Kind], [Width], [Height], [DurationSec], [TakenAtSource], [MissingSinceUtc]) WHERE [Shelf] = 0;
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260813141214_AddPhotoShelf', N'8.0.22');
GO

COMMIT;
GO
