using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConsentExpiryNotified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpiryNotifiedSessionHash",
                table: "BankConsents",
                type: "text",
                nullable: true);

            // Carry over "already told" (Story 4.1). The value is a hash of one session, so copying it to a consent
            // row of another bank of the same user is harmless: it never equals that bank's session hash. If a user
            // has several non-null values, the one of the newest first sync wins (FirstSyncCompletedAt, then
            // AccountKey as a stable tie-break). The old column is NOT dropped: the still-running old code needs it
            // between this migration and the new deploy.
            migrationBuilder.Sql("""
                UPDATE "BankConsents" AS c
                SET "ExpiryNotifiedSessionHash" = s."ExpiryNotifiedSessionHash"
                FROM (
                    SELECT DISTINCT ON ("UserId") "UserId", "ExpiryNotifiedSessionHash"
                    FROM "AccountSyncStates"
                    WHERE "ExpiryNotifiedSessionHash" IS NOT NULL
                    ORDER BY "UserId", "FirstSyncCompletedAt" DESC, "AccountKey"
                ) AS s
                WHERE s."UserId" = c."UserId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiryNotifiedSessionHash",
                table: "BankConsents");
        }
    }
}
