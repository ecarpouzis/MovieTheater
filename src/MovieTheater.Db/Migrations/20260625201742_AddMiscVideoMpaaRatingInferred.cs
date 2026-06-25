using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddMiscVideoMpaaRatingInferred : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MpaaRatingInferred",
                table: "MiscVideo",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpaaRatingInferredSource",
                table: "MiscVideo",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MpaaRatingInferred",
                table: "MiscVideo");

            migrationBuilder.DropColumn(
                name: "MpaaRatingInferredSource",
                table: "MiscVideo");
        }
    }
}
