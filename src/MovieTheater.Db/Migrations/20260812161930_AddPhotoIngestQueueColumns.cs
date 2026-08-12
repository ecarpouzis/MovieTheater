using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoIngestQueueColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HashUpdatedUtc",
                table: "PhotoAsset",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IngestError",
                table: "PhotoAsset",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MetadataUpdatedUtc",
                table: "PhotoAsset",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbKey",
                table: "PhotoAsset",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThumbState",
                table: "PhotoAsset",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ThumbVariants",
                table: "PhotoAsset",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ThumbsUpdatedUtc",
                table: "PhotoAsset",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_HashQueue",
                table: "PhotoAsset",
                column: "Id",
                filter: "[HashUpdatedUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_MetadataQueue",
                table: "PhotoAsset",
                column: "Id",
                filter: "[MetadataUpdatedUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAsset_ThumbQueue",
                table: "PhotoAsset",
                column: "Id",
                filter: "[ThumbsUpdatedUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PhotoAsset_HashQueue",
                table: "PhotoAsset");

            migrationBuilder.DropIndex(
                name: "IX_PhotoAsset_MetadataQueue",
                table: "PhotoAsset");

            migrationBuilder.DropIndex(
                name: "IX_PhotoAsset_ThumbQueue",
                table: "PhotoAsset");

            migrationBuilder.DropColumn(
                name: "HashUpdatedUtc",
                table: "PhotoAsset");

            migrationBuilder.DropColumn(
                name: "IngestError",
                table: "PhotoAsset");

            migrationBuilder.DropColumn(
                name: "MetadataUpdatedUtc",
                table: "PhotoAsset");

            migrationBuilder.DropColumn(
                name: "ThumbKey",
                table: "PhotoAsset");

            migrationBuilder.DropColumn(
                name: "ThumbState",
                table: "PhotoAsset");

            migrationBuilder.DropColumn(
                name: "ThumbVariants",
                table: "PhotoAsset");

            migrationBuilder.DropColumn(
                name: "ThumbsUpdatedUtc",
                table: "PhotoAsset");
        }
    }
}
