using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class PersonalizedRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: Channel.CachedCeiling and the ChannelShelf table already exist in the live DB
            // (added to the model without their own migration — pre-existing snapshot drift). EF folded
            // them into this migration; they are intentionally omitted here so applying it only creates
            // the two recommendation tables. The model snapshot still records them, so no future
            // migration will try to re-add them.
            migrationBuilder.CreateTable(
                name: "TitleRecommendation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    SubjectKind = table.Column<int>(type: "int", nullable: false),
                    SubjectId = table.Column<int>(type: "int", nullable: false),
                    Score = table.Column<double>(type: "float", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    ReasonText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AlgoVersion = table.Column<int>(type: "int", nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleRecommendation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserTasteProfile",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    ProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RatingsStamp = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    GeneratedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTasteProfile", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TitleRecommendation_UserId_SubjectKind_SubjectId",
                table: "TitleRecommendation",
                columns: new[] { "UserId", "SubjectKind", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTasteProfile_UserId",
                table: "UserTasteProfile",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TitleRecommendation");

            migrationBuilder.DropTable(
                name: "UserTasteProfile");
        }
    }
}
