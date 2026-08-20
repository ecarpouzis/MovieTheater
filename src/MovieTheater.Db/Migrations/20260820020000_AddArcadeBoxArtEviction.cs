using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Two columns that let a wrong box cover be taken back. The posters mount is shared and the app cannot
    /// delete from it, and /ArcadeImage serves a cached {cardId}.png before it ever re-searches — so until
    /// now a cover the cascade got wrong was permanent unless someone hand-set a replacement URL.
    /// BoxArtGeneration retires the file by renaming what the route looks for; BoxArtBlocked is the terminal
    /// "no art exists for this card, stop looking" state. Applied to the live DB by hand on 2026-08-20.
    /// </summary>
    public partial class AddArcadeBoxArtEviction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BoxArtGeneration",
                table: "ArcadeGame",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "BoxArtBlocked",
                table: "ArcadeGame",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BoxArtGeneration", table: "ArcadeGame");
            migrationBuilder.DropColumn(name: "BoxArtBlocked", table: "ArcadeGame");
        }
    }
}
