using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Arcade congestion memory (worker patches 0049/0050, 2026-10-03): <c>ArcadeLinkStat.Descents</c>,
    /// <c>HardDescents</c>, <c>Craters</c>, <c>WallExits</c>, <c>WallSeeds</c>, <c>Plis</c> (int, NOT NULL,
    /// default 0) and <c>OverFirstKbps</c>, <c>OverMaxKbps</c> (int, NOT NULL, default -1 = none) — the room's
    /// congestion-memory counters and the peer's PLI count, sent by the worker on the close row.
    ///
    /// Applied by hand to the live database (the dev connection IS prod — deploy-db-ops skill) through the
    /// idempotent script sql/AddArcadeLinkStatCongestion.sql, which also records this row in
    /// __EFMigrationsHistory. This class documents the same DDL for the model's sake. Must be applied BEFORE
    /// the site deploy that maps these columns (EF inserts every mapped column).
    /// </summary>
    public partial class AddArcadeLinkStatCongestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "Descents", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "HardDescents", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "Craters", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "WallExits", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "WallSeeds", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "OverFirstKbps", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: -1);
            migrationBuilder.AddColumn<int>(name: "OverMaxKbps", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: -1);
            migrationBuilder.AddColumn<int>(name: "Plis", table: "ArcadeLinkStat", type: "int", nullable: false, defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Descents", table: "ArcadeLinkStat");
            migrationBuilder.DropColumn(name: "HardDescents", table: "ArcadeLinkStat");
            migrationBuilder.DropColumn(name: "Craters", table: "ArcadeLinkStat");
            migrationBuilder.DropColumn(name: "WallExits", table: "ArcadeLinkStat");
            migrationBuilder.DropColumn(name: "WallSeeds", table: "ArcadeLinkStat");
            migrationBuilder.DropColumn(name: "OverFirstKbps", table: "ArcadeLinkStat");
            migrationBuilder.DropColumn(name: "OverMaxKbps", table: "ArcadeLinkStat");
            migrationBuilder.DropColumn(name: "Plis", table: "ArcadeLinkStat");
        }
    }
}
