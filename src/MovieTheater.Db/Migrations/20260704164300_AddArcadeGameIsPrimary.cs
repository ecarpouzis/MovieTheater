using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeGameIsPrimary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill existing rows as primary (visible) — the arcade-dedupe CLI then flips the
            // non-canonical rows to false. Without this the deduped default lobby would be empty
            // between applying the migration and running dedupe.
            migrationBuilder.AddColumn<bool>(
                name: "IsPrimary",
                table: "ArcadeGame",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeGame_IsPrimary_Variant_Region_SortTitle",
                table: "ArcadeGame",
                columns: new[] { "IsPrimary", "Variant", "Region", "SortTitle" });

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeGame_System_Title",
                table: "ArcadeGame",
                columns: new[] { "System", "Title" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArcadeGame_IsPrimary_Variant_Region_SortTitle",
                table: "ArcadeGame");

            migrationBuilder.DropIndex(
                name: "IX_ArcadeGame_System_Title",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "IsPrimary",
                table: "ArcadeGame");
        }
    }
}
