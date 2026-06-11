using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Boardgame",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BggThingId = table.Column<int>(type: "int", nullable: false),
                    ThingType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BaseGameId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    YearPublished = table.Column<int>(type: "int", nullable: true),
                    MinPlayers = table.Column<int>(type: "int", nullable: true),
                    MaxPlayers = table.Column<int>(type: "int", nullable: true),
                    PlayingTime = table.Column<int>(type: "int", nullable: true),
                    MinPlayTime = table.Column<int>(type: "int", nullable: true),
                    MaxPlayTime = table.Column<int>(type: "int", nullable: true),
                    MinAge = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UsersRated = table.Column<int>(type: "int", nullable: true),
                    AverageRating = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    BayesAverageRating = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    StdDev = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Median = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Owned = table.Column<int>(type: "int", nullable: true),
                    Trading = table.Column<int>(type: "int", nullable: true),
                    Wanting = table.Column<int>(type: "int", nullable: true),
                    Wishing = table.Column<int>(type: "int", nullable: true),
                    NumComments = table.Column<int>(type: "int", nullable: true),
                    NumWeights = table.Column<int>(type: "int", nullable: true),
                    AverageWeight = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    LastSyncedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RulesPdfCandidateUrlsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RulesPdfUrlsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HowToPlayVideoUrlsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RulesSyncedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Boardgame", x => x.id);
                    table.ForeignKey(
                        name: "FK_Boardgame_Boardgame_BaseGameId",
                        column: x => x.BaseGameId,
                        principalTable: "Boardgame",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Movie",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
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
                    RemoveFromRandom = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movie", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "RatingMap",
                columns: table => new
                {
                    RatingMapID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MovieRating = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MPARatingID = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatingMap", x => x.RatingMapID);
                });

            migrationBuilder.CreateTable(
                name: "RatingMPA",
                columns: table => new
                {
                    RatingID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MinAge = table.Column<int>(type: "int", nullable: false),
                    MPAName = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatingMPA", x => x.RatingID);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastLogin = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserID);
                });

            migrationBuilder.CreateTable(
                name: "BoardgameExtraDetails",
                columns: table => new
                {
                    BoardgameId = table.Column<int>(type: "int", nullable: false),
                    AlternateNamesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RanksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LinksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PollsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    VersionsXml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    VideosJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MarketplaceXml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawXml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardgameExtraDetails", x => x.BoardgameId);
                    table.ForeignKey(
                        name: "FK_BoardgameExtraDetails_Boardgame_BoardgameId",
                        column: x => x.BoardgameId,
                        principalTable: "Boardgame",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BoardgameImageDetails",
                columns: table => new
                {
                    BoardgameId = table.Column<int>(type: "int", nullable: false),
                    ImageVersion = table.Column<int>(type: "int", nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ThumbnailUrl = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardgameImageDetails", x => x.BoardgameId);
                    table.ForeignKey(
                        name: "FK_BoardgameImageDetails_Boardgame_BoardgameId",
                        column: x => x.BoardgameId,
                        principalTable: "Boardgame",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MoviePosterDetails",
                columns: table => new
                {
                    MovieId = table.Column<int>(type: "int", nullable: false),
                    PosterLink = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PosterVersion = table.Column<int>(type: "int", nullable: false),
                    DominantColor = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoviePosterDetails", x => x.MovieId);
                    table.ForeignKey(
                        name: "FK_MoviePosterDetails_Movie_MovieId",
                        column: x => x.MovieId,
                        principalTable: "Movie",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSettings",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserID = table.Column<int>(type: "int", nullable: false),
                    SettingKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SettingValue = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => x.ID);
                    table.ForeignKey(
                        name: "FK_UserSettings_Users_UserID",
                        column: x => x.UserID,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Viewing",
                columns: table => new
                {
                    ViewingID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MovieID = table.Column<int>(type: "int", nullable: false),
                    UserID = table.Column<int>(type: "int", nullable: false),
                    ViewingType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ViewingData = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Viewing", x => x.ViewingID);
                    table.ForeignKey(
                        name: "FK_Viewing_Movie_MovieID",
                        column: x => x.MovieID,
                        principalTable: "Movie",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Viewing_Users_UserID",
                        column: x => x.UserID,
                        principalTable: "Users",
                        principalColumn: "UserID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Boardgame_BaseGameId",
                table: "Boardgame",
                column: "BaseGameId");

            migrationBuilder.CreateIndex(
                name: "IX_Boardgame_BggThingId",
                table: "Boardgame",
                column: "BggThingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSettings_UserID",
                table: "UserSettings",
                column: "UserID");

            migrationBuilder.CreateIndex(
                name: "IX_Viewing_MovieID",
                table: "Viewing",
                column: "MovieID");

            migrationBuilder.CreateIndex(
                name: "IX_Viewing_UserID",
                table: "Viewing",
                column: "UserID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoardgameExtraDetails");

            migrationBuilder.DropTable(
                name: "BoardgameImageDetails");

            migrationBuilder.DropTable(
                name: "MoviePosterDetails");

            migrationBuilder.DropTable(
                name: "RatingMap");

            migrationBuilder.DropTable(
                name: "RatingMPA");

            migrationBuilder.DropTable(
                name: "UserSettings");

            migrationBuilder.DropTable(
                name: "Viewing");

            migrationBuilder.DropTable(
                name: "Boardgame");

            migrationBuilder.DropTable(
                name: "Movie");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
