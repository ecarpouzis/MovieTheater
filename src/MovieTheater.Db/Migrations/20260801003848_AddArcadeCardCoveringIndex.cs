using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeCardCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ArcadeGame_IsEnabled_System_CollapseKey",
                table: "ArcadeGame",
                columns: new[] { "IsEnabled", "System", "CollapseKey" })
                .Annotation("SqlServer:Include", new[] { "SortTitle", "Title", "RatingWeighted", "Year", "MaxPlayers" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArcadeGame_IsEnabled_System_CollapseKey",
                table: "ArcadeGame");
        }
    }
}
