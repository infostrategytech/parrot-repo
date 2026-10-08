using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionAppSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppSecretEncrypted",
                table: "WhatsAppConnections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppSecretEncrypted",
                table: "FacebookConnections",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppSecretEncrypted",
                table: "WhatsAppConnections");

            migrationBuilder.DropColumn(
                name: "AppSecretEncrypted",
                table: "FacebookConnections");
        }
    }
}
