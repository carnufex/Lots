using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Channels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplyJson",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "channel_events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_events", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_events");

            migrationBuilder.DropColumn(
                name: "ReplyJson",
                table: "runs");
        }
    }
}
