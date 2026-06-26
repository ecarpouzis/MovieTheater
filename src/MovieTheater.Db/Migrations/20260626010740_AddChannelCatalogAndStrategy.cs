using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelCatalogAndStrategy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CatalogKey",
                table: "Channel",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Channel",
                type: "nvarchar(48)",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoPath",
                table: "Channel",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RotationJson",
                table: "Channel",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleStrategy",
                table: "Channel",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonEndDay",
                table: "Channel",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonEndMonth",
                table: "Channel",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonStartDay",
                table: "Channel",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonStartMonth",
                table: "Channel",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TitleInsight_SubjectKind_SubjectId_SpecVersion_GeneratedUtc_Id",
                table: "TitleInsight",
                columns: new[] { "SubjectKind", "SubjectId", "SpecVersion", "GeneratedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Channel_CatalogKey",
                table: "Channel",
                column: "CatalogKey",
                unique: true,
                filter: "[CatalogKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TitleInsight_SubjectKind_SubjectId_SpecVersion_GeneratedUtc_Id",
                table: "TitleInsight");

            migrationBuilder.DropIndex(
                name: "IX_Channel_CatalogKey",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "CatalogKey",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "LogoPath",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "RotationJson",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "ScheduleStrategy",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "SeasonEndDay",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "SeasonEndMonth",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "SeasonStartDay",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "SeasonStartMonth",
                table: "Channel");
        }
    }
}
