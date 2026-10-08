using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTikTokConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TikTokConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "text", nullable: false),
                    WebhookIdentifier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AppId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AppSecretEncrypted = table.Column<string>(type: "text", nullable: false),
                    TikTokOpenId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AccessTokenEncrypted = table.Column<string>(type: "text", nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefreshTokenEncrypted = table.Column<string>(type: "text", nullable: true),
                    RefreshTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisabledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TikTokConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TikTokConnections_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TikTokConnections_OwnerUserId",
                table: "TikTokConnections",
                column: "OwnerUserId",
                unique: true,
                filter: "\"DisabledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TikTokConnections_TikTokOpenId",
                table: "TikTokConnections",
                column: "TikTokOpenId",
                unique: true,
                filter: "\"TikTokOpenId\" IS NOT NULL AND \"DisabledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TikTokConnections_WebhookIdentifier",
                table: "TikTokConnections",
                column: "WebhookIdentifier",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TikTokConnections");
        }
    }
}
