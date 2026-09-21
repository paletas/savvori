namespace Savvori.Shared;

/// <summary>A display name of a category in another language. The default (pt-PT) name is ProductCategory.Name.</summary>
public class ProductCategoryTranslation
{
    public Guid ProductCategoryId { get; set; }
    public ProductCategory ProductCategory { get; set; } = null!;
    /// <summary>Primary language subtag: "en" (the default "pt" is ProductCategory.Name).</summary>
    public string Language { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class ProductCategory
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public Guid? ParentCategoryId { get; set; }
    public ProductCategory? Parent { get; set; }
    public List<ProductCategory> Children { get; set; } = new();
    public List<Product> Products { get; set; } = new();
}
