using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Parrot.Catalog.Infrastructure.Data;

#nullable disable

namespace Parrot.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MediaAiStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ai_status",
                schema: "catalog",
                table: "media",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ai_status_at",
                schema: "catalog",
                table: "media",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_url",
                schema: "catalog",
                table: "media",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            // The relay's access to the marks, readers' view of them, and the marks from reports already received.
            migrationBuilder.Sql(SecuritySql.MediaAiStatusUp());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecuritySql.MediaAiStatusDown());

            migrationBuilder.DropColumn(
                name: "ai_status",
                schema: "catalog",
                table: "media");

            migrationBuilder.DropColumn(
                name: "ai_status_at",
                schema: "catalog",
                table: "media");

            migrationBuilder.DropColumn(
                name: "ai_url",
                schema: "catalog",
                table: "media");

            migrationBuilder.Sql(SecuritySql.ReplaceViews("v_media"));
            migrationBuilder.Sql($"GRANT SELECT ON catalog.v_media TO {SecuritySql.ReaderRole};");
        }
    }
}
