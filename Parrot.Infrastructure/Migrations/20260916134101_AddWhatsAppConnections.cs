using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WhatsAppConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WebhookIdentifier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VerifyTokenEncrypted = table.Column<string>(type: "text", nullable: false),
                    WhatsAppBusinessAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PhoneNumberId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayPhoneNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WebhookVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstMessageReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisabledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WhatsAppConnections_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConnections_OwnerUserId",
                table: "WhatsAppConnections",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConnections_PhoneNumberId",
                table: "WhatsAppConnections",
                column: "PhoneNumberId",
                unique: true,
                filter: "\"PhoneNumberId\" IS NOT NULL AND \"DisabledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConnections_WebhookIdentifier",
                table: "WhatsAppConnections",
                column: "WebhookIdentifier",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WhatsAppConnections");
        }
    }
}
