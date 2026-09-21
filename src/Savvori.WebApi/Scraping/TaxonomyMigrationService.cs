using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>Deterministic tag rules (never model-based): name, brand and raw store category are searched.</summary>
public static class TagRules
{
    public const string Bio = "bio";
    public const string SemLactose = "sem-lactose";
    public const string SemGluten = "sem-gluten";
    public const string Vegan = "vegan";
    public const string SemAcucar = "sem-acucar";

    public static readonly IReadOnlyList<string> All = [Bio, SemLactose, SemGluten, Vegan, SemAcucar];

    private static readonly (string Tag, Regex Pattern)[] Rules =
    [
        (Bio, Make("bio", "biologico", "biologica", "biologicos", "biologicas", "organico", "organica", "organicos", "organicas", "organic")),
        (SemLactose, Make("sem lactose", "lactose free", "zero lactose", "s lactose", "0 lactose")),
        (SemGluten, Make("sem gluten", "gluten free", "s gluten", "isento de gluten")),
        (Vegan, Make("vegan", "vegano", "vegana")),
        (SemAcucar, Make("sem acucar", "sem acucares", "zero acucar", "sugar free", "sem adicao de acucares", "s acucar", "0 acucar")),
    ];

    private static Regex Make(params string[] keywords) =>
        new(@"\b(" + string.Join("|", keywords.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<string> Compute(string? name, string? brand, string? rawCategory)
    {
        var text = ProductNormalizer.Normalize($"{name} {brand} {rawCategory}");
        return Rules.Where(r => r.Pattern.IsMatch(text)).Select(r => r.Tag).ToList();
    }
}

public sealed record PlanRow(
    string LegacySlug, string LegacyName, int Products, int OneToOne, int ByRule, int LeftForClassifier,
    IReadOnlyDictionary<string, int> RuleTargets);

public sealed record TaxonomyPlan(
    bool V2Active, int ProductsWithCategory, int ProductsAlreadyMigrated, int Unchanged, int MovedToUncategorised,
    IReadOnlyList<PlanRow> Rows, IReadOnlyDictionary<string, int> Tags,
    IReadOnlyDictionary<string, int>? SeedTargets = null);

public sealed record TaxonomyApplyResult(bool Applied, string? Reason, TaxonomyPlan Plan);

/// <summary>
/// Plans, applies and reverts the taxonomy v1 -> v2 label migration. Reversible: every changed product keeps its v1
/// category in <see cref="Product.LegacyCategoryId"/> and is marked with <see cref="Product.CategorySource"/>.
/// A dry-run plan (<see cref="PlanAsync"/>) changes nothing.
/// </summary>
public sealed class TaxonomyMigrationService(SavvoriDbContext db, TimeProvider time)
{
    public const string SourceOneToOne = "taxonomy-1to1";
    public const string SourceRule = "taxonomy-rule";
    public const string SourceLeft = "taxonomy-left";
    public const string SourceSeed = "taxonomy-seed";

    public async Task<bool> IsV2ActiveAsync(CancellationToken ct = default) =>
        await db.TaxonomyMigrations.Where(m => m.Version == 2).OrderByDescending(m => m.AppliedAt)
            .Select(m => m.RevertedAt == null).FirstOrDefaultAsync(ct);

    /// <summary>Counts what an apply would do, per v1 category. Reads only.</summary>
    public async Task<TaxonomyPlan> PlanAsync(CancellationToken ct = default)
    {
        var idToSlug = await db.ProductCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Slug, ct);
        var products = await db.Products.AsNoTracking().Where(p => p.CategoryId != null)
            .Select(p => new { p.Name, p.CategoryId, p.LegacyCategoryId }).ToListAsync(ct);

        var rows = new Dictionary<string, (int total, int one, int rule, int left, Dictionary<string, int> targets)>();
        int already = 0, unchanged = 0, toNull = 0;
        var seeds = new Dictionary<string, int>();
        foreach (var p in products)
        {
            if (p.LegacyCategoryId is not null) { already++; continue; }
            var slug = idToSlug[p.CategoryId!.Value];
            var (known, targetSlug, kind) = Classify(slug, p.Name);
            if (!known) { unchanged++; continue; }

            if (!rows.TryGetValue(slug, out var row)) row = (0, 0, 0, 0, []);
            row.total++;
            if (kind == PlacementKind.OneToOne) row.one++;
            else if (kind == PlacementKind.Rule) { row.rule++; row.targets[targetSlug!] = row.targets.GetValueOrDefault(targetSlug!) + 1; }
            else
            {
                row.left++; toNull++;
                if (TaxonomyV2.Seed(p.Name) is { } leftSeed) seeds[leftSeed] = seeds.GetValueOrDefault(leftSeed) + 1;
            }
            rows[slug] = row;
        }

        var names = CategoryTaxonomy.All.ToDictionary(d => d.Slug, d => d.Name);
        var planRows = rows.OrderBy(r => r.Key).Select(r => new PlanRow(
            r.Key, names.GetValueOrDefault(r.Key, r.Key), r.Value.total, r.Value.one, r.Value.rule, r.Value.left, r.Value.targets)).ToList();

        var tagCounts = TagRules.All.ToDictionary(t => t, _ => 0);
        foreach (var p in await db.Products.AsNoTracking().Select(p => new { p.Name, p.Brand, p.Category }).ToListAsync(ct))
            foreach (var t in TagRules.Compute(p.Name, p.Brand, p.Category)) tagCounts[t]++;

        // Products with no category at all (for example a whole chain that was never categorised).
        foreach (var name in await db.Products.AsNoTracking().Where(p => p.CategoryId == null && p.LegacyCategoryId == null && p.CategorySource == null)
                     .Select(p => p.Name).ToListAsync(ct))
            if (TaxonomyV2.Seed(name) is { } seed) seeds[seed] = seeds.GetValueOrDefault(seed) + 1;

        return new TaxonomyPlan(await IsV2ActiveAsync(ct), products.Count, already, unchanged, toNull, planRows, tagCounts, seeds);
    }

    /// <summary>Where a product in v1 category <paramref name="slug"/> goes. Known=false: not a v1 category, leave it alone.</summary>
    private static (bool Known, string? TargetSlug, PlacementKind Kind) Classify(string slug, string productName)
    {
        if (TaxonomyV2.Legacy.ContainsKey(slug))
        {
            var placement = TaxonomyV2.Place(slug, productName)!;
            return (true, placement.TargetSlug, placement.Kind);
        }
        // A v1 parent group (for example the broad Mercearia): not an assignable category in v2.
        if (TaxonomyV2.AisleSlugs.Contains(slug) || CategoryTaxonomy.All.Any(d => d.Slug == slug && d.ParentSlug is null))
            return (true, null, PlacementKind.LeftForClassifier);
        return (false, null, PlacementKind.LeftForClassifier);
    }

    /// <summary>Applies the migration: seeds v2 categories, relabels products (keeping v1 ids), sets tags. Refuses if v2 is already active.</summary>
    public async Task<TaxonomyApplyResult> ApplyAsync(CancellationToken ct = default)
    {
        var plan = await PlanAsync(ct);
        if (plan.V2Active) return new(false, "Taxonomy v2 is already applied.", plan);

        await SeedV2CategoriesAsync(ct);

        var idToSlug = await db.ProductCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Slug, ct);
        var slugToId = await db.ProductCategories.AsNoTracking().ToDictionaryAsync(c => c.Slug, c => c.Id, ct);
        var products = await db.Products.Where(p => p.CategoryId != null && p.LegacyCategoryId == null).ToListAsync(ct);

        int oneToOne = 0, byRule = 0, left = 0;
        foreach (var p in products)
        {
            var oldId = p.CategoryId!.Value;
            var (known, targetSlug, kind) = Classify(idToSlug[oldId], p.Name);
            if (!known) continue;

            p.LegacyCategoryId = oldId;
            p.CategoryId = targetSlug is null ? null : slugToId[targetSlug];
            p.CategorySource = kind switch
            {
                PlacementKind.OneToOne => SourceOneToOne,
                PlacementKind.Rule => SourceRule,
                _ => SourceLeft
            };
            if (kind == PlacementKind.OneToOne) oneToOne++; else if (kind == PlacementKind.Rule) byRule++; else left++;
        }

        await db.SaveChangesAsync(ct); // the relabelling above must be visible to the query below

        // Seed the categories that are new in v2 from product names, for products that still have no category.
        int seeded = 0;
        foreach (var p in await db.Products.Where(p => p.CategoryId == null && (p.CategorySource == null || p.CategorySource == SourceLeft)).ToListAsync(ct))
            if (TaxonomyV2.Seed(p.Name) is { } seed)
            {
                p.CategoryId = slugToId[seed];
                p.CategorySource = SourceSeed;
                seeded++;
            }

        var tagged = await BackfillTagsAsync(ct);
        db.TaxonomyMigrations.Add(new TaxonomyMigration
        {
            Id = Guid.NewGuid(), Version = 2, AppliedAt = time.GetUtcNow().UtcDateTime,
            Summary = JsonSerializer.Serialize(new { oneToOne, byRule, leftForClassifier = left, seeded, tagsAdded = tagged })
        });
        await db.SaveChangesAsync(ct);
        return new(true, null, plan with { V2Active = true });
    }

