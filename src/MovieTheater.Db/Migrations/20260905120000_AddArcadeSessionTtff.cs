using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Arcade perf program P1 (2026-09-05): one nullable column, <c>ArcadeSession.TtffMs</c> — the room's
    /// time-to-first-frame in ms as the creator's browser measured it (opening the signaling socket to the
    /// first presented video frame: ROM staging + core load + boot + WebRTC setup + first keyframe).
    /// Reported once on a heartbeat; the first sane value wins; observability only. Until this column the
    /// stack had no number for how long "Connecting…" really took, so the time-to-first-frame phases of
    /// the program could not be judged.
    ///
    /// Applied by hand to the live database (the dev connection IS prod — deploy-db-ops skill) through
    /// the idempotent script sql/AddArcadeSessionTtff.sql, which also records this row in
    /// __EFMigrationsHistory. This class documents the same DDL for the model's sake.
    /// </summary>
    public partial class AddArcadeSessionTtff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TtffMs", table: "ArcadeSession", type: "int", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TtffMs", table: "ArcadeSession");
        }
    }
}
