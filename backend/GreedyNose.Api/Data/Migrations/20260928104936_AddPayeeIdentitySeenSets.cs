using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPayeeIdentitySeenSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "CreditorAgentsSeen",
                table: "Payees",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<string>>(
                name: "IbansSeen",
                table: "Payees",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<string>>(
                name: "NormalizedNamesSeen",
                table: "Payees",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreditorAgentsSeen",
                table: "Payees");

            migrationBuilder.DropColumn(
                name: "IbansSeen",
                table: "Payees");

            migrationBuilder.DropColumn(
                name: "NormalizedNamesSeen",
                table: "Payees");
        }
    }
}
