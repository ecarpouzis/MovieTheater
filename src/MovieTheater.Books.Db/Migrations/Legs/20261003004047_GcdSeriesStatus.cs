using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Books.Db.Migrations.Legs
{
    /// <inheritdoc />
    public partial class GcdSeriesStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                table: "GcdSeries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicationDates",
                table: "GcdSeries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrackingNotes",
                table: "GcdSeries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsCurrent",
                table: "GcdSeries");

            migrationBuilder.DropColumn(
                name: "PublicationDates",
                table: "GcdSeries");

            migrationBuilder.DropColumn(
                name: "TrackingNotes",
                table: "GcdSeries");
        }
    }
}
