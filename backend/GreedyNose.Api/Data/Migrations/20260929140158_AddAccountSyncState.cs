using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountSyncStates",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountKey = table.Column<string>(type: "text", nullable: false),
                    FirstSyncCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountSyncStates", x => new { x.UserId, x.AccountKey });
                    table.ForeignKey(
                        name: "FK_AccountSyncStates_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Hand-written backfill: an account that already has debits has finished its first sync
            // by definition, and without a row the next tick would run it as a first sync again and
            // silently swallow the next real charge. MIN(FirstSeenAt) is when we first stored a
            // debit for it. Accounts with no debits get no row and run a (harmless, empty) first sync.
            // Not a guarantee: every account with any Debits row counts as done, including one whose
            // first sync was cut short under the old code — acceptable for the dev database.
            migrationBuilder.Sql("""
                INSERT INTO "AccountSyncStates" ("UserId", "AccountKey", "FirstSyncCompletedAt")
                SELECT "UserId", "AccountKey", MIN("FirstSeenAt")
                FROM "Debits"
                GROUP BY "UserId", "AccountKey";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountSyncStates");
        }
    }
}
