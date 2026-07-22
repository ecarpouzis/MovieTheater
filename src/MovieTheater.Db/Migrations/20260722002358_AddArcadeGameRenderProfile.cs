using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeGameRenderProfile : Migration
    {
        // NOTE: this adds BOTH HwContext and RenderProfile. HwContext was added to the ArcadeGameProfile
        // ENTITY earlier but never captured in a migration (the model snapshot lacked it), so the diff picks
        // it up here. The shared prod/dev DB is baselined and drifts from the snapshot, and HwContext may
        // already have been added out-of-band — so both adds are written as IDEMPOTENT SQL (add only if the
        // column is missing), making this safe to apply whatever the live DB's current state is.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('ArcadeGameProfile', 'HwContext') IS NULL
    ALTER TABLE [ArcadeGameProfile] ADD [HwContext] nvarchar(10) NULL;
IF COL_LENGTH('ArcadeGameProfile', 'RenderProfile') IS NULL
    ALTER TABLE [ArcadeGameProfile] ADD [RenderProfile] nvarchar(40) NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only drop RenderProfile — HwContext may predate this migration, so leave it in place on rollback.
            migrationBuilder.Sql(@"
IF COL_LENGTH('ArcadeGameProfile', 'RenderProfile') IS NOT NULL
    ALTER TABLE [ArcadeGameProfile] DROP COLUMN [RenderProfile];
");
        }
    }
}
