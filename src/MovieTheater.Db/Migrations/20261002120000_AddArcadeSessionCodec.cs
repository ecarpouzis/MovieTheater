using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Arcade codec program (2026-10-02): three nullable columns on <c>ArcadeSession</c>.
    /// <c>VideoCodec</c> and <c>ControllerScheme</c> make the room's per-room codec and Wii scheme durable —
    /// until now they lived only in <c>ArcadeRoomService</c>'s in-memory registry, so a pod restart's
    /// rehydrate handed joiners the worker DEFAULT codec (an AV1 track on an H.264 room: binds, shows
    /// nothing) and the default controller scheme. <c>CodecProbe</c> records what the creator's browser
    /// reported when Auto chose the codec — observability for the evidence-gated codec work.
    ///
    /// Applied by hand to the live database (the dev connection IS prod — deploy-db-ops skill) through
    /// the idempotent script sql/AddArcadeSessionCodec.sql, which also records this row in
    /// __EFMigrationsHistory. This class documents the same DDL for the model's sake.
    /// </summary>
    public partial class AddArcadeSessionCodec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VideoCodec", table: "ArcadeSession", type: "nvarchar(20)", maxLength: 20, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "ControllerScheme", table: "ArcadeSession", type: "nvarchar(20)", maxLength: 20, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "CodecProbe", table: "ArcadeSession", type: "nvarchar(80)", maxLength: 80, nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "VideoCodec", table: "ArcadeSession");
            migrationBuilder.DropColumn(name: "ControllerScheme", table: "ArcadeSession");
            migrationBuilder.DropColumn(name: "CodecProbe", table: "ArcadeSession");
        }
    }
}
