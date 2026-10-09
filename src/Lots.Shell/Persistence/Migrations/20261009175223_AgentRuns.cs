using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalAnswer",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "runs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "run_messages",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: true),
                    ToolCallsJson = table.Column<string>(type: "text", nullable: true),
                    ToolCallId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_messages", x => new { x.RunId, x.Seq });
                    table.ForeignKey(
                        name: "FK_run_messages_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runs_Status",
                table: "runs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_messages");

            migrationBuilder.DropIndex(
                name: "IX_runs_Status",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "FinalAnswer",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "runs");
        }
    }
}
