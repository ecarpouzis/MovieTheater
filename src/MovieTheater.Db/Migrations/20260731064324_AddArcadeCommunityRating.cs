using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeCommunityRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CommunityRating",
                table: "ArcadeGame",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CommunityRatingCount",
                table: "ArcadeGame",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommunityRatingSource",
                table: "ArcadeGame",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommunityRating",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "CommunityRatingCount",
                table: "ArcadeGame");

            migrationBuilder.DropColumn(
                name: "CommunityRatingSource",
                table: "ArcadeGame");
        }
    }
}
