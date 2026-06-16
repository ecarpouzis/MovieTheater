using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Phase-4 structural cutover (docs/metadata-enrichment-plan.md §3.1). Introduces the
    /// <c>Playable</c> parent and <c>Episode</c>/<c>Series</c> tables, and repoints the streamable/
    /// schedulable surface (<c>MovieFile</c>→<c>MediaFile</c>, <c>MoviePlaybackProgress</c>,
    /// <c>ChannelScheduleItem</c>) from <c>Movie.id</c> to <c>Playable.Id</c>.
    ///
    /// Hand-tuned to be DATA-PRESERVING (EF's generated version would drop MovieFile's 5,700+ synced
    /// rows and leave the repointed columns holding movie-ids). The order is: create Playable, give
    /// every Movie a Playable, then for each repointed table carry its rows over and remap MovieID →
    /// the movie's PlayableId before swapping the FK. Reversible; a full DB backup is taken first.
    /// </summary>
    public partial class Phase4PlayableCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Parent + new tables (additive) ──────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "Playable",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playable", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    MovieId = table.Column<int>(type: "int", nullable: false),
                    SeasonCount = table.Column<int>(type: "int", nullable: true),
                    EpisodeCount = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartYear = table.Column<int>(type: "int", nullable: true),
                    EndYear = table.Column<int>(type: "int", nullable: true),
                    Network = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.MovieId);
                    table.ForeignKey(
                        name: "FK_Series_Movie_MovieId",
                        column: x => x.MovieId,
                        principalTable: "Movie",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Episode",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SeriesMovieId = table.Column<int>(type: "int", nullable: false),
                    PlayableId = table.Column<int>(type: "int", nullable: true),
                    SeasonNumber = table.Column<int>(type: "int", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ImdbId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    AirDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RuntimeMinutes = table.Column<int>(type: "int", nullable: true),
                    Plot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImdbRating = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    StillPath = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Episode", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Episode_Movie_SeriesMovieId",
                        column: x => x.SeriesMovieId,
                        principalTable: "Movie",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Episode_Playable_PlayableId",
                        column: x => x.PlayableId,
                        principalTable: "Playable",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Episode_PlayableId",
                table: "Episode",
                column: "PlayableId",
                unique: true,
                filter: "[PlayableId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Episode_SeriesMovieId_SeasonNumber_EpisodeNumber",
                table: "Episode",
                columns: new[] { "SeriesMovieId", "SeasonNumber", "EpisodeNumber" },
                unique: true);

            // ── 2. Movie.PlayableId + backfill one Playable(Kind=Movie) per existing Movie ───────
            migrationBuilder.AddColumn<int>(
                name: "PlayableId",
                table: "Movie",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(@"
                CREATE TABLE #pmap (MovieId int PRIMARY KEY, PlayableId int);
                MERGE INTO dbo.Playable AS tgt
                USING (SELECT id FROM dbo.Movie) AS src
                ON 1 = 0
                WHEN NOT MATCHED THEN INSERT (Kind) VALUES (0)
                OUTPUT src.id, inserted.Id INTO #pmap (MovieId, PlayableId);
                UPDATE m SET m.PlayableId = pm.PlayableId
                    FROM dbo.Movie m JOIN #pmap pm ON m.id = pm.MovieId;
                DROP TABLE #pmap;");

            migrationBuilder.CreateIndex(
                name: "IX_Movie_PlayableId",
                table: "Movie",
                column: "PlayableId",
                unique: true,
                filter: "[PlayableId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Movie_Playable_PlayableId",
                table: "Movie",
                column: "PlayableId",
                principalTable: "Playable",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ── 3. MoviePlaybackProgress: carry rows over, remap MovieID → PlayableId ────────────
            migrationBuilder.DropForeignKey(
                name: "FK_MoviePlaybackProgress_Movie_MovieID",
                table: "MoviePlaybackProgress");
            migrationBuilder.RenameColumn(
                name: "MovieID", table: "MoviePlaybackProgress", newName: "PlayableId");
            migrationBuilder.RenameIndex(
                name: "IX_MoviePlaybackProgress_UserID_MovieID",
                table: "MoviePlaybackProgress",
                newName: "IX_MoviePlaybackProgress_UserID_PlayableId");
            migrationBuilder.RenameIndex(
                name: "IX_MoviePlaybackProgress_MovieID",
                table: "MoviePlaybackProgress",
                newName: "IX_MoviePlaybackProgress_PlayableId");
            // The renamed column still holds the old movie ids — remap each to that movie's PlayableId.
            migrationBuilder.Sql(@"
                UPDATE pp SET pp.PlayableId = m.PlayableId
                FROM dbo.MoviePlaybackProgress pp JOIN dbo.Movie m ON m.id = pp.PlayableId;");
            migrationBuilder.AddForeignKey(
                name: "FK_MoviePlaybackProgress_Playable_PlayableId",
                table: "MoviePlaybackProgress",
                column: "PlayableId",
                principalTable: "Playable",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // ── 4. ChannelScheduleItem: carry rows over, remap MovieID → PlayableId ──────────────
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelScheduleItem_Movie_MovieID",
                table: "ChannelScheduleItem");
            migrationBuilder.RenameColumn(
                name: "MovieID", table: "ChannelScheduleItem", newName: "PlayableId");
            migrationBuilder.RenameIndex(
                name: "IX_ChannelScheduleItem_MovieID",
                table: "ChannelScheduleItem",
                newName: "IX_ChannelScheduleItem_PlayableId");
            migrationBuilder.Sql(@"
                UPDATE csi SET csi.PlayableId = m.PlayableId
                FROM dbo.ChannelScheduleItem csi JOIN dbo.Movie m ON m.id = csi.PlayableId;");
            migrationBuilder.AddForeignKey(
                name: "FK_ChannelScheduleItem_Playable_PlayableId",
                table: "ChannelScheduleItem",
                column: "PlayableId",
                principalTable: "Playable",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ── 5. MovieFile → MediaFile (rename preserves the 5,700+ synced rows), repoint to Playable ──
            migrationBuilder.RenameTable(name: "MovieFile", newName: "MediaFile");
            migrationBuilder.Sql("EXEC sp_rename N'PK_MovieFile', N'PK_MediaFile';");

            migrationBuilder.AddColumn<int>(name: "PlayableId", table: "MediaFile", type: "int", nullable: true);
            migrationBuilder.AddColumn<int>(name: "Role", table: "MediaFile", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "PartNumber", table: "MediaFile", type: "int", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Label", table: "MediaFile", type: "nvarchar(128)", maxLength: 128, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "IsHdr", table: "MediaFile", type: "bit", nullable: true);
            migrationBuilder.AddColumn<string>(name: "HdrFormat", table: "MediaFile", type: "nvarchar(16)", maxLength: 16, nullable: true);
            migrationBuilder.AddColumn<string>(name: "AudioLayout", table: "MediaFile", type: "nvarchar(16)", maxLength: 16, nullable: true);
            migrationBuilder.AddColumn<int>(name: "AudioChannels", table: "MediaFile", type: "int", nullable: true);
            migrationBuilder.AddColumn<double>(name: "FrameRate", table: "MediaFile", type: "float", nullable: true);
            migrationBuilder.AddColumn<int>(name: "BitDepth", table: "MediaFile", type: "int", nullable: true);

            migrationBuilder.Sql(@"
                UPDATE mf SET mf.PlayableId = m.PlayableId
                FROM dbo.MediaFile mf JOIN dbo.Movie m ON m.id = mf.MovieID;");

            migrationBuilder.DropForeignKey(name: "FK_MovieFile_Movie_MovieID", table: "MediaFile");
            migrationBuilder.DropIndex(name: "IX_MovieFile_MovieID", table: "MediaFile");
            migrationBuilder.DropColumn(name: "MovieID", table: "MediaFile");

            migrationBuilder.AlterColumn<int>(
                name: "PlayableId", table: "MediaFile", type: "int", nullable: false,
                oldClrType: typeof(int), oldType: "int", oldNullable: true);

            migrationBuilder.CreateIndex(name: "IX_MediaFile_PlayableId", table: "MediaFile", column: "PlayableId");
            migrationBuilder.AddForeignKey(
                name: "FK_MediaFile_Playable_PlayableId",
                table: "MediaFile",
                column: "PlayableId",
                principalTable: "Playable",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse of §5: MediaFile → MovieFile, repoint Playable → Movie (re-derive MovieID).
            migrationBuilder.DropForeignKey(name: "FK_MediaFile_Playable_PlayableId", table: "MediaFile");
            migrationBuilder.DropIndex(name: "IX_MediaFile_PlayableId", table: "MediaFile");
            migrationBuilder.AddColumn<int>(name: "MovieID", table: "MediaFile", type: "int", nullable: true);
            migrationBuilder.Sql(@"
                UPDATE mf SET mf.MovieID = m.id
                FROM dbo.MediaFile mf JOIN dbo.Movie m ON m.PlayableId = mf.PlayableId;");
            migrationBuilder.DropColumn(name: "PlayableId", table: "MediaFile");
            migrationBuilder.DropColumn(name: "Role", table: "MediaFile");
            migrationBuilder.DropColumn(name: "PartNumber", table: "MediaFile");
            migrationBuilder.DropColumn(name: "Label", table: "MediaFile");
            migrationBuilder.DropColumn(name: "IsHdr", table: "MediaFile");
            migrationBuilder.DropColumn(name: "HdrFormat", table: "MediaFile");
            migrationBuilder.DropColumn(name: "AudioLayout", table: "MediaFile");
            migrationBuilder.DropColumn(name: "AudioChannels", table: "MediaFile");
            migrationBuilder.DropColumn(name: "FrameRate", table: "MediaFile");
            migrationBuilder.DropColumn(name: "BitDepth", table: "MediaFile");
            migrationBuilder.AlterColumn<int>(
                name: "MovieID", table: "MediaFile", type: "int", nullable: false,
                oldClrType: typeof(int), oldType: "int", oldNullable: true);
            migrationBuilder.Sql("EXEC sp_rename N'PK_MediaFile', N'PK_MovieFile';");
            migrationBuilder.RenameTable(name: "MediaFile", newName: "MovieFile");
            migrationBuilder.CreateIndex(name: "IX_MovieFile_MovieID", table: "MovieFile", column: "MovieID");
            migrationBuilder.AddForeignKey(
                name: "FK_MovieFile_Movie_MovieID", table: "MovieFile", column: "MovieID",
                principalTable: "Movie", principalColumn: "id", onDelete: ReferentialAction.Cascade);

            // Reverse §4: ChannelScheduleItem PlayableId → MovieID.
            migrationBuilder.DropForeignKey(name: "FK_ChannelScheduleItem_Playable_PlayableId", table: "ChannelScheduleItem");
            migrationBuilder.Sql(@"
                UPDATE csi SET csi.PlayableId = m.id
                FROM dbo.ChannelScheduleItem csi JOIN dbo.Movie m ON m.PlayableId = csi.PlayableId;");
            migrationBuilder.RenameColumn(name: "PlayableId", table: "ChannelScheduleItem", newName: "MovieID");
            migrationBuilder.RenameIndex(
                name: "IX_ChannelScheduleItem_PlayableId", table: "ChannelScheduleItem", newName: "IX_ChannelScheduleItem_MovieID");
            migrationBuilder.AddForeignKey(
                name: "FK_ChannelScheduleItem_Movie_MovieID", table: "ChannelScheduleItem", column: "MovieID",
                principalTable: "Movie", principalColumn: "id", onDelete: ReferentialAction.Restrict);

            // Reverse §3: MoviePlaybackProgress PlayableId → MovieID.
            migrationBuilder.DropForeignKey(name: "FK_MoviePlaybackProgress_Playable_PlayableId", table: "MoviePlaybackProgress");
            migrationBuilder.Sql(@"
                UPDATE pp SET pp.PlayableId = m.id
                FROM dbo.MoviePlaybackProgress pp JOIN dbo.Movie m ON m.PlayableId = pp.PlayableId;");
            migrationBuilder.RenameColumn(name: "PlayableId", table: "MoviePlaybackProgress", newName: "MovieID");
            migrationBuilder.RenameIndex(
                name: "IX_MoviePlaybackProgress_UserID_PlayableId", table: "MoviePlaybackProgress", newName: "IX_MoviePlaybackProgress_UserID_MovieID");
            migrationBuilder.RenameIndex(
                name: "IX_MoviePlaybackProgress_PlayableId", table: "MoviePlaybackProgress", newName: "IX_MoviePlaybackProgress_MovieID");
            migrationBuilder.AddForeignKey(
                name: "FK_MoviePlaybackProgress_Movie_MovieID", table: "MoviePlaybackProgress", column: "MovieID",
                principalTable: "Movie", principalColumn: "id", onDelete: ReferentialAction.Cascade);

            // Reverse §2 + §1.
            migrationBuilder.DropForeignKey(name: "FK_Movie_Playable_PlayableId", table: "Movie");
            migrationBuilder.DropIndex(name: "IX_Movie_PlayableId", table: "Movie");
            migrationBuilder.DropColumn(name: "PlayableId", table: "Movie");
            migrationBuilder.DropTable(name: "Episode");
            migrationBuilder.DropTable(name: "Series");
            migrationBuilder.DropTable(name: "Playable");
        }
    }
}
