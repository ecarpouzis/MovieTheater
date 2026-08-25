using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// One column that lets a JIT game's companion land somewhere other than the ROM mount.
    ///
    /// PSP downloadable content is the case that forced it: a game looks for its DLC at
    /// ms0:/PSP/GAME/&lt;TITLEID&gt;/ and nowhere else, and ms0: is the emulator SAVE root
    /// (&lt;ConfDir&gt;/libretro/legacy_save) — not the read-only ROM mount every other companion goes to.
    /// Without this the DLC could only be hand-copied onto each worker, which is exactly how the 3DS
    /// texture packs are installed today and exactly why they vanish whenever a ConfDir is rebuilt.
    ///
    /// Values use the same "&lt;root&gt;:&lt;relpath&gt;" grammar as the worker config's cards: map, e.g.
    /// "save:PSP/GAME/NPUG80061". NULL keeps the existing behaviour (ROM-mount subfolder), so every
    /// existing row is unaffected. Applied to the live DB by hand on 2026-08-25.
    /// </summary>
    public partial class AddArcadeCompanionDest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceCompanionDest",
                table: "ArcadeGame",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SourceCompanionDest", table: "ArcadeGame");
        }
    }
}
