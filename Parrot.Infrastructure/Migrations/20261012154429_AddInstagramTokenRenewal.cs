using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramTokenRenewal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AccessTokenExpiresAt",
                table: "InstagramConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessTokenRejectedAt",
                table: "InstagramConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessTokenRenewedAt",
                table: "InstagramConnections",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessTokenExpiresAt",
                table: "InstagramConnections");

            migrationBuilder.DropColumn(
                name: "AccessTokenRejectedAt",
                table: "InstagramConnections");

            migrationBuilder.DropColumn(
                name: "AccessTokenRenewedAt",
                table: "InstagramConnections");
        }
    }
}
