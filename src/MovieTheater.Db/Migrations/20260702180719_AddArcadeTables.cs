using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArcadeGame",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SortTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    System = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RomPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CloudRetroGameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MaxPlayers = table.Column<byte>(type: "tinyint", nullable: false),
                    RatingCeiling = table.Column<int>(type: "int", nullable: false),
                    BoxArtPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Year = table.Column<int>(type: "int", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcadeGame", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArcadeSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ArcadeGameId = table.Column<int>(type: "int", nullable: false),
                    RoomCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CloudRetroRoomId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcadeSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArcadeSession_ArcadeGame_ArcadeGameId",
                        column: x => x.ArcadeGameId,
                        principalTable: "ArcadeGame",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeGame_System_RomPath",
                table: "ArcadeGame",
                columns: new[] { "System", "RomPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeSession_ArcadeGameId",
                table: "ArcadeSession",
                column: "ArcadeGameId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArcadeSession");

            migrationBuilder.DropTable(
                name: "ArcadeGame");
        }
    }
}
