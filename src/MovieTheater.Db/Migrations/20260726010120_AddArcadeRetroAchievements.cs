using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeRetroAchievements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCompetitive",
                table: "ArcadeSession",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ArcadeAchievementUnlock",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RaUser = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ArcadeGameId = table.Column<int>(type: "int", nullable: true),
                    RaGameHash = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    RaAchievementId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Points = table.Column<int>(type: "int", nullable: false),
                    Hardcore = table.Column<bool>(type: "bit", nullable: false),
                    UnlockedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcadeAchievementUnlock", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArcadeAchievementUnlock_ArcadeGame_ArcadeGameId",
                        column: x => x.ArcadeGameId,
                        principalTable: "ArcadeGame",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ArcadeLeaderboardEntry",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RaUser = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ArcadeGameId = table.Column<int>(type: "int", nullable: true),
                    RaGameHash = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    RaLeaderboardId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Value = table.Column<long>(type: "bigint", nullable: false),
                    Format = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Hardcore = table.Column<bool>(type: "bit", nullable: false),
                    AchievedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcadeLeaderboardEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArcadeLeaderboardEntry_ArcadeGame_ArcadeGameId",
                        column: x => x.ArcadeGameId,
                        principalTable: "ArcadeGame",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeAchievementUnlock_ArcadeGameId",
                table: "ArcadeAchievementUnlock",
                column: "ArcadeGameId");

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeAchievementUnlock_UserId_RaAchievementId_Hardcore",
                table: "ArcadeAchievementUnlock",
                columns: new[] { "UserId", "RaAchievementId", "Hardcore" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeLeaderboardEntry_ArcadeGameId",
                table: "ArcadeLeaderboardEntry",
                column: "ArcadeGameId");

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeLeaderboardEntry_UserId_RaLeaderboardId",
                table: "ArcadeLeaderboardEntry",
                columns: new[] { "UserId", "RaLeaderboardId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArcadeAchievementUnlock");

            migrationBuilder.DropTable(
                name: "ArcadeLeaderboardEntry");

            migrationBuilder.DropColumn(
                name: "IsCompetitive",
                table: "ArcadeSession");
        }
    }
}
