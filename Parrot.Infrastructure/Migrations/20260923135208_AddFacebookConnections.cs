using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFacebookConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FacebookConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "text", nullable: false),
                    VerifyTokenEncrypted = table.Column<string>(type: "text", nullable: false),
                    PageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PageName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AccessTokenEncrypted = table.Column<string>(type: "text", nullable: true),
                    WebhookVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisabledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FacebookConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FacebookConnections_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FacebookConnections_OwnerUserId",
                table: "FacebookConnections",
                column: "OwnerUserId",
                unique: true,
                filter: "\"DisabledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FacebookConnections_PageId",
                table: "FacebookConnections",
                column: "PageId",
                unique: true,
                filter: "\"PageId\" IS NOT NULL AND \"DisabledAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FacebookConnections");
        }
    }
}
