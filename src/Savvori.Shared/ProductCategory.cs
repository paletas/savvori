namespace Savvori.Shared;

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
