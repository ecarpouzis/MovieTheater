using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class DropSampledKeyframeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyframeIntervalSeconds",
                table: "MediaFile");

            migrationBuilder.DropColumn(
                name: "KeyframeMinSeconds",
                table: "MediaFile");

            migrationBuilder.DropColumn(
                name: "KeyframeProbedUtc",
                table: "MediaFile");

            migrationBuilder.DropColumn(
                name: "KeyframeSampleDetail",
                table: "MediaFile");

            migrationBuilder.DropColumn(
                name: "KeyframeSpacingCensored",
                table: "MediaFile");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "KeyframeIntervalSeconds",
                table: "MediaFile",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "KeyframeMinSeconds",
                table: "MediaFile",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "KeyframeProbedUtc",
                table: "MediaFile",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyframeSampleDetail",
                table: "MediaFile",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KeyframeSpacingCensored",
                table: "MediaFile",
                type: "bit",
                nullable: true);
        }
    }
}
