using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lots.Shell.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EvalCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eval_candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Signature = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Profile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    Problem = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Runs = table.Column<int>(type: "integer", nullable: false),
                    Impact = table.Column<int>(type: "integer", nullable: false),
                    RunIdsJson = table.Column<string>(type: "text", nullable: false),
                    DraftJson = table.Column<string>(type: "text", nullable: false),
                    CaseJson = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReviewedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FirstSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_candidates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_eval_candidates_ClusterKey",
                table: "eval_candidates",
                column: "ClusterKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_eval_candidates_State_Impact",
                table: "eval_candidates",
                columns: new[] { "State", "Impact" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eval_candidates");
        }
    }
}
