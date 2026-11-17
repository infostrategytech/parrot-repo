using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Checkouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CheckoutExpiresAt",
                table: "PaymentOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldMode",
                table: "PaymentOrders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "AtPay");

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentStartedAt",
                table: "PaymentOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuotedTotalMinor",
                table: "PaymentOrders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Orders made before checkouts: the link's total was the quote, and stock was held when the link was made.
            migrationBuilder.Sql("UPDATE \"PaymentOrders\" SET \"QuotedTotalMinor\" = \"TotalMinor\";");

            migrationBuilder.CreateTable(
                name: "WorkspacePaymentSettings",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CheckoutMinutes = table.Column<int>(type: "integer", nullable: false),
                    HoldMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PaymentMinutes = table.Column<int>(type: "integer", nullable: false),
                    PriceChangeRule = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspacePaymentSettings", x => x.WorkspaceId);
                    table.CheckConstraint("CK_WorkspacePaymentSettings_CheckoutMinutes", "\"CheckoutMinutes\" BETWEEN 5 AND 1440");
                    table.CheckConstraint("CK_WorkspacePaymentSettings_PaymentMinutes", "\"PaymentMinutes\" BETWEEN 5 AND 120");
                    table.ForeignKey(
                        name: "FK_WorkspacePaymentSettings_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspacePaymentSettings");

            migrationBuilder.DropColumn(
                name: "CheckoutExpiresAt",
                table: "PaymentOrders");

            migrationBuilder.DropColumn(
                name: "HoldMode",
                table: "PaymentOrders");

            migrationBuilder.DropColumn(
                name: "PaymentStartedAt",
                table: "PaymentOrders");

            migrationBuilder.DropColumn(
                name: "QuotedTotalMinor",
                table: "PaymentOrders");
        }
    }
}
