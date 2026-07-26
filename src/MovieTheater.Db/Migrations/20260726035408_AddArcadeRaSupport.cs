using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeRaSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RaAchievementCount",
                table: "ArcadeGame",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RaCheckedUtc",
                table: "ArcadeGame",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RaGameId",
                table: "ArcadeGame",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RaHasScoreLeaderboard",
                table: "ArcadeGame",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RaHasTimeLeaderboard",
                table: "ArcadeGame",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RaAchievementCount",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "RaCheckedUtc",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "RaGameId",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "RaHasScoreLeaderboard",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "RaHasTimeLeaderboard",
                table: "ArcadeGame");
        }
    }
}
