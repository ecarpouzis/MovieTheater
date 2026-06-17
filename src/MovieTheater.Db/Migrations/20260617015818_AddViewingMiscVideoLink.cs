using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddViewingMiscVideoLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MiscVideoId",
                table: "Viewing",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Viewing_MiscVideoId",
                table: "Viewing",
                column: "MiscVideoId");

            migrationBuilder.AddForeignKey(
                name: "FK_Viewing_MiscVideo_MiscVideoId",
                table: "Viewing",
                column: "MiscVideoId",
                principalTable: "MiscVideo",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Viewing_MiscVideo_MiscVideoId",
                table: "Viewing");

            migrationBuilder.DropIndex(
                name: "IX_Viewing_MiscVideoId",
                table: "Viewing");

            migrationBuilder.DropColumn(
                name: "MiscVideoId",
                table: "Viewing");
        }
    }
}
