using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VoiceTimings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "voice_usage",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                table: "voice_usage",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_voice_usage_ConversationId",
                table: "voice_usage",
                column: "ConversationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_voice_usage_ConversationId",
                table: "voice_usage");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "voice_usage");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "voice_usage");
        }
    }
}
