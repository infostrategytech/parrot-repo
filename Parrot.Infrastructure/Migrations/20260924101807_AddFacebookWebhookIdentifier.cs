using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFacebookWebhookIdentifier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebhookIdentifier",
                table: "FacebookConnections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Rows created before this column exist in production with an empty identifier. Give each one its
            // own 64-character reference here, or the unique index below fails on the second row.
            migrationBuilder.Sql(
                """
                UPDATE "FacebookConnections"
                SET "WebhookIdentifier" =
                    md5(random()::text || clock_timestamp()::text || "Id"::text)
                    || md5(random()::text || clock_timestamp()::text || "Id"::text)
                WHERE "WebhookIdentifier" = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_FacebookConnections_WebhookIdentifier",
                table: "FacebookConnections",
                column: "WebhookIdentifier",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FacebookConnections_WebhookIdentifier",
                table: "FacebookConnections");

            migrationBuilder.DropColumn(
                name: "WebhookIdentifier",
                table: "FacebookConnections");
        }
    }
}
