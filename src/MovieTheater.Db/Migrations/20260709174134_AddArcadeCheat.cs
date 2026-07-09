using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddArcadeCheat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArcadeCheat",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ArcadeGameId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OptionKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    OptionValue = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DefaultOn = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcadeCheat", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArcadeCheat_ArcadeGame_ArcadeGameId",
                        column: x => x.ArcadeGameId,
                        principalTable: "ArcadeGame",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArcadeCheat_ArcadeGameId_Ordinal",
                table: "ArcadeCheat",
                columns: new[] { "ArcadeGameId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArcadeCheat");
        }
    }
}
