using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QuotaOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quota_overrides",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RunsPerMinute = table.Column<int>(type: "integer", nullable: true),
                    ConcurrentRuns = table.Column<int>(type: "integer", nullable: true),
                    TokensPerDay = table.Column<long>(type: "bigint", nullable: true),
                    ToolCallsPerRun = table.Column<int>(type: "integer", nullable: true),
                    SpeechSecondsPerDay = table.Column<double>(type: "double precision", nullable: true),
                    SetBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_overrides", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quota_overrides");
        }
    }
}
