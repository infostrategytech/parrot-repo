using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Parrot.Catalog.Infrastructure.Data;

#nullable disable

namespace Parrot.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ItemAiStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_ai_status",
                schema: "catalog",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sent_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sent_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reported_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    report_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    text_status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    photos_rejected = table.Column<int>(type: "integer", nullable: false),
                    photos_failed = table.Column<int>(type: "integer", nullable: false),
                    photos = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_ai_status", x => x.product_id);
                    table.ForeignKey(
                        name: "fk_item_ai_status_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_ai_status_tenant_id",
                schema: "catalog",
                table: "item_ai_status",
                column: "tenant_id");

            migrationBuilder.Sql(SecuritySql.ItemAiStatusUp());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecuritySql.ItemAiStatusDown());
            migrationBuilder.DropTable(
                name: "item_ai_status",
                schema: "catalog");
        }
    }
}
