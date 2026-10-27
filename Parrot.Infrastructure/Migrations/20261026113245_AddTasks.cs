using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CustomerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CustomerChannel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CustomerContact = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerSender = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Address = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    RaisedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RaisedById = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RaisedByAgent = table.Column<bool>(type: "boolean", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RaisedInMessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AssigneeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AssigneeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Urgent = table.Column<bool>(type: "boolean", nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueHasTime = table.Column<bool>(type: "boolean", nullable: false),
                    Lines = table.Column<string>(type: "jsonb", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkTasks_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkTaskEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Who = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WhoId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TargetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTaskEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkTaskEvents_WorkTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "WorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskEvents_TaskId",
                table: "WorkTaskEvents",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_ConnectionId_CustomerSender",
                table: "WorkTasks",
                columns: new[] { "ConnectionId", "CustomerSender" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_WorkspaceId_Number",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_WorkspaceId_State",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTaskEvents");

            migrationBuilder.DropTable(
                name: "WorkTasks");
        }
    }
}
