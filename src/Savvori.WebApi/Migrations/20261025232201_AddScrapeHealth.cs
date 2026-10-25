using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddScrapeHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DropRatio",
                table: "ScrapingJobs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthReason",
                table: "ScrapingJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviousHealthyCount",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProductsReturned",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StaleMarked",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "StaleMarkingSkipped",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WarningCount",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ScrapingRunOutcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScrapingJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Result = table.Column<int>(type: "INTEGER", nullable: false),
                    PagesFetched = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductsFound = table.Column<int>(type: "INTEGER", nullable: false),
                    TilesSeen = table.Column<int>(type: "INTEGER", nullable: false),
                    Retries = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusCounts = table.Column<string>(type: "TEXT", nullable: false),
                    FirstError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapingRunOutcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScrapingRunOutcomes_ScrapingJobs_ScrapingJobId",
                        column: x => x.ScrapingJobId,
                        principalTable: "ScrapingJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingRunOutcomes_Key_ScrapingJobId",
                table: "ScrapingRunOutcomes",
                columns: new[] { "Key", "ScrapingJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingRunOutcomes_ScrapingJobId",
                table: "ScrapingRunOutcomes",
                column: "ScrapingJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScrapingRunOutcomes");

            migrationBuilder.DropColumn(
                name: "DropRatio",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "HealthReason",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "PreviousHealthyCount",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProductsReturned",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "StaleMarked",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "StaleMarkingSkipped",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "WarningCount",
                table: "ScrapingJobs");
        }
    }
}
