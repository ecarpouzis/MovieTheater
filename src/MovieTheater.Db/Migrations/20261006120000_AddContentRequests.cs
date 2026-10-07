using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// The request queue (2026-10-06): <c>dbo.ContentRequest</c> (one row per "please add this", from any
    /// signed-in user, with the section it is for, the matcher's standing proposal and the resolution) and
    /// <c>dbo.ContentRequestVote</c> (one "me too" per user per request).
    ///
    /// Applied by hand to the live database (the dev connection IS prod — deploy-db-ops skill) through the
    /// idempotent script sql/AddContentRequests.sql, which also records this row in __EFMigrationsHistory.
    /// This class documents the same DDL for the model's sake. Purely additive: nothing the deployed site
    /// maps changes, so it can land before the deploy that reads it.
    /// </summary>
    public partial class AddContentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentRequest",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    Section = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Link = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedByUserId = table.Column<int>(type: "int", nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MatchKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    MatchId = table.Column<int>(type: "int", nullable: true),
                    MatchTitle = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MatchFoundUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DismissedMatchKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    FulfilledKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    FulfilledId = table.Column<int>(type: "int", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentRequest", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentRequest_Users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentRequest_Status_CreatedUtc",
                table: "ContentRequest",
                columns: new[] { "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentRequest_RequestedByUserId",
                table: "ContentRequest",
                column: "RequestedByUserId");

            migrationBuilder.CreateTable(
                name: "ContentRequestVote",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentRequestVote", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentRequestVote_ContentRequest_RequestId",
                        column: x => x.RequestId,
                        principalTable: "ContentRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentRequestVote_RequestId_UserId",
                table: "ContentRequestVote",
                columns: new[] { "RequestId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ContentRequestVote");
            migrationBuilder.DropTable(name: "ContentRequest");
        }
    }
}
