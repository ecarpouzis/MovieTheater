using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddNormalizedTitleType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NormalizedTitleType",
                table: "Movie",
                type: "int",
                nullable: false,
                computedColumnSql: "CASE WHEN [TitleType] IN (2, 3) THEN 2 ELSE 0 END",
                stored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NormalizedTitleType",
                table: "Movie");
        }
    }
}
