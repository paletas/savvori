using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Savvori.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingsAndCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MatchCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StoreProductAId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StoreProductBId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cosine = table.Column<double>(type: "REAL", nullable: false),
                    SizeKnown = table.Column<bool>(type: "INTEGER", nullable: false),
                    BrandCheck = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", nullable: false),
                    ModelDigest = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchCandidates_StoreProducts_StoreProductAId",
                        column: x => x.StoreProductAId,
                        principalTable: "StoreProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MatchCandidates_StoreProducts_StoreProductBId",
                        column: x => x.StoreProductBId,
                        principalTable: "StoreProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoreProductEmbeddings",
                columns: table => new
                {
                    StoreProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Vector = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", nullable: false),
                    ModelDigest = table.Column<string>(type: "TEXT", nullable: false),
                    Dimension = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTextHash = table.Column<string>(type: "TEXT", nullable: false),
                    EmbeddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreProductEmbeddings", x => x.StoreProductId);
                    table.ForeignKey(
                        name: "FK_StoreProductEmbeddings_StoreProducts_StoreProductId",
                        column: x => x.StoreProductId,
                        principalTable: "StoreProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_Cosine",
                table: "MatchCandidates",
                column: "Cosine");

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_StoreProductAId_StoreProductBId",
                table: "MatchCandidates",
                columns: new[] { "StoreProductAId", "StoreProductBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_StoreProductBId",
                table: "MatchCandidates",
                column: "StoreProductBId");

            migrationBuilder.CreateIndex(
                name: "IX_StoreProductEmbeddings_EmbeddedAt",
                table: "StoreProductEmbeddings",
                column: "EmbeddedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchCandidates");

            migrationBuilder.DropTable(
                name: "StoreProductEmbeddings");
        }
    }
}
