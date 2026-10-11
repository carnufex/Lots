using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupContextJson",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Archived",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Isolated",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "conversation_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Icon = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Pinned = table.Column<bool>(type: "boolean", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    Instructions = table.Column<string>(type: "text", nullable: true),
                    DefaultContext = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ShareContext = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_groups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_UserId_GroupId",
                table: "conversations",
                columns: new[] { "UserId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_groups_UserId",
                table: "conversation_groups",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_groups");

            migrationBuilder.DropIndex(
                name: "IX_conversations_UserId_GroupId",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "GroupContextJson",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "Archived",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Isolated",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Pinned",
                table: "conversations");
        }
    }
}
