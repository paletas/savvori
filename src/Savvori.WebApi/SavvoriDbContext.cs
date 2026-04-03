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
    public DbSet<ProductPrice> ProductPrices { get; set; } = default!;
    public DbSet<StoreChain> StoreChains { get; set; } = default!;
    public DbSet<ProductCategory> ProductCategories { get; set; } = default!;
    public DbSet<ProductStoreLink> ProductStoreLinks { get; set; } = default!;
    public DbSet<ScrapingJob> ScrapingJobs { get; set; } = default!;
    public DbSet<ScrapingLog> ScrapingLogs { get; set; } = default!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ProductCategory: self-referencing hierarchy
        modelBuilder.Entity<ProductCategory>()
            .HasOne(pc => pc.Parent)
            .WithMany(pc => pc.Children)
            .HasForeignKey(pc => pc.ParentCategoryId)
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

        // ProductPrice → Product
        modelBuilder.Entity<ProductPrice>()
            .HasOne(pp => pp.Product)
            .WithMany(p => p.Prices)
            .HasForeignKey(pp => pp.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // ProductPrice → Store
        modelBuilder.Entity<ProductPrice>()
            .HasOne(pp => pp.Store)
            .WithMany(s => s.Prices)
            .HasForeignKey(pp => pp.StoreId)
            .OnDelete(DeleteBehavior.Cascade);

        // ProductStoreLink → Product
        modelBuilder.Entity<ProductStoreLink>()
            .HasOne(psl => psl.Product)
            .WithMany(p => p.StoreLinks)
            .HasForeignKey(psl => psl.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // ProductStoreLink → StoreChain
        modelBuilder.Entity<ProductStoreLink>()
            .HasOne(psl => psl.StoreChain)
            .WithMany()
            .HasForeignKey(psl => psl.StoreChainId)
            .OnDelete(DeleteBehavior.Cascade);

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

        // Default value for Currency
        modelBuilder.Entity<ProductPrice>()
            .Property(pp => pp.Currency)
            .HasDefaultValue("EUR");

        // Unique indexes
        modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<StoreChain>().HasIndex(sc => sc.Slug).IsUnique();
        modelBuilder.Entity<ProductCategory>().HasIndex(pc => pc.Slug).IsUnique();
        modelBuilder.Entity<ProductStoreLink>()
            .HasIndex(psl => new { psl.StoreChainId, psl.ExternalId }).IsUnique();

        // Performance indexes
        modelBuilder.Entity<Product>().HasIndex(p => p.EAN);
        modelBuilder.Entity<Product>().HasIndex(p => p.NormalizedName);
        modelBuilder.Entity<ProductPrice>()
            .HasIndex(pp => new { pp.ProductId, pp.StoreId, pp.IsLatest });
    }
}
