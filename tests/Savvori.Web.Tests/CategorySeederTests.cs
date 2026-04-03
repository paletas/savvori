using Microsoft.EntityFrameworkCore;
using Savvori.WebApi;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public class CategorySeederTests
{
    private static SavvoriDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<SavvoriDbContext>()
            .UseInMemoryDatabase($"SeederTests_{Guid.NewGuid()}")
            .Options;
        return new SavvoriDbContext(options);
    }

    [Fact]
    public async Task SeedAsync_PopulatesCategories()
    {
        await using var db = CreateDb();
        await CategorySeeder.SeedAsync(db);
        var count = await db.ProductCategories.CountAsync();
        Assert.True(count > 0, "Expected categories to be seeded.");
        // Taxonomy has 8 parents + 32 children = 40 total
        Assert.Equal(CategoryTaxonomy.All.Count, count);
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent_DoesNotDuplicateOnSecondCall()
    {
        await using var db = CreateDb();
        await CategorySeeder.SeedAsync(db);
        await CategorySeeder.SeedAsync(db);
        var count = await db.ProductCategories.CountAsync();
        Assert.Equal(CategoryTaxonomy.All.Count, count);
    }

    [Fact]
    public async Task SeedAsync_HasCorrectParentChildRelationships()
    {
        await using var db = CreateDb();
        await CategorySeeder.SeedAsync(db);

        var leite = await db.ProductCategories.FirstOrDefaultAsync(c => c.Slug == "leite");
        Assert.NotNull(leite);
        Assert.NotNull(leite.ParentCategoryId);

        var parent = await db.ProductCategories.FindAsync(leite.ParentCategoryId);
        Assert.NotNull(parent);
        Assert.Equal("laticinios", parent.Slug);
    }

    [Fact]
    public async Task SeedAsync_ParentsHaveNoParent()
    {
        await using var db = CreateDb();
        await CategorySeeder.SeedAsync(db);

        var parents = CategoryTaxonomy.All.Where(d => d.ParentSlug is null).Select(d => d.Slug).ToHashSet();
        var parentCategories = await db.ProductCategories
            .Where(c => c.ParentCategoryId == null)
            .Select(c => c.Slug)
            .ToListAsync();

        Assert.Equal(parents.Count, parentCategories.Count);
        Assert.All(parentCategories, slug => Assert.Contains(slug, parents));
    }

    [Fact]
    public async Task SeedAsync_AllSlugsUnique()
    {
        await using var db = CreateDb();
        await CategorySeeder.SeedAsync(db);

        var slugs = await db.ProductCategories.Select(c => c.Slug).ToListAsync();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    [Fact]
    public async Task SeedAsync_AllTaxonomySlugsPresent()
    {
        await using var db = CreateDb();
        await CategorySeeder.SeedAsync(db);

        var dbSlugs = (await db.ProductCategories.Select(c => c.Slug).ToListAsync()).ToHashSet();
        foreach (var def in CategoryTaxonomy.All)
            Assert.Contains(def.Slug, dbSlugs);
    }
}
