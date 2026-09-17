using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Books.Db.Migrations.Hot
{
    /// <inheritdoc />
    public partial class SpanRunRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectedEditionSpanRun",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectedEditionSpanRun", x => new { x.ItemId, x.Source, x.Provider });
                    table.ForeignKey(
                        name: "FK_CollectedEditionSpanRun_CollectedEditionSpan_ItemId_Source",
                        columns: x => new { x.ItemId, x.Source },
                        principalTable: "CollectedEditionSpan",
                        principalColumns: new[] { "ItemId", "Source" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectedEditionSpanRun_Provider_ProviderKey",
                table: "CollectedEditionSpanRun",
                columns: new[] { "Provider", "ProviderKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectedEditionSpanRun");
        }
    }
}
