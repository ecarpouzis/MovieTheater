using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddBrowseOrderingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SimpleTitle",
                table: "Series",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SimpleTitle",
                table: "Movie",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Series_SimpleTitle_Id",
                table: "Series",
                columns: new[] { "SimpleTitle", "Id" },
                filter: "[ReviewBatch] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Movie_SimpleTitle_id",
                table: "Movie",
                columns: new[] { "SimpleTitle", "id" },
                filter: "[ReviewBatch] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Series_SimpleTitle_Id",
                table: "Series");

            migrationBuilder.DropIndex(
                name: "IX_Movie_SimpleTitle_id",
                table: "Movie");

            migrationBuilder.AlterColumn<string>(
                name: "SimpleTitle",
                table: "Series",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SimpleTitle",
                table: "Movie",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);
        }
    }
}
