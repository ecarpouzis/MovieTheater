using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyframeCustody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentFingerprint",
                table: "MediaFile",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaKeyframes",
                columns: table => new
                {
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TotalDurationTicks = table.Column<long>(type: "bigint", nullable: false),
                    KeyframeTicks = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SourceItemId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaKeyframes", x => x.Fingerprint);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaFile_ContentFingerprint",
                table: "MediaFile",
                column: "ContentFingerprint",
                filter: "[ContentFingerprint] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaKeyframes");

            migrationBuilder.DropIndex(
                name: "IX_MediaFile_ContentFingerprint",
                table: "MediaFile");

            migrationBuilder.DropColumn(
                name: "ContentFingerprint",
                table: "MediaFile");
        }
    }
}
