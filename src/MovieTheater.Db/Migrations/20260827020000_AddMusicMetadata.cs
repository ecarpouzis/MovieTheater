using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// The music metadata the Music rail, groups and sorts were designed around (R9 S10). Purely
    /// additive: two nullable columns on MusicTrack, three on MusicAlbum, three new tables. Nothing
    /// existing is rewritten and no row is deleted, so an unapplied deploy simply has no genre.
    ///
    /// <para>Three legs feed it. <c>music-genres</c> reads the files' own genre frames into
    /// MusicTrack.Genre and rolls them up into MusicAlbumGenre / MusicArtistGenre with
    /// Source='tags'; <c>music-enrich</c> adds MusicBrainz / Last.fm rows and MusicAlbum.Popularity;
    /// listeners write MusicAlbumRating themselves through /API/Music/Rating. The Source column is
    /// part of each genre row's identity, which is what lets the three passes coexist and each be
    /// re-runnable.</para>
    ///
    /// <para>Applied to the live DB by hand on 2026-08-27 via SqlConnection
    /// (sql/AddMusicMetadata.sql is the same DDL written idempotently).</para>
    /// </summary>
    public partial class AddMusicMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Genre",
                table: "MusicTrack",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<System.DateTime>(
                name: "GenreCheckedUtc",
                table: "MusicTrack",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Popularity",
                table: "MusicAlbum",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PopularitySource",
                table: "MusicAlbum",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<System.DateTime>(
                name: "PopularityCheckedUtc",
                table: "MusicAlbum",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MusicAlbumGenre",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlbumId = table.Column<int>(type: "int", nullable: false),
                    Genre = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Weight = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicAlbumGenre", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicAlbumGenre_MusicAlbum_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "MusicAlbum",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MusicArtistGenre",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ArtistId = table.Column<int>(type: "int", nullable: false),
                    Genre = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Weight = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicArtistGenre", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicArtistGenre_MusicArtist_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "MusicArtist",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MusicAlbumRating",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    AlbumId = table.Column<int>(type: "int", nullable: false),
                    Score = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicAlbumRating", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicAlbumRating_MusicAlbum_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "MusicAlbum",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MusicAlbumRating_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbumGenre_AlbumId_Source_Genre",
                table: "MusicAlbumGenre",
                columns: new[] { "AlbumId", "Source", "Genre" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbumGenre_Genre",
                table: "MusicAlbumGenre",
                column: "Genre");

            migrationBuilder.CreateIndex(
                name: "IX_MusicArtistGenre_ArtistId_Source_Genre",
                table: "MusicArtistGenre",
                columns: new[] { "ArtistId", "Source", "Genre" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicArtistGenre_Genre",
                table: "MusicArtistGenre",
                column: "Genre");

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbumRating_UserId_AlbumId",
                table: "MusicAlbumRating",
                columns: new[] { "UserId", "AlbumId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbumRating_AlbumId",
                table: "MusicAlbumRating",
                column: "AlbumId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MusicAlbumRating");
            migrationBuilder.DropTable(name: "MusicArtistGenre");
            migrationBuilder.DropTable(name: "MusicAlbumGenre");
            migrationBuilder.DropColumn(name: "PopularityCheckedUtc", table: "MusicAlbum");
            migrationBuilder.DropColumn(name: "PopularitySource", table: "MusicAlbum");
            migrationBuilder.DropColumn(name: "Popularity", table: "MusicAlbum");
            migrationBuilder.DropColumn(name: "GenreCheckedUtc", table: "MusicTrack");
            migrationBuilder.DropColumn(name: "Genre", table: "MusicTrack");
        }
    }
}
