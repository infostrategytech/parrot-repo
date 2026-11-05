using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppCatalogueItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WhatsAppCatalogueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MetaCatalogueId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetaProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RetailerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Availability = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReviewStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Brand = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Condition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SalePrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Inventory = table.Column<int>(type: "integer", nullable: true),
                    CatalogProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppCatalogueItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WhatsAppCatalogueItems_WhatsAppConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "WhatsAppConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppCatalogueItems_ConnectionId",
                table: "WhatsAppCatalogueItems",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppCatalogueItems_ConnectionId_RetailerId",
                table: "WhatsAppCatalogueItems",
                columns: new[] { "ConnectionId", "RetailerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WhatsAppCatalogueItems");
        }
    }
}
