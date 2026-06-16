using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieMetadataEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackdropPath",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BudgetUsd",
                table: "Movie",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RevenueUsd",
                table: "Movie",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tagline",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TmdbId",
                table: "Movie",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TmdbPopularity",
                table: "Movie",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TmdbVoteCount",
                table: "Movie",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrailerKey",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackdropPath",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "BudgetUsd",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "RevenueUsd",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "Tagline",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "TmdbId",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "TmdbPopularity",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "TmdbVoteCount",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "TrailerKey",
                table: "Movie");
        }
    }
}
