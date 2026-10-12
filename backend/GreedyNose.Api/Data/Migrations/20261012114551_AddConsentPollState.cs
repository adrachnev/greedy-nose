using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConsentPollState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAt",
                table: "BankConsents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "BankConsents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSuccessAt",
                table: "BankConsents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PollIntervalMinutes",
                table: "BankConsents",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "BankConsents");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "BankConsents");

            migrationBuilder.DropColumn(
                name: "LastSuccessAt",
                table: "BankConsents");

            migrationBuilder.DropColumn(
                name: "PollIntervalMinutes",
                table: "BankConsents");
        }
    }
}
