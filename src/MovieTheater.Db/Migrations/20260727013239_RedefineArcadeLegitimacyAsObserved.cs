using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class RedefineArcadeLegitimacyAsObserved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArcadeAchievementUnlock_UserId_RaAchievementId_Hardcore",
                table: "ArcadeAchievementUnlock");

            migrationBuilder.RenameColumn(
                name: "Hardcore",
                table: "ArcadeLeaderboardEntry",
                newName: "Competitive");

            migrationBuilder.RenameColumn(
                name: "Hardcore",
                table: "ArcadeAchievementUnlock",
                newName: "Competitive");

            migrationBuilder.AddColumn<bool>(
                name: "Clean",
                table: "ArcadeLeaderboardEntry",
                type: "bit",
                nullable: false,
                computedColumnSql: "CASE WHEN [Cheat] = 0 AND [Savescum] = 0 AND [Timeplay] = 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END",
                stored: true);

            migrationBuilder.AddColumn<bool>(
                name: "Clean",
                table: "ArcadeAchievementUnlock",
                type: "bit",
                nullable: false,
                computedColumnSql: "CASE WHEN [Cheat] = 0 AND [Savescum] = 0 AND [Timeplay] = 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeAchievementUnlock_UserId_RaAchievementId_Clean",
                table: "ArcadeAchievementUnlock",
                columns: new[] { "UserId", "RaAchievementId", "Clean" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArcadeAchievementUnlock_UserId_RaAchievementId_Clean",
                table: "ArcadeAchievementUnlock");

            migrationBuilder.DropColumn(
                name: "Clean",
                table: "ArcadeLeaderboardEntry");

            migrationBuilder.DropColumn(
                name: "Clean",
                table: "ArcadeAchievementUnlock");

            migrationBuilder.RenameColumn(
                name: "Competitive",
                table: "ArcadeLeaderboardEntry",
                newName: "Hardcore");

            migrationBuilder.RenameColumn(
                name: "Competitive",
                table: "ArcadeAchievementUnlock",
                newName: "Hardcore");

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeAchievementUnlock_UserId_RaAchievementId_Hardcore",
                table: "ArcadeAchievementUnlock",
                columns: new[] { "UserId", "RaAchievementId", "Hardcore" },
                unique: true);
        }
    }
}
