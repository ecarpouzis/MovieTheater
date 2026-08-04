using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeLinkStat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArcadeLinkStat",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    System = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Codec = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CeilingKbps = table.Column<int>(type: "int", nullable: false),
                    OpenKbps = table.Column<int>(type: "int", nullable: false),
                    SustainedKbps = table.Column<int>(type: "int", nullable: false),
                    RampTicks = table.Column<int>(type: "int", nullable: true),
                    AtCeilPct = table.Column<int>(type: "int", nullable: false),
                    CutsSteady = table.Column<int>(type: "int", nullable: false),
                    StarvesSteady = table.Column<int>(type: "int", nullable: false),
                    CongEpisodes = table.Column<int>(type: "int", nullable: false),
                    RttMeanMs = table.Column<double>(type: "float", nullable: false),
                    RttSdMs = table.Column<double>(type: "float", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcadeLinkStat", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeLinkStat_UserId_DeviceId_CreatedUtc",
                table: "ArcadeLinkStat",
                columns: new[] { "UserId", "DeviceId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArcadeLinkStat");
        }
    }
}
