using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Multi-source track popularity (2026-08-31): one row per (track, source), plus the consensus
    /// columns a library-wide ranking is read from.
    ///
    /// <para><b>A table rather than a column per service</b>, because Source is part of the unique
    /// key — each pass then owns and REPLACES only its own rows and any number of services coexist
    /// without a "who wrote this last" column. That is the shape MusicAlbumGenre already uses, for
    /// the same reason.</para>
    ///
    /// <para><b>Score is a PERCENTILE within its own source</b>, not the service's raw number:
    /// Last.fm counts listeners (1 … 4.2M), Deezer publishes an internal rank (~0 … 1M) and
    /// ListenBrainz counts listens from a far smaller crowd, so averaging the raw values would be
    /// meaningless. RawValue keeps each service's own figure so a re-scale never costs another
    /// request.</para>
    ///
    /// <para>Purely additive: a new table and two nullable/defaulted columns; existing rows keep
    /// NULL, which reads as "no source has scored this yet". Applied to the live DB by hand
    /// (sql/AddMusicTrackScores.sql is the same DDL written idempotently).</para>
    /// </summary>
    public partial class AddMusicTrackScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PopularityRank", table: "MusicTrack", type: "int", nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PopularityRankSources", table: "MusicTrack", type: "int",
                nullable: false, defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MusicTrackScore",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MusicTrackId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Score = table.Column<int>(type: "int", nullable: false),
                    RawValue = table.Column<long>(type: "bigint", nullable: true),
                    CheckedUtc = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicTrackScore", x => x.Id);
                    // CASCADE, unlike MusicTrack's own parents: a score is a LABEL on a track rather
                    // than content, so it should not outlive the row it describes.
                    table.ForeignKey(
                        name: "FK_MusicTrackScore_MusicTrack_MusicTrackId",
                        column: x => x.MusicTrackId,
                        principalTable: "MusicTrack",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrackScore_MusicTrackId_Source",
                table: "MusicTrackScore",
                columns: new[] { "MusicTrackId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrackScore_Source", table: "MusicTrackScore", column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_PopularityRank", table: "MusicTrack", column: "PopularityRank");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MusicTrackScore");
            migrationBuilder.DropIndex(name: "IX_MusicTrack_PopularityRank", table: "MusicTrack");
            migrationBuilder.DropColumn(name: "PopularityRankSources", table: "MusicTrack");
            migrationBuilder.DropColumn(name: "PopularityRank", table: "MusicTrack");
        }
    }
}
