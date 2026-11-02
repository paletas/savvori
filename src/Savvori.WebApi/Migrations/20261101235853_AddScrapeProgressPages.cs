using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddScrapeProgressPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProgressDoneKeys",
                table: "ScrapingJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressKeyPages",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressKeyPagesTotal",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressPagesDone",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProgressDoneKeys",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProgressKeyPages",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProgressKeyPagesTotal",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProgressPagesDone",
                table: "ScrapingJobs");
        }
    }
}
