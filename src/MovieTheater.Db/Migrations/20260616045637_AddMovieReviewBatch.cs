using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieReviewBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewBatch",
                table: "Movie",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewConfidence",
                table: "Movie",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewProvenance",
                table: "Movie",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewSourcePath",
                table: "Movie",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movie_ReviewBatch",
                table: "Movie",
                column: "ReviewBatch");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Movie_ReviewBatch",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "ReviewBatch",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "ReviewConfidence",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "ReviewProvenance",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "ReviewSourcePath",
                table: "Movie");
        }
    }
}
