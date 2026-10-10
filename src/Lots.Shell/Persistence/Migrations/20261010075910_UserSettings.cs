using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_settings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Talkativeness = table.Column<int>(type: "integer", nullable: false),
                    Warmth = table.Column<int>(type: "integer", nullable: false),
                    Formality = table.Column<int>(type: "integer", nullable: false),
                    Expressiveness = table.Column<int>(type: "integer", nullable: false),
                    Pace = table.Column<int>(type: "integer", nullable: false),
                    VoiceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    VoiceSeconds = table.Column<double>(type: "double precision", nullable: true),
                    VoiceConsentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_settings", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_settings");
        }
    }
}
