using Microsoft.EntityFrameworkCore.Migrations;
using Parrot.Catalog.Infrastructure.Data;

#nullable disable

namespace Parrot.Catalog.Infrastructure.Migrations
{
    /// <summary>
    /// Hidden items are kept but not offered: readers, and so the AI tools, stop seeing them.
    /// Every other reader view filters through v_products, so their variants, offers, prices,
    /// availability, photos and links go with them.
    /// </summary>
    public partial class ReadersSkipHidden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecuritySql.ProductsView.Replace("CREATE VIEW", "CREATE OR REPLACE VIEW", StringComparison.Ordinal));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecuritySql.ProductsViewBeforeHidden.Replace("CREATE VIEW", "CREATE OR REPLACE VIEW", StringComparison.Ordinal));
        }
    }
}
