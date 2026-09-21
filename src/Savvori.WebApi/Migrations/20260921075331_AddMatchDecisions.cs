using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DecidedAt",
                table: "MatchCandidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JudgeModel",
                table: "MatchCandidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JudgeVerdict",
                table: "MatchCandidates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "MatchCandidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "MatchCandidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "MatchCandidates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Suggestion",
                table: "MatchCandidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MatchMerges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CandidateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SurvivorProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RetiredProductId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RetiredProductJson = table.Column<string>(type: "TEXT", nullable: true),
                    SurvivorPreviousCategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MovedStoreProductsJson = table.Column<string>(type: "TEXT", nullable: false),
                    MovedListItemsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UndoneAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchMerges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_Status",
                table: "MatchCandidates",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MatchMerges_CandidateId",
                table: "MatchMerges",
                column: "CandidateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchMerges");

            migrationBuilder.DropIndex(
                name: "IX_MatchCandidates_Status",
                table: "MatchCandidates");

            migrationBuilder.DropColumn(
                name: "DecidedAt",
                table: "MatchCandidates");

            migrationBuilder.DropColumn(
                name: "JudgeModel",
                table: "MatchCandidates");

            migrationBuilder.DropColumn(
                name: "JudgeVerdict",
                table: "MatchCandidates");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "MatchCandidates");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "MatchCandidates");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "MatchCandidates");

            migrationBuilder.DropColumn(
                name: "Suggestion",
                table: "MatchCandidates");
        }
    }
}
