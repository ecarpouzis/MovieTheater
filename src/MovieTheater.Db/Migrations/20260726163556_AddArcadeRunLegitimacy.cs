using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeRunLegitimacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Cheat",
                table: "ArcadeLeaderboardEntry",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Savescum",
                table: "ArcadeLeaderboardEntry",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Timeplay",
                table: "ArcadeLeaderboardEntry",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Cheat",
                table: "ArcadeAchievementUnlock",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Savescum",
                table: "ArcadeAchievementUnlock",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Timeplay",
                table: "ArcadeAchievementUnlock",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cheat",
                table: "ArcadeLeaderboardEntry");

            migrationBuilder.DropColumn(
                name: "Savescum",
                table: "ArcadeLeaderboardEntry");

            migrationBuilder.DropColumn(
                name: "Timeplay",
                table: "ArcadeLeaderboardEntry");

            migrationBuilder.DropColumn(
                name: "Cheat",
                table: "ArcadeAchievementUnlock");

            migrationBuilder.DropColumn(
                name: "Savescum",
                table: "ArcadeAchievementUnlock");

            migrationBuilder.DropColumn(
                name: "Timeplay",
                table: "ArcadeAchievementUnlock");
        }
    }
}
