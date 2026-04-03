using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class SmartShoppingDataModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "Users",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "Users",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Stores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "Stores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "Stores",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "Stores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StoreChainId",
                table: "Stores",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "Products",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Brand",
                table: "Products",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "Products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EAN",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SizeValue",
                table: "Products",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Unit",
                table: "Products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "ProductPrices",
                type: "text",
                nullable: false,
                defaultValue: "EUR");

            migrationBuilder.AddColumn<bool>(
                name: "IsLatest",
                table: "ProductPrices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPromotion",
                table: "ProductPrices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PromotionDescription",
                table: "ProductPrices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "ProductPrices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                table: "ProductPrices",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProductCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    ParentCategoryId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCategories_ProductCategories_ParentCategoryId",
                        column: x => x.ParentCategoryId,
                        principalTable: "ProductCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StoreChains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    BaseUrl = table.Column<string>(type: "text", nullable: false),
                    LogoUrl = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreChains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductStoreLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreChainId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: true),
                    LastSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductStoreLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductStoreLinks_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductStoreLinks_StoreChains_StoreChainId",
                        column: x => x.StoreChainId,
                        principalTable: "StoreChains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScrapingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreChainId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProductsScraped = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapingJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScrapingJobs_StoreChains_StoreChainId",
                        column: x => x.StoreChainId,
                        principalTable: "StoreChains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScrapingLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScrapingJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapingLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScrapingLogs_ScrapingJobs_ScrapingJobId",
                        column: x => x.ScrapingJobId,
                        principalTable: "ScrapingJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stores_StoreChainId",
                table: "Stores",
                column: "StoreChainId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_CategoryId",
                table: "Products",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_EAN",
                table: "Products",
                column: "EAN");

            migrationBuilder.CreateIndex(
                name: "IX_Products_NormalizedName",
                table: "Products",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_ProductPrices_ProductId_StoreId_IsLatest",
                table: "ProductPrices",
                columns: new[] { "ProductId", "StoreId", "IsLatest" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductPrices_StoreId",
                table: "ProductPrices",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_ParentCategoryId",
                table: "ProductCategories",
                column: "ParentCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_Slug",
                table: "ProductCategories",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductStoreLinks_ProductId",
                table: "ProductStoreLinks",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductStoreLinks_StoreChainId_ExternalId",
                table: "ProductStoreLinks",
                columns: new[] { "StoreChainId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingJobs_StoreChainId",
                table: "ScrapingJobs",
                column: "StoreChainId");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapingLogs_ScrapingJobId",
                table: "ScrapingLogs",
                column: "ScrapingJobId");

            migrationBuilder.CreateIndex(
                name: "IX_StoreChains_Slug",
                table: "StoreChains",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductPrices_Products_ProductId",
                table: "ProductPrices",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductPrices_Stores_StoreId",
                table: "ProductPrices",
                column: "StoreId",
                principalTable: "Stores",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ProductCategories_CategoryId",
                table: "Products",
                column: "CategoryId",
                principalTable: "ProductCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Stores_StoreChains_StoreChainId",
                table: "Stores",
                column: "StoreChainId",
                principalTable: "StoreChains",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductPrices_Products_ProductId",
                table: "ProductPrices");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductPrices_Stores_StoreId",
                table: "ProductPrices");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_ProductCategories_CategoryId",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_Stores_StoreChains_StoreChainId",
                table: "Stores");

            migrationBuilder.DropTable(
                name: "ProductCategories");

            migrationBuilder.DropTable(
                name: "ProductStoreLinks");

            migrationBuilder.DropTable(
                name: "ScrapingLogs");

            migrationBuilder.DropTable(
                name: "ScrapingJobs");

            migrationBuilder.DropTable(
                name: "StoreChains");

            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Stores_StoreChainId",
                table: "Stores");

            migrationBuilder.DropIndex(
                name: "IX_Products_CategoryId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_EAN",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_NormalizedName",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_ProductPrices_ProductId_StoreId_IsLatest",
                table: "ProductPrices");

            migrationBuilder.DropIndex(
                name: "IX_ProductPrices_StoreId",
                table: "ProductPrices");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Address",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "City",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "StoreChainId",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "EAN",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SizeValue",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "ProductPrices");

            migrationBuilder.DropColumn(
                name: "IsLatest",
                table: "ProductPrices");

            migrationBuilder.DropColumn(
                name: "IsPromotion",
                table: "ProductPrices");

            migrationBuilder.DropColumn(
                name: "PromotionDescription",
                table: "ProductPrices");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "ProductPrices");

            migrationBuilder.DropColumn(
                name: "UnitPrice",
                table: "ProductPrices");

            migrationBuilder.AlterColumn<string>(
                name: "Category",
                table: "Products",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Brand",
                table: "Products",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
