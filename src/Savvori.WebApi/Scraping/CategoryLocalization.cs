using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Display names of the v1 categories in English. Slugs never change (they are identifiers); the default
/// <see cref="ProductCategory.Name"/> stays Portuguese (pt-PT) and other languages live in translation rows.
/// </summary>
public static class CategoryTranslations
{
    public const string DefaultLanguage = "pt";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string> { "pt", "en" };

    public static IReadOnlyDictionary<string, string> V1English { get; } = new Dictionary<string, string>
    {
        ["laticinios"] = "Dairy", ["frescos"] = "Fresh", ["mercearia"] = "Pantry", ["padaria-pastelaria"] = "Bakery & Pastry",
        ["bebidas"] = "Drinks", ["congelados"] = "Frozen", ["higiene-beleza"] = "Personal Care & Beauty", ["limpeza"] = "Cleaning",
        ["leite"] = "Milk", ["iogurtes"] = "Yoghurt", ["queijos"] = "Cheese", ["manteiga-margarinas"] = "Butter & Margarine",
        ["natas-cremes"] = "Cream", ["frutas"] = "Fruit", ["legumes"] = "Vegetables", ["carne"] = "Meat",
        ["peixe-marisco"] = "Fish & Seafood", ["ovos"] = "Eggs", ["charcutaria"] = "Deli Meats", ["arroz"] = "Rice",
        ["massas"] = "Pasta", ["conservas"] = "Canned Goods", ["molhos-temperos"] = "Sauces & Seasonings",
        ["azeite-oleos"] = "Olive Oil & Oils", ["cereais"] = "Cereals & Granola", ["bolachas"] = "Biscuits", ["pao"] = "Bread",
        ["bolos-sobremesas"] = "Cakes & Desserts", ["agua"] = "Water", ["sumos"] = "Juices & Nectars",
        ["bebidas-alcoolicas"] = "Alcoholic Drinks", ["legumes-congelados"] = "Frozen Vegetables", ["peixe-congelado"] = "Frozen Fish",
        ["refeicoes-prontas"] = "Ready Meals", ["higiene-pessoal"] = "Personal Hygiene", ["higiene-oral"] = "Oral Care",
        ["detergentes"] = "Detergents", ["limpeza-lar"] = "Household Cleaning", ["bebe-puericultura"] = "Baby & Childcare",
        ["saude-bem-estar"] = "Health & Wellness",
    };

    /// <summary>slug -> English name for every category we know (v1 and v2).</summary>
    public static IReadOnlyDictionary<string, string> AllEnglish { get; } = BuildAll();

    private static Dictionary<string, string> BuildAll()
    {
        var all = new Dictionary<string, string>(V1English);
        foreach (var aisle in TaxonomyV2Data.Aisles)
        {
            all[aisle.Slug] = aisle.NameEn;
            foreach (var leaf in aisle.Leaves) all[leaf.Slug] = leaf.NameEn;
        }
        return all;
    }

    /// <summary>Adds any missing English translation rows for categories that exist. Idempotent; never overwrites.</summary>
    public static async Task<int> SeedAsync(SavvoriDbContext db, CancellationToken ct = default)
    {
        var categories = await db.ProductCategories.AsNoTracking().Select(c => new { c.Id, c.Slug }).ToListAsync(ct);
        var have = (await db.ProductCategoryTranslations.Where(t => t.Language == "en").Select(t => t.ProductCategoryId).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var c in categories)
            if (!have.Contains(c.Id) && AllEnglish.TryGetValue(c.Slug, out var name))
            {
                db.ProductCategoryTranslations.Add(new ProductCategoryTranslation { ProductCategoryId = c.Id, Language = "en", Name = name });
                added++;
            }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }
}

public interface ICategoryLocalizer
{
    /// <summary>The supported language for a request: ?lang=, else the first supported Accept-Language tag, else the default.</summary>
    string ResolveLanguage(HttpRequest request);

    /// <summary>Category id -> display name in the language (the default-language Name where there is no translation).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(string language, CancellationToken ct = default);
}

public sealed class CategoryLocalizer(SavvoriDbContext db, IMemoryCache cache) : ICategoryLocalizer
{
    public string ResolveLanguage(HttpRequest request)
    {
        if (request.Query["lang"].FirstOrDefault() is { Length: > 0 } explicitLang)
            return Normalize(explicitLang) ?? CategoryTranslations.DefaultLanguage;

        foreach (var part in (request.Headers.AcceptLanguage.ToString()).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = part.Split(';')[0];
            if (Normalize(tag) is { } lang) return lang;
        }
        return CategoryTranslations.DefaultLanguage;
    }

    private static string? Normalize(string tag)
    {
        var primary = tag.Split('-', '_')[0].Trim().ToLowerInvariant();
        return CategoryTranslations.Supported.Contains(primary) ? primary : null;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(string language, CancellationToken ct = default)
    {
        var key = $"category-names:{language}";
        if (cache.TryGetValue(key, out IReadOnlyDictionary<Guid, string>? cached) && cached is not null) return cached;

        var names = await db.ProductCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        if (language != CategoryTranslations.DefaultLanguage)
            foreach (var t in await db.ProductCategoryTranslations.AsNoTracking().Where(t => t.Language == language).ToListAsync(ct))
                names[t.ProductCategoryId] = t.Name;

        cache.Set(key, (IReadOnlyDictionary<Guid, string>)names, TimeSpan.FromMinutes(2));
        return names;
    }
}
