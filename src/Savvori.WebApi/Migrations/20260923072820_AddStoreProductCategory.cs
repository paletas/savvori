using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreProductCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "StoreProducts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "StoreProducts");
        }
    }
}
