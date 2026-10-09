using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentChannelsAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channels",
                table: "Agents",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDraft",
                table: "Agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Existing agents get an empty document, never an empty string:
            // jsonb refuses '' and the API reads {} as the default settings.
            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "Agents",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Agents",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Channels",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "IsDraft",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "SettingsJson",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Agents");
        }
    }
}
