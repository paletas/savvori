using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBulkBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "MatchMerges",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "CategorySuggestions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BulkBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Threshold = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Applied = table.Column<int>(type: "INTEGER", nullable: false),
                    Blocked = table.Column<int>(type: "INTEGER", nullable: false),
                    Undone = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UndoneAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkBatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchMerges_BatchId",
                table: "MatchMerges",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_CategorySuggestions_BatchId",
                table: "CategorySuggestions",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkBatches_Kind_CreatedAt",
                table: "BulkBatches",
                columns: new[] { "Kind", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BulkBatches");

            migrationBuilder.DropIndex(
                name: "IX_MatchMerges_BatchId",
                table: "MatchMerges");

            migrationBuilder.DropIndex(
                name: "IX_CategorySuggestions_BatchId",
                table: "CategorySuggestions");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "MatchMerges");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "CategorySuggestions");
        }
    }
}
