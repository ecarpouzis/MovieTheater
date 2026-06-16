using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMiscVideo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MiscVideo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlayableId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SimpleTitle = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Year = table.Column<int>(type: "int", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RelatedMovieId = table.Column<int>(type: "int", nullable: true),
                    RelatedSeriesId = table.Column<int>(type: "int", nullable: true),
                    CollectionName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: true),
                    ReviewBatch = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ReviewProvenance = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ReviewSourcePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MiscVideo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MiscVideo_Movie_RelatedMovieId",
                        column: x => x.RelatedMovieId,
                        principalTable: "Movie",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MiscVideo_Playable_PlayableId",
                        column: x => x.PlayableId,
                        principalTable: "Playable",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MiscVideo_Series_RelatedSeriesId",
                        column: x => x.RelatedSeriesId,
                        principalTable: "Series",
                        principalColumn: "MovieId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MiscVideo_CollectionName",
                table: "MiscVideo",
                column: "CollectionName");

            migrationBuilder.CreateIndex(
                name: "IX_MiscVideo_PlayableId",
                table: "MiscVideo",
                column: "PlayableId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MiscVideo_RelatedMovieId",
                table: "MiscVideo",
                column: "RelatedMovieId");

            migrationBuilder.CreateIndex(
                name: "IX_MiscVideo_RelatedSeriesId",
                table: "MiscVideo",
                column: "RelatedSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_MiscVideo_ReviewBatch",
                table: "MiscVideo",
                column: "ReviewBatch");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MiscVideo");
        }
    }
}
