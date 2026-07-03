using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistsAndWatchParty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUserPlaylist",
                table: "Channel",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "WatchpartyStartedUtc",
                table: "Channel",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchpartyToken",
                table: "Channel",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlaylistItem",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChannelId = table.Column<int>(type: "int", nullable: false),
                    PlayableId = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistItem_Channel_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaylistItem_Playable_PlayableId",
                        column: x => x.PlayableId,
                        principalTable: "Playable",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Channel_WatchpartyToken",
                table: "Channel",
                column: "WatchpartyToken",
                unique: true,
                filter: "[WatchpartyToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistItem_ChannelId_Position",
                table: "PlaylistItem",
                columns: new[] { "ChannelId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistItem_PlayableId",
                table: "PlaylistItem",
                column: "PlayableId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaylistItem");

            migrationBuilder.DropIndex(
                name: "IX_Channel_WatchpartyToken",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "IsUserPlaylist",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "WatchpartyStartedUtc",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "WatchpartyToken",
                table: "Channel");
        }
    }
}
