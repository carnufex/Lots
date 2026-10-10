using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContextRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoutingJson",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoutingMode",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Rerouted",
                table: "run_outcomes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Routing",
                table: "run_outcomes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoutingJson",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "RoutingMode",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "Rerouted",
                table: "run_outcomes");

            migrationBuilder.DropColumn(
                name: "Routing",
                table: "run_outcomes");
        }
    }
}
