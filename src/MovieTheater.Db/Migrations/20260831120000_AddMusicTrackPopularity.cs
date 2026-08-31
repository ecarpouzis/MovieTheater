using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Track-level popularity (2026-08-31) — the number that answers "which songs on this record are
    /// the famous ones", which the album's own popularity never could.
    ///
    /// <para>Purely additive: three nullable columns and two indexes on an existing table, nothing
    /// rewritten and nothing deleted, so an unapplied deploy simply shows no track popularity. The
    /// columns mirror <c>MusicAlbum</c>'s three exactly (value / source / checked-stamp) because they
    /// are the same fact at a smaller scale and are filled by the same kind of resumable pass.</para>
    ///
    /// <para><b>The indexes are the two shapes the feature reads in.</b>
    /// <c>(PopularityCheckedUtc, Id)</c> is the <c>music-track-popularity</c> queue — "never asked",
    /// in cursor order — so a batch is a range scan rather than a sort over 60,797 rows. It leads on
    /// the stamp rather than being FILTERED on it because a filtered index needs
    /// <c>QUOTED_IDENTIFIER ON</c> at every writer and sqlcmd defaults it OFF, a trap this database
    /// has been bitten by before. <c>(ArtistId, Popularity)</c> is the artist page's "Most popular"
    /// read, which is a top-10 over one artist.</para>
    ///
    /// <para>Applied to the live DB by hand on 2026-08-31 via SqlConnection
    /// (sql/AddMusicTrackPopularity.sql is the same DDL written idempotently).</para>
    /// </summary>
    public partial class AddMusicTrackPopularity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Popularity",
                table: "MusicTrack",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PopularitySource",
                table: "MusicTrack",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<System.DateTime>(
                name: "PopularityCheckedUtc",
                table: "MusicTrack",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_PopularityCheckedUtc_Id",
                table: "MusicTrack",
                columns: new[] { "PopularityCheckedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_ArtistId_Popularity",
                table: "MusicTrack",
                columns: new[] { "ArtistId", "Popularity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_MusicTrack_ArtistId_Popularity", table: "MusicTrack");
            migrationBuilder.DropIndex(name: "IX_MusicTrack_PopularityCheckedUtc_Id", table: "MusicTrack");
            migrationBuilder.DropColumn(name: "PopularityCheckedUtc", table: "MusicTrack");
            migrationBuilder.DropColumn(name: "PopularitySource", table: "MusicTrack");
            migrationBuilder.DropColumn(name: "Popularity", table: "MusicTrack");
        }
    }
}
