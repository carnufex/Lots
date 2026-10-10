using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConfigResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "config_resources",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ManagedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Spec = table.Column<string>(type: "text", nullable: false),
                    AppliedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config_resources", x => new { x.Kind, x.Name });
                });

            migrationBuilder.CreateTable(
                name: "config_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Spec = table.Column<string>(type: "text", nullable: true),
                    ManagedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AppliedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config_versions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_config_versions_AppliedAt",
                table: "config_versions",
                column: "AppliedAt");

            migrationBuilder.CreateIndex(
                name: "IX_config_versions_Kind_Name_AppliedAt",
                table: "config_versions",
                columns: new[] { "Kind", "Name", "AppliedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "config_resources");

            migrationBuilder.DropTable(
                name: "config_versions");
        }
    }
}
