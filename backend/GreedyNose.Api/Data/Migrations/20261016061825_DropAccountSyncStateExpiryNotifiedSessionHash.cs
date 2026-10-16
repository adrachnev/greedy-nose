using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropAccountSyncStateExpiryNotifiedSessionHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiryNotifiedSessionHash",
                table: "AccountSyncStates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpiryNotifiedSessionHash",
                table: "AccountSyncStates",
                type: "text",
                nullable: true);
        }
    }
}
