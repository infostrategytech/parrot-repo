using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwnerRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Permissions = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerRoles_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FullName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Grants = table.Column<string>(type: "jsonb", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InvitedByUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "text", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamInvites_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", nullable: true),
                    SystemRole = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CustomRoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Channels = table.Column<string>(type: "text", nullable: false),
                    InvitedByUserId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceGrants", x => x.Id);
                    table.CheckConstraint("CK_WorkspaceGrants_OneRole", "(\"SystemRole\" IS NULL) <> (\"CustomRoleId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_WorkspaceGrants_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceGrants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceGrants_OwnerRoles_CustomRoleId",
                        column: x => x.CustomRoleId,
                        principalTable: "OwnerRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceGrants_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerRoles_OwnerId_NormalizedName",
                table: "OwnerRoles",
                columns: new[] { "OwnerId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvites_OwnerId_NormalizedEmail",
                table: "TeamInvites",
                columns: new[] { "OwnerId", "NormalizedEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvites_TokenHash",
                table: "TeamInvites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceGrants_CustomRoleId",
                table: "WorkspaceGrants",
                column: "CustomRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceGrants_OwnerId_UserId_AllWorkspaces",
                table: "WorkspaceGrants",
                columns: new[] { "OwnerId", "UserId" },
                unique: true,
                filter: "\"WorkspaceId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceGrants_OwnerId_UserId_WorkspaceId",
                table: "WorkspaceGrants",
                columns: new[] { "OwnerId", "UserId", "WorkspaceId" },
                unique: true,
                filter: "\"WorkspaceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceGrants_UserId",
                table: "WorkspaceGrants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceGrants_WorkspaceId",
                table: "WorkspaceGrants",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TeamInvites");

            migrationBuilder.DropTable(
                name: "WorkspaceGrants");

            migrationBuilder.DropTable(
                name: "OwnerRoles");
        }
    }
}
