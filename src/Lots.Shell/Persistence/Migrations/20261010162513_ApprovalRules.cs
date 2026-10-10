using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "approvals",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "approvals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequiredApprovals",
                table: "approvals",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Risk",
                table: "approvals",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Event = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approvals_Status_ExpiresAt",
                table: "approvals",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_outbox_SentAt_NextAttemptAt",
                table: "notification_outbox",
                columns: new[] { "SentAt", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_outbox");

            migrationBuilder.DropIndex(
                name: "IX_approvals_Status_ExpiresAt",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "RequiredApprovals",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "Risk",
                table: "approvals");
        }
    }
}
