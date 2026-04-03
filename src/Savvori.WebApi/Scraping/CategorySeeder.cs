using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

public static class CategorySeeder
{
    public static async Task SeedAsync(
        SavvoriDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (await db.ProductCategories.AnyAsync(ct))
        {
            logger?.LogDebug("Categories already seeded — skipping.");
            return;
        }

        // Insert parents first so children can reference them
        var parents = CategoryTaxonomy.All.Where(d => d.ParentSlug is null).ToList();
        var children = CategoryTaxonomy.All.Where(d => d.ParentSlug is not null).ToList();

        var slugToId = new Dictionary<string, Guid>();

        foreach (var def in parents)
        {
            var id = Guid.NewGuid();
            slugToId[def.Slug] = id;
            db.ProductCategories.Add(new ProductCategory
            {
                Id = id,
                Name = def.Name,
                Slug = def.Slug
            });
        }

        // Save parents so their PKs are available for FK resolution
        await db.SaveChangesAsync(ct);

        foreach (var def in children)
        {
            var id = Guid.NewGuid();
            slugToId[def.Slug] = id;
            var parentId = def.ParentSlug is not null && slugToId.TryGetValue(def.ParentSlug, out var pid)
                ? pid
                : (Guid?)null;

            db.ProductCategories.Add(new ProductCategory
            {
                Id = id,
                Name = def.Name,
                Slug = def.Slug,
                ParentCategoryId = parentId
            });
        }

        await db.SaveChangesAsync(ct);

        var total = parents.Count + children.Count;
        logger?.LogInformation("Seeded {Count} product categories.", total);
    }
}
