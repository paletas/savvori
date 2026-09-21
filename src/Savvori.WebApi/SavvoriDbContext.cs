using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi;

public class SavvoriDbContext : DbContext
{
    public SavvoriDbContext(DbContextOptions<SavvoriDbContext> options) : base(options) { }

    public DbSet<ShoppingList> ShoppingLists { get; set; } = default!;
    public DbSet<ShoppingListItem> ShoppingListItems { get; set; } = default!;
    public DbSet<Product> Products { get; set; } = default!;
    public DbSet<Store> Stores { get; set; } = default!;
    public DbSet<StoreChain> StoreChains { get; set; } = default!;
    public DbSet<ProductCategory> ProductCategories { get; set; } = default!;
    public DbSet<ScrapingJob> ScrapingJobs { get; set; } = default!;
    public DbSet<ScrapingLog> ScrapingLogs { get; set; } = default!;
    public DbSet<StoreCategory> StoreCategories { get; set; } = default!;
    public DbSet<StoreCategoryMapping> StoreCategoryMappings { get; set; } = default!;
    public DbSet<StoreProduct> StoreProducts { get; set; } = default!;
    public DbSet<StoreProductPrice> StoreProductPrices { get; set; } = default!;
    public DbSet<ModelJob> ModelJobs { get; set; } = default!;
    public DbSet<StoreProductEmbedding> StoreProductEmbeddings { get; set; } = default!;
    public DbSet<MatchCandidate> MatchCandidates { get; set; } = default!;
    public DbSet<MatchMerge> MatchMerges { get; set; } = default!;
    public DbSet<CategorySuggestion> CategorySuggestions { get; set; } = default!;
    public DbSet<ProductTag> ProductTags { get; set; } = default!;
    public DbSet<BulkBatch> BulkBatches { get; set; } = default!;
    public DbSet<ProductCategoryTranslation> ProductCategoryTranslations { get; set; } = default!;
    public DbSet<TaxonomyMigration> TaxonomyMigrations { get; set; } = default!;
    public DbSet<CategoryStringDecision> CategoryStringDecisions { get; set; } = default!;

