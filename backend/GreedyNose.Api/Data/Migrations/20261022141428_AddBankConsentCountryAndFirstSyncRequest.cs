using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBankConsentCountryAndFirstSyncRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "BankConsents",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstSyncRequestedAt",
                table: "BankConsents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Country",
                table: "BankConsents");

            migrationBuilder.DropColumn(
                name: "FirstSyncRequestedAt",
                table: "BankConsents");
        }
    }
}
