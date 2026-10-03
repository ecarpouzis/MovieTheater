using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Arcade codec program (2026-10-02): <c>ArcadeLinkStat.DistressWeakTicks</c>, <c>DistressStrongTicks</c>
    /// and <c>ScaleDowns</c> (int, NOT NULL, default 0) — each peer's decoder-distress record for the room,
    /// sent by the worker on the close row. The per-device codec history the Auto codec choice learns from.
    ///
    /// Applied by hand to the live database (the dev connection IS prod — deploy-db-ops skill) through the
    /// idempotent script sql/AddArcadeLinkStatDistress.sql, which also records this row in
    /// __EFMigrationsHistory. This class documents the same DDL for the model's sake.
    /// </summary>
    public partial class AddArcadeLinkStatDistress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "DistressWeakTicks", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "DistressStrongTicks", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "ScaleDowns", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "DistressWeakTicks", table: "ArcadeLinkStat");
            migrationBuilder.DropColumn(name: "DistressStrongTicks", table: "ArcadeLinkStat");
            migrationBuilder.DropColumn(name: "ScaleDowns", table: "ArcadeLinkStat");
        }
    }
}
