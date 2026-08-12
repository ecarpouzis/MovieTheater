using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoCurationBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotoCurationBatch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    BatchId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedByUserId = table.Column<int>(type: "int", nullable: true),
                    AppliedCount = table.Column<int>(type: "int", nullable: false),
                    Cursor = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Complete = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoCurationBatch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoCurationBatch_Users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhotoCurationBatchItem",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhotoCurationBatchId = table.Column<int>(type: "int", nullable: false),
                    PhotoAssetId = table.Column<int>(type: "int", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(850)", maxLength: 850, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Rule = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoCurationBatchItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoCurationBatchItem_PhotoAsset_PhotoAssetId",
                        column: x => x.PhotoAssetId,
                        principalTable: "PhotoAsset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhotoCurationBatchItem_PhotoCurationBatch_PhotoCurationBatchId",
                        column: x => x.PhotoCurationBatchId,
                        principalTable: "PhotoCurationBatch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoCurationBatch_DecidedByUserId",
                table: "PhotoCurationBatch",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoCurationBatch_Kind_BatchId",
                table: "PhotoCurationBatch",
                columns: new[] { "Kind", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoCurationBatch_Kind_Status_CreatedUtc",
                table: "PhotoCurationBatch",
                columns: new[] { "Kind", "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoCurationBatchItem_PhotoAssetId",
                table: "PhotoCurationBatchItem",
                column: "PhotoAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoCurationBatchItem_PhotoCurationBatchId_PhotoAssetId",
                table: "PhotoCurationBatchItem",
                columns: new[] { "PhotoCurationBatchId", "PhotoAssetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotoCurationBatchItem");

            migrationBuilder.DropTable(
                name: "PhotoCurationBatch");
        }
    }
}
