using Microsoft.EntityFrameworkCore.Migrations;
using Parrot.Catalog.Infrastructure.Data;

#nullable disable

namespace Parrot.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ItemsFormFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "duration",
                schema: "catalog",
                table: "products",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "form_draft",
                schema: "catalog",
                table: "products",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "hidden",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "lead_time",
                schema: "catalog",
                table: "products",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_form_draft_object",
                schema: "catalog",
                table: "products",
                sql: "form_draft IS NULL OR jsonb_typeof(form_draft) = 'object'");

            // v_products is products.*, fixed when it was made: remade so readers see the new columns.
            migrationBuilder.Sql(SecuritySql.ReplaceViews("v_products"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // v_products selects the columns being dropped, and most views read it; take them down and put them back after.
            migrationBuilder.Sql(string.Join(' ', ViewsOnProducts.Reverse().Select(view => $"DROP VIEW catalog.{view};")));

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_form_draft_object",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "duration",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "form_draft",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "hidden",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "lead_time",
                schema: "catalog",
                table: "products");

            migrationBuilder.Sql(SecuritySql.ReplaceViews(ViewsOnProducts));
            migrationBuilder.Sql($"GRANT SELECT ON {string.Join(", ", ViewsOnProducts.Select(view => $"catalog.{view}"))} TO {SecuritySql.ReaderRole};");
        }

        private static readonly string[] ViewsOnProducts =
        [
            "v_products", "v_product_variants", "v_product_attribute_index", "v_offers", "v_prices", "v_availability",
            "v_product_relationships", "v_media", "v_catalog_documents",
        ];
    }
}
