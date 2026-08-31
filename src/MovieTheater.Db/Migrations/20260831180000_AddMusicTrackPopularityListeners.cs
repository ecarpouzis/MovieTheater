using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Banks the RAW listener count behind MusicTrack.Popularity (2026-08-31).
    ///
    /// <para>The 0-100 scale is logarithmic by necessity, and the cost of that is a display problem:
    /// two neighbouring scores can be an enormous audience gap. On one album 73 and 50 are 112,303
    /// listeners and 2,905 - a 39x difference reading as "23 points" - so a tracklist asked how much
    /// of a drop there is between its songs cannot answer from the score alone.</para>
    ///
    /// <para>It also makes the scale re-tunable offline: the ceiling has already been raised once,
    /// and with the counts stored a re-score becomes an UPDATE rather than a re-parse of the whole
    /// response cache.</para>
    ///
    /// <para>Purely additive: one nullable column, no index (nothing sorts or filters by it - the
    /// score does that, and this rides along on rows already being read). Applied to the live DB by
    /// hand (sql/AddMusicTrackPopularityListeners.sql is the same DDL written idempotently).</para>
    /// </summary>
    public partial class AddMusicTrackPopularityListeners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PopularityListeners",
                table: "MusicTrack",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PopularityListeners", table: "MusicTrack");
        }
    }
}
