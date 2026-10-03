using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Books.Db.Migrations.Hot
{
    /// <inheritdoc />
    public partial class SeriesRunStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RunHeld",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunPlanned",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunPublished",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunStatus",
                table: "Series",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RunStatusBasis",
                table: "Series",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunHeld",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "RunPlanned",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "RunPublished",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "RunStatus",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "RunStatusBasis",
                table: "Series");
        }
    }
}
