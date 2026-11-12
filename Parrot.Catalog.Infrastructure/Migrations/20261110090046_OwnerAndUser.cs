using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OwnerAndUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "owner_id",
                schema: "catalog",
                table: "tenants",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owner_id",
                schema: "catalog",
                table: "outbox",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "user_id",
                schema: "catalog",
                table: "outbox",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "owner_id",
                schema: "catalog",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "owner_id",
                schema: "catalog",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "user_id",
                schema: "catalog",
                table: "outbox");
        }
    }
}
