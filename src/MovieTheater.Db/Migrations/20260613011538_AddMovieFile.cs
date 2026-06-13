using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMovieFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MovieFile",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MovieID = table.Column<int>(type: "int", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    JellyfinItemId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DurationTicks = table.Column<long>(type: "bigint", nullable: true),
                    Container = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    VideoCodec = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    AudioCodec = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    LastSyncedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MissingSinceUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieFile", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieFile_Movie_MovieID",
                        column: x => x.MovieID,
                        principalTable: "Movie",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MovieFile_MovieID",
                table: "MovieFile",
                column: "MovieID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MovieFile");
        }
    }
}
