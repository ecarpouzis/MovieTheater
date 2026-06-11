using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <inheritdoc />
    public partial class RedesignPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieCredit_Person_PersonImdbNameId",
                table: "MovieCredit");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Person",
                table: "Person");

            migrationBuilder.DropIndex(
                name: "IX_MovieCredit_MovieID_PersonImdbNameId_Role",
                table: "MovieCredit");

            migrationBuilder.DropIndex(
                name: "IX_MovieCredit_PersonImdbNameId",
                table: "MovieCredit");

            migrationBuilder.DropColumn(
                name: "PersonImdbNameId",
                table: "MovieCredit");

            migrationBuilder.AlterColumn<string>(
                name: "ImdbNameId",
                table: "Person",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "Person",
                type: "int",
                nullable: false,
                defaultValue: 0)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.AddColumn<string>(
                name: "NameKey",
                table: "Person",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PersonId",
                table: "MovieCredit",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Person",
                table: "Person",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Person_ImdbNameId",
                table: "Person",
                column: "ImdbNameId",
                unique: true,
                filter: "[ImdbNameId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Person_NameKey",
                table: "Person",
                column: "NameKey");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCredit_MovieID_PersonId_Role",
                table: "MovieCredit",
                columns: new[] { "MovieID", "PersonId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCredit_PersonId",
                table: "MovieCredit",
                column: "PersonId");

            migrationBuilder.AddForeignKey(
                name: "FK_MovieCredit_Person_PersonId",
                table: "MovieCredit",
                column: "PersonId",
                principalTable: "Person",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MovieCredit_Person_PersonId",
                table: "MovieCredit");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Person",
                table: "Person");

            migrationBuilder.DropIndex(
                name: "IX_Person_ImdbNameId",
                table: "Person");

            migrationBuilder.DropIndex(
                name: "IX_Person_NameKey",
                table: "Person");

            migrationBuilder.DropIndex(
                name: "IX_MovieCredit_MovieID_PersonId_Role",
                table: "MovieCredit");

            migrationBuilder.DropIndex(
                name: "IX_MovieCredit_PersonId",
                table: "MovieCredit");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "Person");

            migrationBuilder.DropColumn(
                name: "NameKey",
                table: "Person");

            migrationBuilder.DropColumn(
                name: "PersonId",
                table: "MovieCredit");

            migrationBuilder.AlterColumn<string>(
                name: "ImdbNameId",
                table: "Person",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonImdbNameId",
                table: "MovieCredit",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Person",
                table: "Person",
                column: "ImdbNameId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCredit_MovieID_PersonImdbNameId_Role",
                table: "MovieCredit",
                columns: new[] { "MovieID", "PersonImdbNameId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieCredit_PersonImdbNameId",
                table: "MovieCredit",
                column: "PersonImdbNameId");

            migrationBuilder.AddForeignKey(
                name: "FK_MovieCredit_Person_PersonImdbNameId",
                table: "MovieCredit",
                column: "PersonImdbNameId",
                principalTable: "Person",
                principalColumn: "ImdbNameId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
