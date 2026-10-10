using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Proposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "proposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Profile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BaseVersion = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: true),
                    BaseYaml = table.Column<string>(type: "text", nullable: false),
                    ProposedYaml = table.Column<string>(type: "text", nullable: false),
                    Diff = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EvaluationJson = table.Column<string>(type: "text", nullable: true),
                    Regresses = table.Column<bool>(type: "boolean", nullable: true),
                    PrUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proposals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_proposals_Profile_CreatedAt",
                table: "proposals",
                columns: new[] { "Profile", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proposals");
        }
    }
}
