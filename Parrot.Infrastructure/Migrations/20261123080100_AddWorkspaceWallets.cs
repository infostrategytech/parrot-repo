using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceWallets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkspaceWallets",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BalanceMinor = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceWallets", x => x.WorkspaceId);
                    table.CheckConstraint("CK_WorkspaceWallets_BalanceMinor", "\"BalanceMinor\" >= 0");
                    table.ForeignKey(
                        name: "FK_WorkspaceWallets_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Every workspace made before wallets gets an empty one; new workspaces are made with theirs.
            migrationBuilder.Sql("""
                INSERT INTO "WorkspaceWallets" ("WorkspaceId", "Currency", "BalanceMinor", "CreatedAt")
                SELECT "Id", 'NGN', 0, CURRENT_TIMESTAMP FROM "Workspaces"
                ON CONFLICT ("WorkspaceId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspaceWallets");
        }
    }
}
