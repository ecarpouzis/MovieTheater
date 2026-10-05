using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// <c>ArcadeGame.Controls</c> (nvarchar(24), NULL) — the cabinet's player-1 control profile
    /// (<c>joy4/1</c>, <c>joy8/6/sf</c>, <c>trackball/1</c>, …) the touch pad's arcade presets read, written from
    /// MAME's -listxml by the <c>arcade-controls</c> CLI.
    ///
    /// Applied by hand to the live database (the dev connection IS prod — deploy-db-ops skill) through the
    /// idempotent script sql/AddArcadeGameControls.sql, which also records this row in __EFMigrationsHistory.
    /// This class documents the same DDL for the model's sake. Must be applied BEFORE the site deploy that
    /// maps the column (EF selects every mapped column, so every ArcadeGames read would fail without it).
    /// </summary>
    public partial class AddArcadeGameControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Controls", table: "ArcadeGame", type: "nvarchar(24)", maxLength: 24, nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Controls", table: "ArcadeGame");
        }
    }
}
