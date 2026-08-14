using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncCandidateSeriesGrouping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EpisodeNumber",
                table: "SyncCandidate",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumber",
                table: "SyncCandidate",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesFolder",
                table: "SyncCandidate",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpansToEpisode",
                table: "SyncCandidate",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetSeriesId",
                table: "SyncCandidate",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncCandidate_Status_SeriesFolder",
                table: "SyncCandidate",
                columns: new[] { "Status", "SeriesFolder" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncCandidate_TargetSeriesId",
                table: "SyncCandidate",
                column: "TargetSeriesId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncCandidate_Series_TargetSeriesId",
                table: "SyncCandidate",
                column: "TargetSeriesId",
                principalTable: "Series",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncCandidate_Series_TargetSeriesId",
                table: "SyncCandidate");

            migrationBuilder.DropIndex(
                name: "IX_SyncCandidate_Status_SeriesFolder",
                table: "SyncCandidate");

            migrationBuilder.DropIndex(
                name: "IX_SyncCandidate_TargetSeriesId",
                table: "SyncCandidate");

            migrationBuilder.DropColumn(
                name: "EpisodeNumber",
                table: "SyncCandidate");

            migrationBuilder.DropColumn(
                name: "SeasonNumber",
                table: "SyncCandidate");

            migrationBuilder.DropColumn(
                name: "SeriesFolder",
                table: "SyncCandidate");

            migrationBuilder.DropColumn(
                name: "SpansToEpisode",
                table: "SyncCandidate");

            migrationBuilder.DropColumn(
                name: "TargetSeriesId",
                table: "SyncCandidate");
        }
    }
}
