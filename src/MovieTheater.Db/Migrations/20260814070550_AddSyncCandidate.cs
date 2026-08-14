using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncCandidate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncCandidate",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    JellyfinItemId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    TargetMovieId = table.Column<int>(type: "int", nullable: true),
                    Signal = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OldPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ParsedTitle = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ParsedYear = table.Column<int>(type: "int", nullable: true),
                    ResolvedImdbId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ResolutionError = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedMovieId = table.Column<int>(type: "int", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCandidate", x => x.Id);
                    // NO ACTION on purpose: SQL Server rejects two SET NULL paths into Movie from one
                    // table; DeleteMovieSubtreeAsync clears candidate references before a movie delete.
                    table.ForeignKey(
                        name: "FK_SyncCandidate_Movie_CreatedMovieId",
                        column: x => x.CreatedMovieId,
                        principalTable: "Movie",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_SyncCandidate_Movie_TargetMovieId",
                        column: x => x.TargetMovieId,
                        principalTable: "Movie",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncCandidate_CreatedMovieId",
                table: "SyncCandidate",
                column: "CreatedMovieId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncCandidate_Status_Kind",
                table: "SyncCandidate",
                columns: new[] { "Status", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncCandidate_TargetMovieId",
                table: "SyncCandidate",
                column: "TargetMovieId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncCandidate");
        }
    }
}
