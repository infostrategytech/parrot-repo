using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ItemAiReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "photos_status",
                schema: "catalog",
                table: "item_ai_status",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "report_type",
                schema: "catalog",
                table: "item_ai_status",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "searchable",
                schema: "catalog",
                table: "item_ai_status",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "sent_version",
                schema: "catalog",
                table: "item_ai_status",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "text_reason",
                schema: "catalog",
                table: "item_ai_status",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "text_reason_code",
                schema: "catalog",
                table: "item_ai_status",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "photos_status",
                schema: "catalog",
                table: "item_ai_status");

            migrationBuilder.DropColumn(
                name: "report_type",
                schema: "catalog",
                table: "item_ai_status");

            migrationBuilder.DropColumn(
                name: "searchable",
                schema: "catalog",
                table: "item_ai_status");

            migrationBuilder.DropColumn(
                name: "sent_version",
                schema: "catalog",
                table: "item_ai_status");

            migrationBuilder.DropColumn(
                name: "text_reason",
                schema: "catalog",
                table: "item_ai_status");

            migrationBuilder.DropColumn(
                name: "text_reason_code",
                schema: "catalog",
                table: "item_ai_status");
        }
    }
}
