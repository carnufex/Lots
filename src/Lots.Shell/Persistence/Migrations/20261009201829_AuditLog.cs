using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Roles = table.Column<string>(type: "text", nullable: false),
                    Profile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tool = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ArgumentsJson = table.Column<string>(type: "text", nullable: true),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    ApproverId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResultStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_RunId",
                table: "audit_log",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_UserId_At",
                table: "audit_log",
                columns: new[] { "UserId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");
        }
    }
}
