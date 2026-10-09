using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RunSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_steps",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ToolCallId = table.Column<string>(type: "text", nullable: true),
                    ArgumentsJson = table.Column<string>(type: "text", nullable: true),
                    Result = table.Column<string>(type: "text", nullable: true),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_steps", x => new { x.RunId, x.Seq });
                    table.ForeignKey(
                        name: "FK_run_steps_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_steps");
        }
    }
}
