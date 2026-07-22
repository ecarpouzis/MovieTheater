using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeCollapseKey : Migration
    {
        // Idempotent raw SQL (add column + index only if missing), matching the AddArcadeGameRenderProfile
        // pattern: the shared prod/dev DB is baselined and drifts from the model snapshot, so this must be
        // safe to apply whatever the live DB's current state is. CollapseKey is backfilled from Title by
        // arcade-renormalize-titles; the NOT NULL default '' keeps existing rows valid until then.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('ArcadeGame', 'CollapseKey') IS NULL
    ALTER TABLE [ArcadeGame] ADD [CollapseKey] nvarchar(200) NOT NULL CONSTRAINT [DF_ArcadeGame_CollapseKey] DEFAULT '';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ArcadeGame_System_CollapseKey' AND object_id = OBJECT_ID('ArcadeGame'))
    CREATE INDEX [IX_ArcadeGame_System_CollapseKey] ON [ArcadeGame] ([System], [CollapseKey]);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ArcadeGame_System_CollapseKey' AND object_id = OBJECT_ID('ArcadeGame'))
    DROP INDEX [IX_ArcadeGame_System_CollapseKey] ON [ArcadeGame];
IF COL_LENGTH('ArcadeGame', 'CollapseKey') IS NOT NULL
BEGIN
    ALTER TABLE [ArcadeGame] DROP CONSTRAINT [DF_ArcadeGame_CollapseKey];
    ALTER TABLE [ArcadeGame] DROP COLUMN [CollapseKey];
END
");
        }
    }
}
