using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FacebookConnections_AspNetUsers_OwnerUserId",
                table: "FacebookConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_InstagramConnections_AspNetUsers_OwnerUserId",
                table: "InstagramConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_TikTokConnections_AspNetUsers_OwnerUserId",
                table: "TikTokConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_WhatsAppConnections_AspNetUsers_OwnerUserId",
                table: "WhatsAppConnections");

            migrationBuilder.AddColumn<string>(
                name: "LastWorkspaceId",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LogoUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workspaces_AspNetUsers_TenantId",
                        column: x => x.TenantId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_TenantId",
                table: "Workspaces",
                column: "TenantId");

            // Every account gets its first workspace now, named from the business
            // fields it already has. It takes the account's own id, which is what
            // every owner column and stored document is keyed by, so no other row
            // has to change — and the foreign keys below find their workspace.
            migrationBuilder.Sql(
                """
                INSERT INTO "Workspaces" ("Id", "TenantId", "Name", "LogoUrl", "CreatedAt")
                SELECT "Id", "Id", "BusinessName", "LogoUrl", "CreatedAt"
                FROM "AspNetUsers"
                ON CONFLICT ("Id") DO NOTHING;
                """);

            migrationBuilder.Sql(
                """
                UPDATE "AspNetUsers" SET "LastWorkspaceId" = "Id" WHERE "LastWorkspaceId" IS NULL;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_FacebookConnections_Workspaces_OwnerUserId",
                table: "FacebookConnections",
                column: "OwnerUserId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InstagramConnections_Workspaces_OwnerUserId",
                table: "InstagramConnections",
                column: "OwnerUserId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TikTokConnections_Workspaces_OwnerUserId",
                table: "TikTokConnections",
                column: "OwnerUserId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WhatsAppConnections_Workspaces_OwnerUserId",
                table: "WhatsAppConnections",
                column: "OwnerUserId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Only clean while every connection is in a tenant's first workspace:
        /// one in a later workspace has no user with its id, and the restored
        /// foreign keys to AspNetUsers refuse it.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FacebookConnections_Workspaces_OwnerUserId",
                table: "FacebookConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_InstagramConnections_Workspaces_OwnerUserId",
                table: "InstagramConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_TikTokConnections_Workspaces_OwnerUserId",
                table: "TikTokConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_WhatsAppConnections_Workspaces_OwnerUserId",
                table: "WhatsAppConnections");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropColumn(
                name: "LastWorkspaceId",
                table: "AspNetUsers");

            migrationBuilder.AddForeignKey(
                name: "FK_FacebookConnections_AspNetUsers_OwnerUserId",
                table: "FacebookConnections",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InstagramConnections_AspNetUsers_OwnerUserId",
                table: "InstagramConnections",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TikTokConnections_AspNetUsers_OwnerUserId",
                table: "TikTokConnections",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WhatsAppConnections_AspNetUsers_OwnerUserId",
                table: "WhatsAppConnections",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
