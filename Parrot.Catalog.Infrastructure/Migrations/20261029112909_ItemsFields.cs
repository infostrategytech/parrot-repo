using Microsoft.EntityFrameworkCore.Migrations;
using Parrot.Catalog.Infrastructure.Data;

#nullable disable

namespace Parrot.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ItemsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "price_on_request",
                schema: "catalog",
                table: "offers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "unit",
                schema: "catalog",
                table: "offers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "lead_time_days",
                schema: "catalog",
                table: "availability",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                schema: "catalog",
                table: "availability",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "in_stock");

            migrationBuilder.AddCheckConstraint(
                name: "ck_offers_unit",
                schema: "catalog",
                table: "offers",
                sql: "unit IS NULL OR length(unit) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_lead_time",
                schema: "catalog",
                table: "availability",
                sql: "lead_time_days IS NULL OR (lead_time_days >= 0 AND mode = 'made_to_order')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_mode",
                schema: "catalog",
                table: "availability",
                sql: "mode IN ('in_stock', 'made_to_order', 'bookings')");

            migrationBuilder.Sql(SecuritySql.ReplaceViews("v_offers", "v_availability"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The views select the columns being dropped; take them down first and put them back after.
            migrationBuilder.Sql("DROP VIEW catalog.v_prices; DROP VIEW catalog.v_offers; DROP VIEW catalog.v_availability;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_offers_unit",
                schema: "catalog",
                table: "offers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_lead_time",
                schema: "catalog",
                table: "availability");

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_mode",
                schema: "catalog",
                table: "availability");

            migrationBuilder.DropColumn(
                name: "price_on_request",
                schema: "catalog",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "unit",
                schema: "catalog",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "lead_time_days",
                schema: "catalog",
                table: "availability");

            migrationBuilder.DropColumn(
                name: "mode",
                schema: "catalog",
                table: "availability");

            migrationBuilder.Sql(SecuritySql.ReplaceViews("v_offers", "v_prices", "v_availability"));
            migrationBuilder.Sql($"GRANT SELECT ON catalog.v_offers, catalog.v_prices, catalog.v_availability TO {SecuritySql.ReaderRole};");
        }
    }
}
