using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddRottenTomatoesScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RtNeedsReview",
                table: "Movie",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RtPopcornmeter",
                table: "Movie",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RtReviewReason",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RtScoresUpdatedDate",
                table: "Movie",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RtTomatometer",
                table: "Movie",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RtUrl",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RtNeedsReview",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "RtPopcornmeter",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "RtReviewReason",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "RtScoresUpdatedDate",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "RtTomatometer",
                table: "Movie");

            migrationBuilder.DropColumn(
                name: "RtUrl",
                table: "Movie");
        }
    }
}
