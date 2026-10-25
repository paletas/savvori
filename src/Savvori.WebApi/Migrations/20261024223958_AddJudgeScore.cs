using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddJudgeScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "JudgeScore",
                table: "MatchCandidates",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JudgeScore",
                table: "MatchCandidates");
        }
    }
}
