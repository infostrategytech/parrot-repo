using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalletTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActionBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    BalanceAfterMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PaymentOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderTransactionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletTransactions", x => x.Id);
                    table.CheckConstraint("CK_WalletTransactions_AmountMinor", "\"AmountMinor\" > 0");
                    table.CheckConstraint("CK_WalletTransactions_BalanceAfterMinor", "\"BalanceAfterMinor\" >= 0");
                    table.ForeignKey(
                        name: "FK_WalletTransactions_WorkspaceWallets_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "WorkspaceWallets",
                        principalColumn: "WorkspaceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_Workspace_CreatedAt",
                table: "WalletTransactions",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UQ_WalletTransactions_Reference",
                table: "WalletTransactions",
                column: "Reference",
                unique: true);

            // Orders paid before the ledger: each becomes a successful credit made by Accelerate, in the order it was
            // paid, and every wallet's balance becomes the sum of its credits. A workspace without a wallet gets one.
            migrationBuilder.Sql(
                """
                INSERT INTO "WorkspaceWallets" ("WorkspaceId", "Currency", "BalanceMinor", "CreatedAt")
                SELECT DISTINCT o."WorkspaceId", 'NGN', 0, CURRENT_TIMESTAMP
                FROM "PaymentOrders" o
                WHERE o."PaidAt" IS NOT NULL AND o."PaidMinor" > 0
                ON CONFLICT ("WorkspaceId") DO NOTHING;

                INSERT INTO "WalletTransactions" ("Id", "WorkspaceId", "Type", "Status", "Kind", "AmountMinor", "BalanceAfterMinor", "Currency",
                    "Reference", "PaymentOrderId", "ProviderTransactionId", "Description", "ActionBy", "CreatedAt")
                SELECT gen_random_uuid(), o."WorkspaceId", 'Credit', 'Success', 'Payment', o."PaidMinor",
                    SUM(o."PaidMinor") OVER (PARTITION BY o."WorkspaceId" ORDER BY o."PaidAt", o."Id"),
                    o."Currency", o."Reference", o."Id", o."ProviderTransactionId",
                    'Payment from ' || COALESCE(NULLIF(BTRIM(o."CustomerName"), ''), 'a customer'),
                    'system:accelerate',
                    o."PaidAt"
                FROM "PaymentOrders" o
                WHERE o."PaidAt" IS NOT NULL AND o."PaidMinor" > 0
                ON CONFLICT ("Reference") DO NOTHING;

                UPDATE "WorkspaceWallets" w
                SET "BalanceMinor" = t.total, "UpdatedAt" = t.latest
                FROM (SELECT "WorkspaceId", SUM("AmountMinor") AS total, MAX("CreatedAt") AS latest
                      FROM "WalletTransactions" WHERE "Status" = 'Success' GROUP BY "WorkspaceId") t
                WHERE w."WorkspaceId" = t."WorkspaceId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletTransactions");
        }
    }
}
