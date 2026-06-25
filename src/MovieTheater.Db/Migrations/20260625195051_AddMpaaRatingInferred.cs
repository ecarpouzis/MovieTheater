using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMpaaRatingInferred : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MpaaRatingInferred",
                table: "Series",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpaaRatingInferredSource",
                table: "Series",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpaaRatingInferred",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpaaRatingInferredSource",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MpaaRatingInferred",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "MpaaRatingInferredSource",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "MpaaRatingInferred",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "MpaaRatingInferredSource",
                table: "Movie");
        }
    }
}
