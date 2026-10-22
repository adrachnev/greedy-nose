using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectAttemptState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ChoiceDeadline",
                table: "PendingConsents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "PendingConsents",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "PendingConsents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "WaitingPayload",
                table: "PendingConsents",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChoiceDeadline",
                table: "PendingConsents");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "PendingConsents");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "PendingConsents");

            migrationBuilder.DropColumn(
                name: "WaitingPayload",
                table: "PendingConsents");
        }
    }
}