    /// <summary>Creates the v2 aisles and categories. Slugs shared with v1 reuse the existing row (renamed and re-parented).</summary>
    private async Task SeedV2CategoriesAsync(CancellationToken ct)
    {
        var existing = await db.ProductCategories.ToDictionaryAsync(c => c.Slug, ct);
        foreach (var aisle in TaxonomyV2Data.Aisles)
        {
            var aisleRow = Upsert(existing, aisle.Slug, aisle.Name, null);
            foreach (var leaf in aisle.Leaves)
                Upsert(existing, leaf.Slug, leaf.Name, aisleRow);
        }
        await db.SaveChangesAsync(ct);
    }

    private ProductCategory Upsert(Dictionary<string, ProductCategory> existing, string slug, string name, ProductCategory? parent)
    {
        if (!existing.TryGetValue(slug, out var row))
        {
            row = new ProductCategory { Id = Guid.NewGuid(), Slug = slug };
            db.ProductCategories.Add(row);
            existing[slug] = row;
        }
        row.Name = name;
        row.Parent = parent;
        row.ParentCategoryId = parent?.Id;
        return row;
    }

    /// <summary>Adds any missing tags computed by the deterministic rules. Idempotent; never removes a tag.</summary>
    public async Task<int> BackfillTagsAsync(CancellationToken ct = default)
    {
        var existing = (await db.ProductTags.Select(t => new { t.ProductId, t.Tag }).ToListAsync(ct))
            .Select(t => (t.ProductId, t.Tag)).ToHashSet();
        var added = 0;
        foreach (var p in await db.Products.AsNoTracking().Select(p => new { p.Id, p.Name, p.Brand, p.Category }).ToListAsync(ct))
            foreach (var tag in TagRules.Compute(p.Name, p.Brand, p.Category))
                if (!existing.Contains((p.Id, tag)))
                {
                    db.ProductTags.Add(new ProductTag { ProductId = p.Id, Tag = tag });
                    added++;
                }
        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// Restores every product still carrying a taxonomy source to its v1 category and returns the reused category rows
    /// to their v1 names and parents. Products you or the classifier categorised since are left alone.
    /// </summary>
    public async Task<(bool Reverted, string? Reason, int Restored)> RevertAsync(CancellationToken ct = default)
    {
        var current = await db.TaxonomyMigrations.Where(m => m.Version == 2 && m.RevertedAt == null)
            .OrderByDescending(m => m.AppliedAt).FirstOrDefaultAsync(ct);
        if (current is null) return (false, "Taxonomy v2 is not applied.", 0);

        var restored = 0;
        foreach (var p in await db.Products.Where(p => p.LegacyCategoryId != null || p.CategorySource != null).ToListAsync(ct))
        {
            if (p.CategorySource is { } s && s.StartsWith("taxonomy", StringComparison.Ordinal))
            {
                p.CategoryId = p.LegacyCategoryId;
                restored++;
            }
            p.LegacyCategoryId = null;
            p.CategorySource = null;
        }

        // Labels the classifier set on the v2 tree since the migration have no v1 equivalent: back to uncategorised.
        // A label you set by hand stays (a hand decision always wins), even though it points at a v2 category.
        var v2OnlyIds = (await db.ProductCategories.Where(c => !TaxonomyV2.V1Slugs.Contains(c.Slug)).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var byModel = (await db.CategorySuggestions
            .Where(s => s.Status == CategorySuggestionStatus.Applied && (s.Method == "embedding-knn" || s.Method == "string-cache"))
            .Select(s => s.ProductId).ToListAsync(ct)).ToHashSet();
        foreach (var p in await db.Products.Where(p => p.CategoryId != null).ToListAsync(ct))
            if (p.CategoryId is { } cid && v2OnlyIds.Contains(cid) && byModel.Contains(p.Id)) p.CategoryId = null;

        var rows = await db.ProductCategories.ToDictionaryAsync(c => c.Slug, ct);
        foreach (var def in CategoryTaxonomy.All)
        {
            if (!rows.TryGetValue(def.Slug, out var row)) continue;
            row.Name = def.Name;
            var parent = def.ParentSlug is null ? null : rows.GetValueOrDefault(def.ParentSlug);
            row.Parent = parent;
            row.ParentCategoryId = parent?.Id;
        }

        current.RevertedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return (true, null, restored);
    }
}
