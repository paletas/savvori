using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class StoreProductArchitecture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fresh start: clear shopping list items first (FK on Products), then products.
            // Shopping lists are preserved; users keep their list structure.
            migrationBuilder.Sql("DELETE FROM \"ShoppingListItems\";");
            migrationBuilder.Sql("DELETE FROM \"Products\";");

            migrationBuilder.DropTable(
                name: "ProductPrices");

            migrationBuilder.DropTable(
                name: "ProductStoreLinks");

            migrationBuilder.CreateTable(
                name: "StoreCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreChainId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ParentStoreCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Url = table.Column<string>(type: "text", nullable: true),
                    LastScraped = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoreCategories_StoreCategories_ParentStoreCategoryId",
                        column: x => x.ParentStoreCategoryId,
                        principalTable: "StoreCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StoreCategories_StoreChains_StoreChainId",
                        column: x => x.StoreChainId,
                        principalTable: "StoreChains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoreCategoryMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductCategoryId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreCategoryMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoreCategoryMappings_ProductCategories_ProductCategoryId",
                        column: x => x.ProductCategoryId,
                        principalTable: "ProductCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StoreCategoryMappings_StoreCategories_StoreCategoryId",
                        column: x => x.StoreCategoryId,
                        principalTable: "StoreCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoreProducts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreChainId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    StoreCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    NormalizedName = table.Column<string>(type: "text", nullable: true),
                    Brand = table.Column<string>(type: "text", nullable: true),
                    EAN = table.Column<string>(type: "text", nullable: true),
                    ImageUrl = table.Column<string>(type: "text", nullable: true),
                    SourceUrl = table.Column<string>(type: "text", nullable: true),
                    Unit = table.Column<int>(type: "integer", nullable: false),
                    SizeValue = table.Column<decimal>(type: "numeric", nullable: true),
                    FirstSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastScraped = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    MatchStatus = table.Column<int>(type: "integer", nullable: false),
                    MatchMethod = table.Column<string>(type: "text", nullable: true),
                    MatchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoreProducts_Products_CanonicalProductId",
                        column: x => x.CanonicalProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StoreProducts_StoreCategories_StoreCategoryId",
                        column: x => x.StoreCategoryId,
                        principalTable: "StoreCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StoreProducts_StoreChains_StoreChainId",
                        column: x => x.StoreChainId,
                        principalTable: "StoreChains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoreProductPrices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    Currency = table.Column<string>(type: "text", nullable: false, defaultValue: "EUR"),
                    IsPromotion = table.Column<bool>(type: "boolean", nullable: false),
                    PromotionDescription = table.Column<string>(type: "text", nullable: true),
                    IsLatest = table.Column<bool>(type: "boolean", nullable: false),
                    ScrapedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreProductPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoreProductPrices_StoreProducts_StoreProductId",
                        column: x => x.StoreProductId,
                        principalTable: "StoreProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoreCategories_ParentStoreCategoryId",
                table: "StoreCategories",
                column: "ParentStoreCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_StoreCategories_StoreChainId_ExternalId",
                table: "StoreCategories",
                columns: new[] { "StoreChainId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoreCategoryMappings_ProductCategoryId",
                table: "StoreCategoryMappings",
                column: "ProductCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_StoreCategoryMappings_StoreCategoryId",
                table: "StoreCategoryMappings",
                column: "StoreCategoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoreProductPrices_StoreProductId_IsLatest",
                table: "StoreProductPrices",
                columns: new[] { "StoreProductId", "IsLatest" });

            migrationBuilder.CreateIndex(
                name: "IX_StoreProductPrices_StoreProductId_ScrapedAt",
                table: "StoreProductPrices",
                columns: new[] { "StoreProductId", "ScrapedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_CanonicalProductId_IsActive",
                table: "StoreProducts",
                columns: new[] { "CanonicalProductId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_EAN",
                table: "StoreProducts",
                column: "EAN");

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_NormalizedName",
                table: "StoreProducts",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_StoreCategoryId",
                table: "StoreProducts",
                column: "StoreCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_StoreChainId_ExternalId",
                table: "StoreProducts",
                columns: new[] { "StoreChainId", "ExternalId" },
                unique: true);

            // Unique partial index: only one IsLatest=true row per StoreProduct
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_store_product_prices_latest " +
                "ON \"StoreProductPrices\" (\"StoreProductId\") WHERE \"IsLatest\" = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoreCategoryMappings");

            migrationBuilder.DropTable(
                name: "StoreProductPrices");

            migrationBuilder.DropTable(
                name: "StoreProducts");

            migrationBuilder.DropTable(
                name: "StoreCategories");

            migrationBuilder.CreateTable(
                name: "ProductPrices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false, defaultValue: "EUR"),
                    IsLatest = table.Column<bool>(type: "boolean", nullable: false),
                    IsPromotion = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    PromotionDescription = table.Column<string>(type: "text", nullable: true),
                    SourceUrl = table.Column<string>(type: "text", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductPrices_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductPrices_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductStoreLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreChainId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_ProductPrices_ProductId_StoreId_IsLatest",
                table: "ProductPrices",
                columns: new[] { "ProductId", "StoreId", "IsLatest" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductPrices_StoreId",
                table: "ProductPrices",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductStoreLinks_ProductId",
                table: "ProductStoreLinks",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductStoreLinks_StoreChainId_ExternalId",
                table: "ProductStoreLinks",
                columns: new[] { "StoreChainId", "ExternalId" },
                unique: true);
        }
    }
}
