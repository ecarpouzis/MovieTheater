using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeLaunchBoxRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "LaunchBoxRating",
                table: "ArcadeGame",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LaunchBoxRatingCount",
                table: "ArcadeGame",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RatingWeighted",
                table: "ArcadeGame",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LaunchBoxRating",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "LaunchBoxRatingCount",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "RatingWeighted",
                table: "ArcadeGame");
        }
    }
}
