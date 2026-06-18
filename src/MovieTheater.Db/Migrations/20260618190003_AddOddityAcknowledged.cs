using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddOddityAcknowledged : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OddityAcknowledgedUtc",
                table: "Series",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OddityAcknowledgedUtc",
                table: "Movie",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OddityAcknowledgedUtc",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "OddityAcknowledgedUtc",
                table: "Movie");
        }
    }
}
