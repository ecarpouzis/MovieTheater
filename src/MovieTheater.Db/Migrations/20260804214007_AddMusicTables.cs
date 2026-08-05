using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MusicArtist",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SortName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FolderName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    YearRange = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicArtist", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MusicPlaylist",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicPlaylist", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicPlaylist_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MusicAlbum",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ArtistId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: true),
                    FolderPath = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    Tag = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HasArt = table.Column<bool>(type: "bit", nullable: false),
                    DominantColor = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicAlbum", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicAlbum_MusicArtist_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "MusicArtist",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MusicTrack",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ArtistId = table.Column<int>(type: "int", nullable: false),
                    AlbumId = table.Column<int>(type: "int", nullable: true),
                    RelativePath = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TrackNo = table.Column<int>(type: "int", nullable: true),
                    DiscNo = table.Column<int>(type: "int", nullable: true),
                    DurationSec = table.Column<double>(type: "float", nullable: true),
                    Codec = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    BitrateKbps = table.Column<int>(type: "int", nullable: true),
                    SampleRateHz = table.Column<int>(type: "int", nullable: true),
                    TagArtist = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TagAlbum = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    HasEmbeddedArt = table.Column<bool>(type: "bit", nullable: false),
                    RequiresTranscode = table.Column<bool>(type: "bit", nullable: false),
                    MissingSinceUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicTrack", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicTrack_MusicAlbum_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "MusicAlbum",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MusicTrack_MusicArtist_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "MusicArtist",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MusicPlaylistItem",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlaylistId = table.Column<int>(type: "int", nullable: false),
                    TrackId = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicPlaylistItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicPlaylistItem_MusicPlaylist_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "MusicPlaylist",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MusicPlaylistItem_MusicTrack_TrackId",
                        column: x => x.TrackId,
                        principalTable: "MusicTrack",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MusicTrackLyrics",
                columns: table => new
                {
                    TrackId = table.Column<int>(type: "int", nullable: false),
                    PlainText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SyncedLrc = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FetchedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicTrackLyrics", x => x.TrackId);
                    table.ForeignKey(
                        name: "FK_MusicTrackLyrics_MusicTrack_TrackId",
                        column: x => x.TrackId,
                        principalTable: "MusicTrack",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbum_ArtistId_Year",
                table: "MusicAlbum",
                columns: new[] { "ArtistId", "Year" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbum_FolderPath",
                table: "MusicAlbum",
                column: "FolderPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicArtist_FolderName",
                table: "MusicArtist",
                column: "FolderName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicArtist_SortName",
                table: "MusicArtist",
                column: "SortName");

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylist_UserId",
                table: "MusicPlaylist",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistItem_PlaylistId_Position",
                table: "MusicPlaylistItem",
                columns: new[] { "PlaylistId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistItem_TrackId",
                table: "MusicPlaylistItem",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_AlbumId_DiscNo_TrackNo",
                table: "MusicTrack",
                columns: new[] { "AlbumId", "DiscNo", "TrackNo" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_ArtistId",
                table: "MusicTrack",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_RelativePath",
                table: "MusicTrack",
                column: "RelativePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_Title",
                table: "MusicTrack",
                column: "Title");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MusicPlaylistItem");

            migrationBuilder.DropTable(
                name: "MusicTrackLyrics");

            migrationBuilder.DropTable(
                name: "MusicPlaylist");

            migrationBuilder.DropTable(
                name: "MusicTrack");

            migrationBuilder.DropTable(
                name: "MusicAlbum");

            migrationBuilder.DropTable(
                name: "MusicArtist");
        }
    }
}
