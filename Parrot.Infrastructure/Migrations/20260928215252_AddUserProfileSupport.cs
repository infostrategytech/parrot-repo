using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfileSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "BusinessName",
                table: "AspNetUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "AspNetUsers",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneDialCode",
                table: "AspNetUsers",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PhoneDialCode_PhoneNumber",
                table: "AspNetUsers",
                columns: new[] { "PhoneDialCode", "PhoneNumber" },
                unique: true,
                filter: "\"PhoneDialCode\" IS NOT NULL AND \"PhoneNumber\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PhoneDialCode_PhoneNumber",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PhoneDialCode",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<string>(
                name: "BusinessName",
                table: "AspNetUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);
        }
    }
}
