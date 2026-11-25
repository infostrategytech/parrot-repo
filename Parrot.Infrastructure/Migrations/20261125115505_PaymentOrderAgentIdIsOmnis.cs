using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaymentOrderAgentIdIsOmnis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Agents live in Omni: an order notes the agent as Omni names it (agt_…), or none. Earlier orders keep their UUID as text.
            migrationBuilder.AlterColumn<string>(
                name: "AgentId",
                table: "PaymentOrders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to a UUID: an agent Omni named (agt_…) or none can't be one, so it becomes the empty UUID.
            migrationBuilder.Sql(
                """
                ALTER TABLE "PaymentOrders" ALTER COLUMN "AgentId" TYPE uuid
                    USING CASE WHEN "AgentId" ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                        THEN "AgentId"::uuid ELSE '00000000-0000-0000-0000-000000000000'::uuid END;
                ALTER TABLE "PaymentOrders" ALTER COLUMN "AgentId" SET NOT NULL;
                """);
        }
    }
}