    // SQLite has no native decimal type: EF stores it as TEXT, which breaks ORDER BY / MIN / SUM
    // (prices would sort lexically, or the query fails to translate). Store prices as REAL instead.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HaveConversion<double>();
        configurationBuilder.Properties<decimal?>().HaveConversion<double>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ProductCategory: self-referencing hierarchy
        modelBuilder.Entity<ProductCategory>()
            .HasOne(pc => pc.Parent)
            .WithMany(pc => pc.Children)
            .HasForeignKey(pc => pc.ParentCategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // ShoppingListItem → Product
        modelBuilder.Entity<ShoppingListItem>()
            .HasOne(i => i.Product)
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // Product → ProductCategory
        modelBuilder.Entity<Product>()
            .HasOne(p => p.ProductCategory)
            .WithMany(pc => pc.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Store → StoreChain
        modelBuilder.Entity<Store>()
            .HasOne(s => s.StoreChain)
            .WithMany(sc => sc.Locations)
            .HasForeignKey(s => s.StoreChainId)
            .OnDelete(DeleteBehavior.SetNull);

        // ScrapingJob → StoreChain
        modelBuilder.Entity<ScrapingJob>()
            .HasOne(j => j.StoreChain)
            .WithMany(sc => sc.ScrapingJobs)
            .HasForeignKey(j => j.StoreChainId)
            .OnDelete(DeleteBehavior.Cascade);

        // ScrapingLog → ScrapingJob
        modelBuilder.Entity<ScrapingLog>()
            .HasOne(l => l.ScrapingJob)
            .WithMany(j => j.Logs)
            .HasForeignKey(l => l.ScrapingJobId)
            .OnDelete(DeleteBehavior.Cascade);

        // StoreCategory: self-referencing hierarchy
        modelBuilder.Entity<StoreCategory>()
            .HasOne(sc => sc.Parent)
            .WithMany(sc => sc.Children)
            .HasForeignKey(sc => sc.ParentStoreCategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // StoreCategory → StoreChain
        modelBuilder.Entity<StoreCategory>()
            .HasOne(sc => sc.StoreChain)
            .WithMany(scc => scc.StoreCategories)
            .HasForeignKey(sc => sc.StoreChainId)
            .OnDelete(DeleteBehavior.Cascade);

        // StoreCategoryMapping → StoreCategory (1:1)
        modelBuilder.Entity<StoreCategoryMapping>()
            .HasOne(scm => scm.StoreCategory)
            .WithOne(sc => sc.Mapping)
            .HasForeignKey<StoreCategoryMapping>(scm => scm.StoreCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // StoreCategoryMapping → ProductCategory
        modelBuilder.Entity<StoreCategoryMapping>()
            .HasOne(scm => scm.ProductCategory)
            .WithMany()
            .HasForeignKey(scm => scm.ProductCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // StoreProduct → StoreChain
        modelBuilder.Entity<StoreProduct>()
            .HasOne(sp => sp.StoreChain)
            .WithMany(sc => sc.StoreProducts)
            .HasForeignKey(sp => sp.StoreChainId)
            .OnDelete(DeleteBehavior.Cascade);

        // StoreProduct → StoreCategory (nullable)
        modelBuilder.Entity<StoreProduct>()
            .HasOne(sp => sp.StoreCategory)
            .WithMany(sc => sc.StoreProducts)
            .HasForeignKey(sp => sp.StoreCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // StoreProduct → Product (canonical, nullable)
        modelBuilder.Entity<StoreProduct>()
            .HasOne(sp => sp.CanonicalProduct)
            .WithMany(p => p.StoreProducts)
            .HasForeignKey(sp => sp.CanonicalProductId)
            .OnDelete(DeleteBehavior.SetNull);

        // StoreProductPrice → StoreProduct
        modelBuilder.Entity<StoreProductPrice>()
            .HasOne(spp => spp.StoreProduct)
            .WithMany(sp => sp.Prices)
            .HasForeignKey(spp => spp.StoreProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Default value for Currency
        modelBuilder.Entity<StoreProductPrice>()
            .Property(spp => spp.Currency)
            .HasDefaultValue("EUR");

        // Unique indexes
        modelBuilder.Entity<StoreChain>().HasIndex(sc => sc.Slug).IsUnique();
        modelBuilder.Entity<ProductCategory>().HasIndex(pc => pc.Slug).IsUnique();
        modelBuilder.Entity<StoreCategory>()
            .HasIndex(sc => new { sc.StoreChainId, sc.ExternalId }).IsUnique();
        modelBuilder.Entity<StoreProduct>()
            .HasIndex(sp => new { sp.StoreChainId, sp.ExternalId }).IsUnique();
        modelBuilder.Entity<StoreCategoryMapping>()
            .HasIndex(scm => scm.StoreCategoryId).IsUnique();

        // Performance indexes
        modelBuilder.Entity<Product>().HasIndex(p => p.EAN);
        modelBuilder.Entity<Product>().HasIndex(p => p.NormalizedName);
        modelBuilder.Entity<StoreProduct>()
            .HasIndex(sp => new { sp.CanonicalProductId, sp.IsActive });
        modelBuilder.Entity<StoreProduct>().HasIndex(sp => sp.NormalizedName);
        modelBuilder.Entity<StoreProduct>().HasIndex(sp => sp.EAN);
        modelBuilder.Entity<StoreProductPrice>()
            .HasIndex(spp => new { spp.StoreProductId, spp.ScrapedAt });
        modelBuilder.Entity<StoreProductPrice>()
            .HasIndex(spp => new { spp.StoreProductId, spp.IsLatest });
        // ModelJob: one active (Pending=0/Running=1) job per subject+input makes enqueueing idempotent
        modelBuilder.Entity<ModelJob>()
            .HasIndex(j => new { j.Type, j.SubjectId, j.PayloadHash })
            .HasDatabaseName("ix_model_jobs_active_unique")
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1)");
        modelBuilder.Entity<ModelJob>().HasIndex(j => new { j.Status, j.NextAttemptAt });
        // StoreProductEmbedding: one per StoreProduct, removed with it
        modelBuilder.Entity<StoreProductEmbedding>().HasKey(e => e.StoreProductId);
        modelBuilder.Entity<StoreProductEmbedding>()
            .HasOne(e => e.StoreProduct)
            .WithOne()
            .HasForeignKey<StoreProductEmbedding>(e => e.StoreProductId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StoreProductEmbedding>().HasIndex(e => e.EmbeddedAt);

        // MatchCandidate: one row per unordered pair (stored with A < B), removed with either product
        modelBuilder.Entity<MatchCandidate>()
            .HasOne(c => c.StoreProductA).WithMany().HasForeignKey(c => c.StoreProductAId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MatchCandidate>()
            .HasOne(c => c.StoreProductB).WithMany().HasForeignKey(c => c.StoreProductBId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MatchCandidate>().HasIndex(c => new { c.StoreProductAId, c.StoreProductBId }).IsUnique();
        modelBuilder.Entity<MatchCandidate>().HasIndex(c => c.StoreProductBId);
        modelBuilder.Entity<MatchCandidate>().HasIndex(c => c.Cosine);
        modelBuilder.Entity<MatchCandidate>().HasIndex(c => c.Status);
        modelBuilder.Entity<MatchMerge>().HasIndex(m => m.CandidateId);

        // Category display names per language (default language lives in ProductCategory.Name)
        modelBuilder.Entity<ProductCategoryTranslation>().HasKey(t => new { t.ProductCategoryId, t.Language });
        modelBuilder.Entity<ProductCategoryTranslation>()
            .HasOne(t => t.ProductCategory).WithMany().HasForeignKey(t => t.ProductCategoryId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BulkBatch>().HasIndex(b => new { b.Kind, b.CreatedAt });
        modelBuilder.Entity<MatchMerge>().HasIndex(m => m.BatchId);
        modelBuilder.Entity<CategorySuggestion>().HasIndex(s => s.BatchId);

        // Tags (bio, sem-lactose, ...) per canonical product; one row per (product, tag)
        modelBuilder.Entity<ProductTag>().HasKey(t => new { t.ProductId, t.Tag });
        modelBuilder.Entity<ProductTag>()
            .HasOne(t => t.Product).WithMany().HasForeignKey(t => t.ProductId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProductTag>().HasIndex(t => t.Tag);

        // Category suggestions: one live decision per product; cached decision per raw store-category string
        modelBuilder.Entity<CategorySuggestion>()
            .HasOne(s => s.Product).WithMany().HasForeignKey(s => s.ProductId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CategorySuggestion>()
            .HasOne(s => s.SuggestedCategory).WithMany().HasForeignKey(s => s.SuggestedCategoryId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CategorySuggestion>().HasIndex(s => s.ProductId).IsUnique();
        modelBuilder.Entity<CategorySuggestion>().HasIndex(s => s.Status);
        modelBuilder.Entity<CategoryStringDecision>()
            .HasOne(d => d.Category).WithMany().HasForeignKey(d => d.CategoryId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CategoryStringDecision>().HasIndex(d => d.RawString).IsUnique();

        // Only one IsLatest row per StoreProduct (partial unique index)
        modelBuilder.Entity<StoreProductPrice>()
            .HasIndex(spp => spp.StoreProductId)
            .HasDatabaseName("ix_store_product_prices_latest")
            .IsUnique()
            .HasFilter("\"IsLatest\" = 1");
    }
}
