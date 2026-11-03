using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    PrimaryCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerLinks_Customers_LinkedCustomerId",
                        column: x => x.LinkedCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerLinks_Customers_PrimaryCustomerId",
                        column: x => x.PrimaryCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLinks_LinkedCustomerId",
                table: "CustomerLinks",
                column: "LinkedCustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLinks_PrimaryCustomerId",
                table: "CustomerLinks",
                column: "PrimaryCustomerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerLinks");
        }
    }
}
