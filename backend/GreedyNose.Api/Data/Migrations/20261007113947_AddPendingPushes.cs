using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingPushes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingPushes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DebitId = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    SummaryNewCount = table.Column<int>(type: "integer", nullable: true),
                    SummaryBadCount = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingPushes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingPushes_Debits_UserId_DebitId",
                        columns: x => new { x.UserId, x.DebitId },
                        principalTable: "Debits",
                        principalColumns: new[] { "UserId", "Id" });
                    table.ForeignKey(
                        name: "FK_PendingPushes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingPushes_UserId_DebitId",
                table: "PendingPushes",
                columns: new[] { "UserId", "DebitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingPushes_UserId_EndedAt_CreatedAt",
                table: "PendingPushes",
                columns: new[] { "UserId", "EndedAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingPushes");
        }
    }
}
