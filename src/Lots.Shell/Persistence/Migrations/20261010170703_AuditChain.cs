using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Hash",
                table: "audit_log",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrevHash",
                table: "audit_log",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Seq",
                table: "audit_log",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_forward_state",
                columns: table => new
                {
                    Target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastSeq = table.Column<long>(type: "bigint", nullable: false),
                    LastSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_forward_state", x => x.Target);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_Seq",
                table: "audit_log",
                column: "Seq",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_forward_state");

            migrationBuilder.DropIndex(
                name: "IX_audit_log_Seq",
                table: "audit_log");

            migrationBuilder.DropColumn(
                name: "Hash",
                table: "audit_log");

            migrationBuilder.DropColumn(
                name: "PrevHash",
                table: "audit_log");

            migrationBuilder.DropColumn(
                name: "Seq",
                table: "audit_log");
        }
    }
}
