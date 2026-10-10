using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Sensitivity",
                table: "runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Public"); // runs before #89 read no classified data

            migrationBuilder.AddColumn<string>(
                name: "Routing",
                table: "run_steps",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sensitivity",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "Routing",
                table: "run_steps");
        }
    }
}
