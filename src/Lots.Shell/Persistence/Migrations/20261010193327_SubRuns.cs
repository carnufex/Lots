using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Depth",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentRunId",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StepLimit",
                table: "runs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Depth",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "ParentRunId",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "StepLimit",
                table: "runs");
        }
    }
}
