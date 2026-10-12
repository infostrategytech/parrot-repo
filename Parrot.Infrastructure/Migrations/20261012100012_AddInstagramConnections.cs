using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstagramConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "text", nullable: false),
                    WebhookIdentifier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VerifyTokenEncrypted = table.Column<string>(type: "text", nullable: false),
                    AccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AccessTokenEncrypted = table.Column<string>(type: "text", nullable: true),
                    AppSecretEncrypted = table.Column<string>(type: "text", nullable: true),
                    WebhookVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisabledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstagramConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstagramConnections_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstagramConnections_AccountId",
                table: "InstagramConnections",
                column: "AccountId",
                unique: true,
                filter: "\"AccountId\" IS NOT NULL AND \"DisabledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InstagramConnections_OwnerUserId",
                table: "InstagramConnections",
                column: "OwnerUserId",
                unique: true,
                filter: "\"DisabledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InstagramConnections_WebhookIdentifier",
                table: "InstagramConnections",
                column: "WebhookIdentifier",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstagramConnections");
        }
    }
}
