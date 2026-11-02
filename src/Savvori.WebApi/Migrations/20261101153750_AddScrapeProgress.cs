using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddScrapeProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatAt",
                table: "ScrapingJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastProgressAt",
                table: "ScrapingJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProgressKey",
                table: "ScrapingJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressKeysDone",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressKeysTotal",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressOffset",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressProductsSeen",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressTotal",
                table: "ScrapingJobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "LastProgressAt",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProgressKey",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProgressKeysDone",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProgressKeysTotal",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProgressOffset",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProgressProductsSeen",
                table: "ScrapingJobs");

            migrationBuilder.DropColumn(
                name: "ProgressTotal",
                table: "ScrapingJobs");
        }
    }
}
