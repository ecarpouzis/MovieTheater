using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoGoogleMeshColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Disagreements",
                table: "PhotoGoogleItem",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DownloadedPath",
                table: "PhotoGoogleItem",
                type: "nvarchar(850)",
                maxLength: 850,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchDistance",
                table: "PhotoGoogleItem",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Disagreements",
                table: "PhotoGoogleItem");

            migrationBuilder.DropColumn(
                name: "DownloadedPath",
                table: "PhotoGoogleItem");

            migrationBuilder.DropColumn(
                name: "MatchDistance",
                table: "PhotoGoogleItem");
        }
    }
}
