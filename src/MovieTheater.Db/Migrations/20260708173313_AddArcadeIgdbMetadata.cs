using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeIgdbMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Developer",
                table: "ArcadeGame",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EsrbRating",
                table: "ArcadeGame",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GameModes",
                table: "ArcadeGame",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "ArcadeGame",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IgdbId",
                table: "ArcadeGame",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OfflineMaxPlayers",
                table: "ArcadeGame",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Publisher",
                table: "ArcadeGame",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RatingCount",
                table: "ArcadeGame",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RatingScore",
                table: "ArcadeGame",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "ArcadeGame",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Themes",
                table: "ArcadeGame",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Developer",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "EsrbRating",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "GameModes",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "Genres",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "IgdbId",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "OfflineMaxPlayers",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "Publisher",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "RatingCount",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "RatingScore",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "Themes",
                table: "ArcadeGame");
        }
    }
}
