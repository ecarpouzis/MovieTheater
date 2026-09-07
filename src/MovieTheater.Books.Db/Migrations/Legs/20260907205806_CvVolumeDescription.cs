using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Books.Db.Migrations.Legs
{
    /// <inheritdoc />
    public partial class CvVolumeDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CvVolumeDescription",
                columns: table => new
                {
                    CvVolumeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    HasCollectedBlock = table.Column<int>(type: "INTEGER", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvVolumeDescription", x => x.CvVolumeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CvVolumeDescription_HasCollectedBlock",
                table: "CvVolumeDescription",
                column: "HasCollectedBlock");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CvVolumeDescription");
        }
    }
}
