using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicArtAndLyricsCheckedUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LyricsCheckedUtc",
                table: "MusicTrack",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArtCheckedUtc",
                table: "MusicAlbum",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LyricsCheckedUtc",
                table: "MusicTrack");

            migrationBuilder.DropColumn(
                name: "ArtCheckedUtc",
                table: "MusicAlbum");
        }
    }
}
