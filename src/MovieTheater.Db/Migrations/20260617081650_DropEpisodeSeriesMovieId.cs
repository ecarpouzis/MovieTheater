using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class DropEpisodeSeriesMovieId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Series-split flip (flip-series, 2026-06-17) already dropped this CASCADE FK on the live DB.
            // Guard so the migration applies cleanly whether the constraint still exists (a fresh rebuild) or
            // was already removed (the live DB).
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Episode_Movie_SeriesMovieId') " +
                "ALTER TABLE [Episode] DROP CONSTRAINT [FK_Episode_Movie_SeriesMovieId];");

            migrationBuilder.DropIndex(
                name: "IX_Episode_SeriesId",
                table: "Episode");

            migrationBuilder.DropIndex(
                name: "IX_Episode_SeriesMovieId_SeasonNumber_EpisodeNumber",
                table: "Episode");

            migrationBuilder.DropColumn(
                name: "SeriesMovieId",
                table: "Episode");

            migrationBuilder.CreateIndex(
                name: "IX_Episode_SeriesId_SeasonNumber_EpisodeNumber",
                table: "Episode",
                columns: new[] { "SeriesId", "SeasonNumber", "EpisodeNumber" },
                unique: true,
                filter: "[SeriesId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Episode_SeriesId_SeasonNumber_EpisodeNumber",
                table: "Episode");

            migrationBuilder.AddColumn<int>(
                name: "SeriesMovieId",
                table: "Episode",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Episode_SeriesId",
                table: "Episode",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_Episode_SeriesMovieId_SeasonNumber_EpisodeNumber",
                table: "Episode",
                columns: new[] { "SeriesMovieId", "SeasonNumber", "EpisodeNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Episode_Movie_SeriesMovieId",
                table: "Episode",
                column: "SeriesMovieId",
                principalTable: "Movie",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
