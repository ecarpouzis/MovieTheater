using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Play telemetry for the music vertical (R9 closing pass) — the data "Most played" was missing.
    /// Purely additive: one table, two indexes, nothing existing rewritten and nothing deleted, so an
    /// unapplied deploy simply has no plays.
    ///
    /// <para><b>An aggregate, not an event log.</b> One row per (user, track) with a count and two
    /// stamps. "Most played album/artist" has to ride the shelf rows the browse already fetches (the
    /// per-shelf fetch rule), so it must be a SUM over a table bounded by
    /// listeners × tracks-ever-played — not a COUNT over a row per play forever, which is the shape
    /// that eventually needs pruning and a rollup job. "Recently played" is a MAX over the same rows,
    /// so it comes free.</para>
    ///
    /// <para><b>LastStartedUtc is the idempotency key</b>: the client sends the moment playback
    /// started, floored to the minute, and a report carrying a minute already recorded is a no-op —
    /// which is what lets the beacon be fire-and-forget across a retry, a <c>pagehide</c> flush and
    /// two tabs.</para>
    ///
    /// <para>Applied to the live DB by hand on 2026-08-27 via SqlConnection
    /// (sql/AddMusicPlayStats.sql is the same DDL written idempotently).</para>
    /// </summary>
    public partial class AddMusicPlayStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MusicPlayStat",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    MusicTrackId = table.Column<int>(type: "int", nullable: false),
                    PlayCount = table.Column<int>(type: "int", nullable: false),
                    LastPlayedUtc = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                    LastStartedUtc = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicPlayStat", x => x.Id);
                    // Both RESTRICT: Users for the multiple-cascade-path reason MusicPlaylist
                    // documents, MusicTrack for the reason MusicTrack's own parents are restricted —
                    // a play is a fact about listening, not a property of a content row.
                    table.ForeignKey(
                        name: "FK_MusicPlayStat_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MusicPlayStat_MusicTrack_MusicTrackId",
                        column: x => x.MusicTrackId,
                        principalTable: "MusicTrack",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlayStat_UserId_MusicTrackId",
                table: "MusicPlayStat",
                columns: new[] { "UserId", "MusicTrackId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlayStat_MusicTrackId",
                table: "MusicPlayStat",
                column: "MusicTrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MusicPlayStat");
        }
    }
}
