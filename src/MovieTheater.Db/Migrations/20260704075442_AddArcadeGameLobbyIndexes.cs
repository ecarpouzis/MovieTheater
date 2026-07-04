using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeGameLobbyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ArcadeGame_SortTitle",
                table: "ArcadeGame",
                column: "SortTitle");

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeGame_System_SortTitle",
                table: "ArcadeGame",
                columns: new[] { "System", "SortTitle" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArcadeGame_SortTitle",
                table: "ArcadeGame");

            migrationBuilder.DropIndex(
                name: "IX_ArcadeGame_System_SortTitle",
                table: "ArcadeGame");
        }
    }
}
