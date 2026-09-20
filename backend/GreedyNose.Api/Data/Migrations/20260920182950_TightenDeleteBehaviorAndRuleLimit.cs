using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TightenDeleteBehaviorAndRuleLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Debits_Payees_UserId_PayeeId",
                table: "Debits");

            migrationBuilder.DropForeignKey(
                name: "FK_NotificationLog_Debits_UserId_DebitId",
                table: "NotificationLog");

            migrationBuilder.DropForeignKey(
                name: "FK_Rules_Payees_UserId_PayeeId",
                table: "Rules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Rules_AmountEUR_Positive",
                table: "Rules",
                sql: "\"AmountEUR\" IS NULL OR \"AmountEUR\" > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_Debits_Payees_UserId_PayeeId",
                table: "Debits",
                columns: new[] { "UserId", "PayeeId" },
                principalTable: "Payees",
                principalColumns: new[] { "UserId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationLog_Debits_UserId_DebitId",
                table: "NotificationLog",
                columns: new[] { "UserId", "DebitId" },
                principalTable: "Debits",
                principalColumns: new[] { "UserId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_Rules_Payees_UserId_PayeeId",
                table: "Rules",
                columns: new[] { "UserId", "PayeeId" },
                principalTable: "Payees",
                principalColumns: new[] { "UserId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Debits_Payees_UserId_PayeeId",
                table: "Debits");

            migrationBuilder.DropForeignKey(
                name: "FK_NotificationLog_Debits_UserId_DebitId",
                table: "NotificationLog");

            migrationBuilder.DropForeignKey(
                name: "FK_Rules_Payees_UserId_PayeeId",
                table: "Rules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Rules_AmountEUR_Positive",
                table: "Rules");

            migrationBuilder.AddForeignKey(
                name: "FK_Debits_Payees_UserId_PayeeId",
                table: "Debits",
                columns: new[] { "UserId", "PayeeId" },
                principalTable: "Payees",
                principalColumns: new[] { "UserId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationLog_Debits_UserId_DebitId",
                table: "NotificationLog",
                columns: new[] { "UserId", "DebitId" },
                principalTable: "Debits",
                principalColumns: new[] { "UserId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Rules_Payees_UserId_PayeeId",
                table: "Rules",
                columns: new[] { "UserId", "PayeeId" },
                principalTable: "Payees",
                principalColumns: new[] { "UserId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
