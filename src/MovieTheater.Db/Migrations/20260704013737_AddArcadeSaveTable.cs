using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeSaveTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArcadeSave",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    ArcadeGameId = table.Column<int>(type: "int", nullable: false),
                    System = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SlotId = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CoreName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CoreVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    StorageRelPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IsAutosave = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcadeSave", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArcadeSave_ArcadeGame_ArcadeGameId",
                        column: x => x.ArcadeGameId,
                        principalTable: "ArcadeGame",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeSave_ArcadeGameId",
                table: "ArcadeSave",
                column: "ArcadeGameId");

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeSave_UserId_ArcadeGameId_Kind_SlotId",
                table: "ArcadeSave",
                columns: new[] { "UserId", "ArcadeGameId", "Kind", "SlotId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArcadeSave");
        }
    }
}
