using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace Parrot.Infrastructure.Migrations;

public partial class AddWhatsAppConnectionAccessToken : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "AccessTokenEncrypted", table: "WhatsAppConnections", type: "text", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "AccessTokenEncrypted", table: "WhatsAppConnections");
}
