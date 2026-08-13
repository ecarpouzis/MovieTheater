using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoShelf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Shelf",
                table: "PhotoAsset",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ArtistName",
                table: "PhotoAlbum",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Shelf",
                table: "PhotoAlbum",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_TimelineShelf",
                table: "PhotoAsset",
                columns: new[] { "Hidden", "TakenAt" },
                descending: new[] { false, true },
                filter: "[Shelf] = 0")
                .Annotation("SqlServer:Include", new[] { "Path", "Kind", "Width", "Height", "DurationSec", "TakenAtSource", "MissingSinceUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PhotoAsset_TimelineShelf",
                table: "PhotoAsset");

            migrationBuilder.DropColumn(
                name: "Shelf",
                table: "PhotoAsset");

            migrationBuilder.DropColumn(
                name: "ArtistName",
                table: "PhotoAlbum");

            migrationBuilder.DropColumn(
                name: "Shelf",
                table: "PhotoAlbum");
        }
    }
}
