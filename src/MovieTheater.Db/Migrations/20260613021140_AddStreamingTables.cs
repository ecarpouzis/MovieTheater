using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddStreamingTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Channel",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    FilterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Seed = table.Column<int>(type: "int", nullable: false),
                    ShuffleMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AnchorUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MoviePlaybackProgress",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserID = table.Column<int>(type: "int", nullable: false),
                    MovieID = table.Column<int>(type: "int", nullable: false),
                    PositionTicks = table.Column<long>(type: "bigint", nullable: false),
                    DurationTicks = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Completed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoviePlaybackProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoviePlaybackProgress_Movie_MovieID",
                        column: x => x.MovieID,
                        principalTable: "Movie",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MoviePlaybackProgress_Users_UserID",
                        column: x => x.UserID,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelScheduleItem",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChannelId = table.Column<int>(type: "int", nullable: false),
                    MovieID = table.Column<int>(type: "int", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelScheduleItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelScheduleItem_Channel_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelScheduleItem_Movie_MovieID",
                        column: x => x.MovieID,
                        principalTable: "Movie",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelScheduleItem_ChannelId_StartUtc",
                table: "ChannelScheduleItem",
                columns: new[] { "ChannelId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelScheduleItem_MovieID",
                table: "ChannelScheduleItem",
                column: "MovieID");

            migrationBuilder.CreateIndex(
                name: "IX_MoviePlaybackProgress_MovieID",
                table: "MoviePlaybackProgress",
                column: "MovieID");

            migrationBuilder.CreateIndex(
                name: "IX_MoviePlaybackProgress_UserID_MovieID",
                table: "MoviePlaybackProgress",
                columns: new[] { "UserID", "MovieID" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelScheduleItem");

            migrationBuilder.DropTable(
                name: "MoviePlaybackProgress");

            migrationBuilder.DropTable(
                name: "Channel");
        }
    }
}
