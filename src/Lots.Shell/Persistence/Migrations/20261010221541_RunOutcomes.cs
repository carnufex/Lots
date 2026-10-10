using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RunOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_outcomes",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserHash = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Profile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WallMs = table.Column<long>(type: "bigint", nullable: false),
                    ModelMs = table.Column<long>(type: "bigint", nullable: false),
                    ToolMs = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Steps = table.Column<int>(type: "integer", nullable: false),
                    ModelCalls = table.Column<int>(type: "integer", nullable: false),
                    ToolCalls = table.Column<int>(type: "integer", nullable: false),
                    DistinctTools = table.Column<int>(type: "integer", nullable: false),
                    Fallbacks = table.Column<int>(type: "integer", nullable: false),
                    TokensIn = table.Column<int>(type: "integer", nullable: false),
                    TokensOut = table.Column<int>(type: "integer", nullable: false),
                    Cost = table.Column<double>(type: "double precision", nullable: false),
                    ToolErrors = table.Column<int>(type: "integer", nullable: false),
                    PolicyDenials = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRefusals = table.Column<int>(type: "integer", nullable: false),
                    TimedOut = table.Column<bool>(type: "boolean", nullable: false),
                    StepLimitHit = table.Column<bool>(type: "boolean", nullable: false),
                    VoiceSkippedTools = table.Column<bool>(type: "boolean", nullable: false),
                    EmptyAnswer = table.Column<bool>(type: "boolean", nullable: false),
                    Refused = table.Column<bool>(type: "boolean", nullable: false),
                    AnswerChars = table.Column<int>(type: "integer", nullable: false),
                    Spoken = table.Column<bool>(type: "boolean", nullable: false),
                    IsRetry = table.Column<bool>(type: "boolean", nullable: false),
                    UserRetried = table.Column<bool>(type: "boolean", nullable: false),
                    FollowUp = table.Column<bool>(type: "boolean", nullable: false),
                    FeedbackRating = table.Column<int>(type: "integer", nullable: true),
                    JudgeScore = table.Column<double>(type: "double precision", nullable: true),
                    JudgeRubric = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ToolsJson = table.Column<string>(type: "text", nullable: true),
                    TraceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Dirty = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_outcomes", x => x.RunId);
                    table.ForeignKey(
                        name: "FK_run_outcomes_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_run_outcomes_Dirty",
                table: "run_outcomes",
                column: "Dirty");

            migrationBuilder.CreateIndex(
                name: "IX_run_outcomes_EndedAt",
                table: "run_outcomes",
                column: "EndedAt");

            migrationBuilder.CreateIndex(
                name: "IX_run_outcomes_Profile_ProfileVersion_EndedAt",
                table: "run_outcomes",
                columns: new[] { "Profile", "ProfileVersion", "EndedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_outcomes");
        }
    }
}
