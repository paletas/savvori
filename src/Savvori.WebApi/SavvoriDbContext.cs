using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi;

public class SavvoriDbContext : DbContext
{
    public SavvoriDbContext(DbContextOptions<SavvoriDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; } = default!;
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
        modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
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
        // IsLatest index — the unique partial index (WHERE IsLatest) is added via raw SQL in the migration
        modelBuilder.Entity<StoreProductPrice>()
            .HasIndex(spp => new { spp.StoreProductId, spp.IsLatest });
    }
}
