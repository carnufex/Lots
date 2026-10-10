using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VoiceConversation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Voice",
                table: "runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_runs_ConversationId",
                table: "runs",
                column: "ConversationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_runs_ConversationId",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "Voice",
                table: "runs");
        }
    }
}
