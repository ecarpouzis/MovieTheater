using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class SplitSeriesFromMovie : Migration
    {
        // Hand-tuned: the scaffolder tried to mutate the (empty) aggregate-only Series table in place
        // (renaming MovieId->TitleType, adding an IDENTITY column) and to drop the Viewing->Movie FK by
        // EF's convention name. The live DB's Viewing->Movie FK is actually named "FK_MovieID_Movie"
        // (baseline), so we drop it by that name; and because Series holds 0 rows we cleanly DROP + CREATE
        // it as a full Movie peer. Additive (dual-existence) — Episode/Viewing keep their old columns.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the FKs that reference the soon-to-be-recreated Series + the baseline Viewing->Movie FK.
            migrationBuilder.DropForeignKey(name: "FK_MiscVideo_Series_RelatedSeriesId", table: "MiscVideo");
            migrationBuilder.DropForeignKey(name: "FK_MovieID_Movie", table: "Viewing");

            // The old aggregate-only Series table is empty — replace it wholesale.
            migrationBuilder.DropTable(name: "Series");

            // Viewing can now target a Movie OR a Series.
            migrationBuilder.AlterColumn<int>(name: "MovieID", table: "Viewing", type: "int", nullable: true,
                oldClrType: typeof(int), oldType: "int");
            migrationBuilder.AddColumn<int>(name: "SeriesId", table: "Viewing", type: "int", nullable: true);

            // Episodes gain the canonical Series link (backfilled in Stage B; SeriesMovieId kept until the flip).
            migrationBuilder.AddColumn<int>(name: "SeriesId", table: "Episode", type: "int", nullable: true);

            // Series: a first-class title, full peer of Movie. Id IDENTITY (seeded to the old Movie id in Stage B).
            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SimpleTitle = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Rating = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Runtime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Genre = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Director = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Writer = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Actors = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Plot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    imdbRating = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    imdbID = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    tomatoRating = table.Column<int>(type: "int", nullable: true),
                    UploadedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RemoveFromRandom = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RuntimeMinutes = table.Column<int>(type: "int", nullable: true),
                    PlotFull = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PlotSynopsis = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MpaaRating = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TopCast = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImdbReleaseDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImdbRatingScraped = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ImdbVerifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImdbScrapedTitle = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImdbNeedsReview = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ImdbReviewReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RtTomatometer = table.Column<int>(type: "int", nullable: true),
                    RtPopcornmeter = table.Column<int>(type: "int", nullable: true),
                    RtUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RtScoresUpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RtNeedsReview = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RtReviewReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TmdbId = table.Column<int>(type: "int", nullable: true),
                    Tagline = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginalLanguage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Country = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BudgetUsd = table.Column<long>(type: "bigint", nullable: true),
                    RevenueUsd = table.Column<long>(type: "bigint", nullable: true),
                    TmdbPopularity = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    TmdbVoteCount = table.Column<int>(type: "int", nullable: true),
                    BackdropPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TrailerKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TitleType = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    SeasonCount = table.Column<int>(type: "int", nullable: true),
                    EpisodeCount = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    StartYear = table.Column<int>(type: "int", nullable: true),
                    EndYear = table.Column<int>(type: "int", nullable: true),
                    Network = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ReviewBatch = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReviewProvenance = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ReviewConfidence = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ReviewSourcePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                },
                constraints: table => { table.PrimaryKey("PK_Series", x => x.Id); });

            migrationBuilder.CreateTable(
                name: "SeriesGenre",
                columns: table => new
                {
                    SeriesId = table.Column<int>(type: "int", nullable: false),
                    GenreId = table.Column<int>(type: "int", nullable: false),
                    Ordering = table.Column<int>(type: "int", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesGenre", x => new { x.SeriesId, x.GenreId });
                    table.ForeignKey("FK_SeriesGenre_Genre_GenreId", x => x.GenreId, "Genre", "Id", onDelete: ReferentialAction.Cascade);
                    table.ForeignKey("FK_SeriesGenre_Series_SeriesId", x => x.SeriesId, "Series", "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeriesCredit",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                    SeriesId = table.Column<int>(type: "int", nullable: false),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Ordering = table.Column<int>(type: "int", nullable: false),
                    Character = table.Column<string>(type: "nvarchar(max)", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesCredit", x => x.Id);
                    table.ForeignKey("FK_SeriesCredit_Person_PersonId", x => x.PersonId, "Person", "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey("FK_SeriesCredit_Series_SeriesId", x => x.SeriesId, "Series", "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeriesPlotSummary",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                    SeriesId = table.Column<int>(type: "int", nullable: false),
                    Ordering = table.Column<int>(type: "int", nullable: false),
                    Author = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesPlotSummary", x => x.Id);
                    table.ForeignKey("FK_SeriesPlotSummary_Series_SeriesId", x => x.SeriesId, "Series", "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeriesPosterDetails",
                columns: table => new
                {
                    SeriesId = table.Column<int>(type: "int", nullable: false),
                    PosterLink = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PosterVersion = table.Column<int>(type: "int", nullable: false),
                    DominantColor = table.Column<string>(type: "nvarchar(max)", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesPosterDetails", x => x.SeriesId);
                    table.ForeignKey("FK_SeriesPosterDetails_Series_SeriesId", x => x.SeriesId, "Series", "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(name: "IX_Series_ReviewBatch", table: "Series", column: "ReviewBatch");
            migrationBuilder.CreateIndex(name: "IX_Episode_SeriesId", table: "Episode", column: "SeriesId");
            migrationBuilder.CreateIndex(name: "IX_Viewing_SeriesId", table: "Viewing", column: "SeriesId");
            migrationBuilder.CreateIndex(name: "IX_SeriesGenre_GenreId", table: "SeriesGenre", column: "GenreId");
            migrationBuilder.CreateIndex(name: "IX_SeriesCredit_PersonId", table: "SeriesCredit", column: "PersonId");
            migrationBuilder.CreateIndex(name: "IX_SeriesCredit_SeriesId_PersonId_Role", table: "SeriesCredit",
                columns: new[] { "SeriesId", "PersonId", "Role" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_SeriesPlotSummary_SeriesId", table: "SeriesPlotSummary", column: "SeriesId");

            migrationBuilder.AddForeignKey(name: "FK_Episode_Series_SeriesId", table: "Episode", column: "SeriesId",
                principalTable: "Series", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_MiscVideo_Series_RelatedSeriesId", table: "MiscVideo", column: "RelatedSeriesId",
                principalTable: "Series", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Viewing_Movie_MovieID", table: "Viewing", column: "MovieID",
                principalTable: "Movie", principalColumn: "id", onDelete: ReferentialAction.NoAction);
            migrationBuilder.AddForeignKey(name: "FK_Viewing_Series_SeriesId", table: "Viewing", column: "SeriesId",
                principalTable: "Series", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_Episode_Series_SeriesId", table: "Episode");
            migrationBuilder.DropForeignKey(name: "FK_MiscVideo_Series_RelatedSeriesId", table: "MiscVideo");
            migrationBuilder.DropForeignKey(name: "FK_Viewing_Movie_MovieID", table: "Viewing");
            migrationBuilder.DropForeignKey(name: "FK_Viewing_Series_SeriesId", table: "Viewing");

            migrationBuilder.DropTable(name: "SeriesGenre");
            migrationBuilder.DropTable(name: "SeriesCredit");
            migrationBuilder.DropTable(name: "SeriesPlotSummary");
            migrationBuilder.DropTable(name: "SeriesPosterDetails");
            migrationBuilder.DropTable(name: "Series");

            migrationBuilder.DropIndex(name: "IX_Episode_SeriesId", table: "Episode");
            migrationBuilder.DropIndex(name: "IX_Viewing_SeriesId", table: "Viewing");
            migrationBuilder.DropColumn(name: "SeriesId", table: "Episode");
            migrationBuilder.DropColumn(name: "SeriesId", table: "Viewing");

            // Recreate the original aggregate-only Series table (1:1 with the series Movie row).
            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    MovieId = table.Column<int>(type: "int", nullable: false),
                    SeasonCount = table.Column<int>(type: "int", nullable: true),
                    EpisodeCount = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    StartYear = table.Column<int>(type: "int", nullable: true),
                    EndYear = table.Column<int>(type: "int", nullable: true),
                    Network = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.MovieId);
                    table.ForeignKey("FK_Series_Movie_MovieId", x => x.MovieId, "Movie", "id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AlterColumn<int>(name: "MovieID", table: "Viewing", type: "int", nullable: false,
                defaultValue: 0, oldClrType: typeof(int), oldType: "int", oldNullable: true);
            migrationBuilder.AddForeignKey(name: "FK_MovieID_Movie", table: "Viewing", column: "MovieID",
                principalTable: "Movie", principalColumn: "id", onDelete: ReferentialAction.NoAction);
            migrationBuilder.AddForeignKey(name: "FK_MiscVideo_Series_RelatedSeriesId", table: "MiscVideo", column: "RelatedSeriesId",
                principalTable: "Series", principalColumn: "MovieId", onDelete: ReferentialAction.Restrict);
        }
    }
}
