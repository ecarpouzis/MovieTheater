using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Books.Db.Migrations.Hot
{
    /// <summary>
    /// Per-run ranges on <c>CollectedEditionSpanRun</c> (TOOLS_TODO 17): the PK widens to include
    /// <c>ProviderKey</c> so one book may name TWO runs on one leg (a trade collecting two minis, every
    /// omnibus and Library Edition), and each row carries the range IN THAT RUN'S numbering
    /// (Return of the Master = CV 51622 #1-5 = GCD 71228 #103-107). NULL start/end = the span's own range.
    ///
    /// <para>SQLite cannot alter a primary key, so the provider rebuilds the table around these operations —
    /// create a temp table, copy every row, drop, rename, recreate the index. Existing rows keep their keys
    /// and arrive with NULL ranges, which is exactly "the same range as the span", i.e. today's meaning.</para>
    /// </summary>
    public partial class SpanRunRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_CollectedEditionSpanRun",
                table: "CollectedEditionSpanRun");

            migrationBuilder.AddColumn<double>(
                name: "IssueEnd",
                table: "CollectedEditionSpanRun",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "IssueStart",
                table: "CollectedEditionSpanRun",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_CollectedEditionSpanRun",
                table: "CollectedEditionSpanRun",
                columns: new[] { "ItemId", "Source", "Provider", "ProviderKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_CollectedEditionSpanRun",
                table: "CollectedEditionSpanRun");

            migrationBuilder.DropColumn(
                name: "IssueEnd",
                table: "CollectedEditionSpanRun");

            migrationBuilder.DropColumn(
                name: "IssueStart",
                table: "CollectedEditionSpanRun");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CollectedEditionSpanRun",
                table: "CollectedEditionSpanRun",
                columns: new[] { "ItemId", "Source", "Provider" });
        }
    }
}
