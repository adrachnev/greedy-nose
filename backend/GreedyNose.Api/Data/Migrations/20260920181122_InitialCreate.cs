using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreedyNose.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payees",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Initials = table.Column<string>(type: "text", nullable: false),
                    Iban = table.Column<string>(type: "text", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payees", x => new { x.UserId, x.Id });
                    table.ForeignKey(
                        name: "FK_Payees_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Debits",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<string>(type: "text", nullable: false),
                    PayeeId = table.Column<string>(type: "text", nullable: false),
                    AmountEUR = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HasTime = table.Column<bool>(type: "boolean", nullable: false),
                    PaymentType = table.Column<string>(type: "text", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: false),
                    AccountKey = table.Column<string>(type: "text", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Debits", x => new { x.UserId, x.Id });
                    table.ForeignKey(
                        name: "FK_Debits_Payees_UserId_PayeeId",
                        columns: x => new { x.UserId, x.PayeeId },
                        principalTable: "Payees",
                        principalColumns: new[] { "UserId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Debits_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Rules",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayeeId = table.Column<string>(type: "text", nullable: false),
                    Classification = table.Column<string>(type: "text", nullable: false),
                    AmountEUR = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => new { x.UserId, x.PayeeId });
                    table.CheckConstraint("CK_Rules_Classification", "\"Classification\" IN ('good', 'bad')");
                    table.ForeignKey(
                        name: "FK_Rules_Payees_UserId_PayeeId",
                        columns: x => new { x.UserId, x.PayeeId },
                        principalTable: "Payees",
                        principalColumns: new[] { "UserId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Rules_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DebitId = table.Column<string>(type: "text", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationLog_Debits_UserId_DebitId",
                        columns: x => new { x.UserId, x.DebitId },
                        principalTable: "Debits",
                        principalColumns: new[] { "UserId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationLog_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "CreatedAt" },
                values: new object[] { new Guid("5f0d7c3a-8b1e-4d6a-9a52-3c7e1b2f4a90"), new DateTimeOffset(new DateTime(2026, 9, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.CreateIndex(
                name: "IX_Debits_UserId_PayeeId",
                table: "Debits",
                columns: new[] { "UserId", "PayeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_Token",
                table: "DeviceTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTokens_UserId",
                table: "DeviceTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLog_UserId_DebitId",
                table: "NotificationLog",
                columns: new[] { "UserId", "DebitId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceTokens");

            migrationBuilder.DropTable(
                name: "NotificationLog");

            migrationBuilder.DropTable(
                name: "Rules");

            migrationBuilder.DropTable(
                name: "Debits");

            migrationBuilder.DropTable(
                name: "Payees");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
