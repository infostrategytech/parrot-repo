using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Parrot.Catalog.Infrastructure.Data;

#nullable disable

namespace Parrot.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StockHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_holds",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_holds", x => x.id);
                    table.CheckConstraint("ck_stock_holds_status", "status IN ('held', 'committed', 'released')");
                    table.ForeignKey(
                        name: "fk_stock_holds_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_holds_product_id",
                schema: "catalog",
                table: "stock_holds",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_holds_tenant_id_order_reference",
                schema: "catalog",
                table: "stock_holds",
                columns: new[] { "tenant_id", "order_reference" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_holds_tenant_id_product_id_status",
                schema: "catalog",
                table: "stock_holds",
                columns: new[] { "tenant_id", "product_id", "status" });

            // Tenant isolation, the writer's grants, and the reader's view of holds still held.
            migrationBuilder.Sql(SecuritySql.StockHoldsUp());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecuritySql.StockHoldsDown());

            migrationBuilder.DropTable(
                name: "stock_holds",
                schema: "catalog");
        }
    }
}
