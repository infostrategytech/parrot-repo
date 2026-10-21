using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameOwnerColumnsToWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.DropForeignKey(
                name: "FK_Workspaces_AspNetUsers_TenantId",
                table: "Workspaces");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                table: "Workspaces",
                newName: "OwnerId");

            migrationBuilder.RenameIndex(
                name: "IX_Workspaces_TenantId",
                table: "Workspaces",
                newName: "IX_Workspaces_OwnerId");

            migrationBuilder.RenameColumn(
                name: "OwnerUserId",
                table: "WhatsAppConnections",
                newName: "WorkspaceId");

            migrationBuilder.RenameIndex(
                name: "IX_WhatsAppConnections_OwnerUserId",
                table: "WhatsAppConnections",
                newName: "IX_WhatsAppConnections_WorkspaceId");

            migrationBuilder.RenameColumn(
                name: "OwnerUserId",
                table: "TikTokConnections",
                newName: "WorkspaceId");

            migrationBuilder.RenameIndex(
                name: "IX_TikTokConnections_OwnerUserId",
                table: "TikTokConnections",
                newName: "IX_TikTokConnections_WorkspaceId");

            migrationBuilder.RenameColumn(
                name: "OwnerUserId",
                table: "InstagramConnections",
                newName: "WorkspaceId");

            migrationBuilder.RenameIndex(
                name: "IX_InstagramConnections_OwnerUserId",
                table: "InstagramConnections",
                newName: "IX_InstagramConnections_WorkspaceId");

            migrationBuilder.RenameColumn(
                name: "OwnerUserId",
                table: "FacebookConnections",
                newName: "WorkspaceId");

            migrationBuilder.RenameIndex(
                name: "IX_FacebookConnections_OwnerUserId",
                table: "FacebookConnections",
                newName: "IX_FacebookConnections_WorkspaceId");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "Agents",
                newName: "WorkspaceId");

            migrationBuilder.RenameIndex(
                name: "IX_Agents_UserId",
                table: "Agents",
                newName: "IX_Agents_WorkspaceId");

            migrationBuilder.AddForeignKey(
                name: "FK_FacebookConnections_Workspaces_WorkspaceId",
                table: "FacebookConnections",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InstagramConnections_Workspaces_WorkspaceId",
                table: "InstagramConnections",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TikTokConnections_Workspaces_WorkspaceId",
                table: "TikTokConnections",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WhatsAppConnections_Workspaces_WorkspaceId",
                table: "WhatsAppConnections",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Workspaces_AspNetUsers_OwnerId",
                table: "Workspaces",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FacebookConnections_Workspaces_WorkspaceId",
                table: "FacebookConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_InstagramConnections_Workspaces_WorkspaceId",
                table: "InstagramConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_TikTokConnections_Workspaces_WorkspaceId",
                table: "TikTokConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_WhatsAppConnections_Workspaces_WorkspaceId",
                table: "WhatsAppConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_Workspaces_AspNetUsers_OwnerId",
                table: "Workspaces");

            migrationBuilder.RenameColumn(
                name: "OwnerId",
                table: "Workspaces",
                newName: "TenantId");

            migrationBuilder.RenameIndex(
                name: "IX_Workspaces_OwnerId",
                table: "Workspaces",
                newName: "IX_Workspaces_TenantId");

            migrationBuilder.RenameColumn(
                name: "WorkspaceId",
                table: "WhatsAppConnections",
                newName: "OwnerUserId");

            migrationBuilder.RenameIndex(
                name: "IX_WhatsAppConnections_WorkspaceId",
                table: "WhatsAppConnections",
                newName: "IX_WhatsAppConnections_OwnerUserId");

            migrationBuilder.RenameColumn(
                name: "WorkspaceId",
                table: "TikTokConnections",
                newName: "OwnerUserId");

            migrationBuilder.RenameIndex(
                name: "IX_TikTokConnections_WorkspaceId",
                table: "TikTokConnections",
                newName: "IX_TikTokConnections_OwnerUserId");

            migrationBuilder.RenameColumn(
                name: "WorkspaceId",
                table: "InstagramConnections",
                newName: "OwnerUserId");

            migrationBuilder.RenameIndex(
                name: "IX_InstagramConnections_WorkspaceId",
                table: "InstagramConnections",
                newName: "IX_InstagramConnections_OwnerUserId");

            migrationBuilder.RenameColumn(
                name: "WorkspaceId",
                table: "FacebookConnections",
                newName: "OwnerUserId");

            migrationBuilder.RenameIndex(
                name: "IX_FacebookConnections_WorkspaceId",
                table: "FacebookConnections",
                newName: "IX_FacebookConnections_OwnerUserId");

            migrationBuilder.RenameColumn(
                name: "WorkspaceId",
                table: "Agents",
                newName: "UserId");

            migrationBuilder.RenameIndex(
                name: "IX_Agents_WorkspaceId",
                table: "Agents",
                newName: "IX_Agents_UserId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_Workspaces_AspNetUsers_TenantId",
                table: "Workspaces",
                column: "TenantId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
