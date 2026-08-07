using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicPlaylistIsFavorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFavorites",
                table: "MusicPlaylist",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylist_Favorites",
                table: "MusicPlaylist",
                column: "UserId",
                unique: true,
                filter: "[IsFavorites] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MusicPlaylist_Favorites",
                table: "MusicPlaylist");

            migrationBuilder.DropColumn(
                name: "IsFavorites",
                table: "MusicPlaylist");
        }
    }
}
