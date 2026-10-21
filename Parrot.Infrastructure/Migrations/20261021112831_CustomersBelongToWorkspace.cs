using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CustomersBelongToWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Customers_AspNetUsers_OwnerUserId",
                table: "Customers");

            migrationBuilder.RenameColumn(
                name: "OwnerUserId",
                table: "Customers",
                newName: "WorkspaceId");

            migrationBuilder.RenameIndex(
                name: "IX_Customers_OwnerUserId",
                table: "Customers",
                newName: "IX_Customers_WorkspaceId");

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_Workspaces_WorkspaceId",
                table: "Customers",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Workspaces_WorkspaceId",
                table: "Customers");

            migrationBuilder.RenameColumn(
                name: "WorkspaceId",
                table: "Customers",
                newName: "OwnerUserId");

            migrationBuilder.RenameIndex(
                name: "IX_Customers_WorkspaceId",
                table: "Customers",
                newName: "IX_Customers_OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_AspNetUsers_OwnerUserId",
                table: "Customers",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
