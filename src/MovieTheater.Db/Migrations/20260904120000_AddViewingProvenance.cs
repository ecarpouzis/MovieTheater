using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieTheater.Db.Migrations
{
    /// <summary>
    /// Provenance on the lists (2026-09-04): who placed a mark, and when.
    ///
    /// <para><b>Two columns on Viewing</b> — <c>CreatedUtc</c> and <c>CreatedByUserId</c> — say when a mark
    /// was made and by whom: the owner, or a friend marking on their behalf (a Want placed by a friend
    /// IS a suggestion — there is no separate type). Both nullable: every row older than this migration
    /// reads as "before Sep 2026". <c>ViewingType</c> narrows from nvarchar(max) to nvarchar(32) so
    /// <c>(UserID, ViewingType)</c> can be indexed — the question every list, the browse <c>my=</c> leg
    /// and the <c>my</c> group axis ask.</para>
    ///
    /// <para><b>One table, ViewingEvent</b> — the append-only journal of every add / remove / re-score,
    /// with the actor. Un-marking deletes the Viewing row, so removals have nowhere else to live. No
    /// foreign keys, on purpose (VideoPlaybackIncident's posture): a journal entry must outlive the
    /// title, the account and the row it describes.</para>
    ///
    /// <para><b>The data step is NOT here.</b> Backfilling <c>CreatedByUserId = Eric</c> on other people's
    /// existing WantToWatch rows (every one of them was his suggestion) is a one-off UPDATE of a NULL
    /// column that belongs to the hand-run script, behind a snapshot table and a dry-run count:
    /// sql/AddViewingProvenance.sql. This migration is purely additive apart from the ALTER COLUMN,
    /// which is guarded there by a MAX(LEN()) check.</para>
    /// </summary>
    public partial class AddViewingProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ViewingType", table: "Viewing", type: "nvarchar(32)", maxLength: 32, nullable: true,
                oldClrType: typeof(string), oldType: "nvarchar(max)", oldNullable: true);

            migrationBuilder.AddColumn<System.DateTime>(
                name: "CreatedUtc", table: "Viewing", type: "datetime2", nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId", table: "Viewing", type: "int", nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Viewing_CreatedByUserId", table: "Viewing", column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Viewing_UserID_ViewingType",
                table: "Viewing",
                columns: new[] { "UserID", "ViewingType" })
                .Annotation("SqlServer:Include", new[] { "MovieID", "SeriesId", "MiscVideoId", "CreatedByUserId", "CreatedUtc" });

            // Restrict in the model; the live table's other Viewing FKs are NO_ACTION, and this one is
            // written the same way there. Either way: a suggestion outlives its suggester's account.
            migrationBuilder.AddForeignKey(
                name: "FK_Viewing_Users_CreatedByUserId",
                table: "Viewing",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "UserID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateTable(
                name: "ViewingEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: true),
                    MovieID = table.Column<int>(type: "int", nullable: true),
                    SeriesId = table.Column<int>(type: "int", nullable: true),
                    MiscVideoId = table.Column<int>(type: "int", nullable: true),
                    ViewingType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Data = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AtUtc = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewingEvent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ViewingEvent_UserId_AtUtc",
                table: "ViewingEvent",
                columns: new[] { "UserId", "AtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ViewingEvent_AtUtc", table: "ViewingEvent", column: "AtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ViewingEvent");
            migrationBuilder.DropForeignKey(name: "FK_Viewing_Users_CreatedByUserId", table: "Viewing");
            migrationBuilder.DropIndex(name: "IX_Viewing_UserID_ViewingType", table: "Viewing");
            migrationBuilder.DropIndex(name: "IX_Viewing_CreatedByUserId", table: "Viewing");
            migrationBuilder.DropColumn(name: "CreatedByUserId", table: "Viewing");
            migrationBuilder.DropColumn(name: "CreatedUtc", table: "Viewing");
            migrationBuilder.AlterColumn<string>(
                name: "ViewingType", table: "Viewing", type: "nvarchar(max)", nullable: true,
                oldClrType: typeof(string), oldType: "nvarchar(32)", oldMaxLength: 32, oldNullable: true);
        }
    }
}
