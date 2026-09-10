using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Books.Db.Migrations.Hot
{
    /// <inheritdoc />
    public partial class SeriesTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TitleId",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SeriesTitle",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Franchise = table.Column<string>(type: "TEXT", nullable: true),
                    PublisherId = table.Column<int>(type: "INTEGER", nullable: true),
                    YearStart = table.Column<int>(type: "INTEGER", nullable: true),
                    YearEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    RunCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesTitle", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Series_TitleId",
                table: "Series",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesTitle_Franchise",
                table: "SeriesTitle",
                column: "Franchise");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesTitle_Key",
                table: "SeriesTitle",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeriesTitle_Name_Id",
                table: "SeriesTitle",
                columns: new[] { "Name", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_Series_SeriesTitle_TitleId",
                table: "Series",
                column: "TitleId",
                principalTable: "SeriesTitle",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Series_SeriesTitle_TitleId",
                table: "Series");

            migrationBuilder.DropTable(
                name: "SeriesTitle");

            migrationBuilder.DropIndex(
                name: "IX_Series_TitleId",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "TitleId",
                table: "Series");
        }
    }
}
