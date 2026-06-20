using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AiTitleInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TitleInsight",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubjectKind = table.Column<int>(type: "int", nullable: false),
                    SubjectId = table.Column<int>(type: "int", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SpecVersion = table.Column<int>(type: "int", nullable: false),
                    Recognized = table.Column<bool>(type: "bit", nullable: false),
                    Confidence = table.Column<int>(type: "int", nullable: false),
                    Vibe = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WhyInteresting = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WatchIfYouLiked = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PeopleNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Surrealism = table.Column<int>(type: "int", nullable: true),
                    CultClassic = table.Column<int>(type: "int", nullable: true),
                    Intensity = table.Column<int>(type: "int", nullable: true),
                    Novelty = table.Column<int>(type: "int", nullable: true),
                    Rewatchability = table.Column<int>(type: "int", nullable: true),
                    Energy = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleInsight", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TitleTag",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TitleInsightId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Weight = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleTag", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TitleTag_TitleInsight_TitleInsightId",
                        column: x => x.TitleInsightId,
                        principalTable: "TitleInsight",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TitleInsight_SubjectKind_SubjectId",
                table: "TitleInsight",
                columns: new[] { "SubjectKind", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_TitleTag_Category_Value",
                table: "TitleTag",
                columns: new[] { "Category", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_TitleTag_TitleInsightId",
                table: "TitleTag",
                column: "TitleInsightId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TitleTag");

            migrationBuilder.DropTable(
                name: "TitleInsight");
        }
    }
}
