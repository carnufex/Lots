using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoSpeak",
                table: "user_settings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultContext",
                table: "user_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Theme",
                table: "user_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UiLanguage",
                table: "user_settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoSpeak",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "DefaultContext",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "Theme",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "UiLanguage",
                table: "user_settings");
        }
    }
}
