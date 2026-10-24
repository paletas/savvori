using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class BulkThrottleTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Apply_HoldsBack_WhenTheCategoryJudgeIsThrottled_AndLeavesTheBreakerAlone()
    {
        var cat = Guid.NewGuid();
        var product = Guid.NewGuid();
        var suggestion = Guid.NewGuid();
        _h.With(db =>
        {
            db.ProductCategories.Add(new ProductCategory { Id = cat, Name = "Leite", Slug = "leite" });
            db.Products.Add(new Product { Id = product, Name = "Leite" });
            db.CategorySuggestions.Add(new CategorySuggestion
            {
                Id = suggestion, ProductId = product, SuggestedCategoryId = cat, Confidence = 0.95, Status = CategorySuggestionStatus.Suggested,
                Method = "embedding-knn", ModelName = "m", ModelDigest = "d", CreatedAt = DateTime.UtcNow
            });
        });
        _h.Faults.GoDown(FaultMode.Throttled);

        using var scope = _h.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CategoryBulkService>();
        var created = await svc.CreateAsync(0.90, TestContext.Current.CancellationToken);
        await svc.RunApplyAsync(created.Id, TestContext.Current.CancellationToken);

        var batch = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>().BulkBatches.AsNoTracking().Single(b => b.Id == created.Id);
        Assert.Equal(0, batch.Applied);
        Assert.Equal(1, batch.Blocked);
        var held = _h.Query(db => db.CategorySuggestions.AsNoTracking().Single(s => s.Id == suggestion));
        Assert.Equal("Held back: category judge was unavailable.", held.Note);
        Assert.True(_h.Breaker.IsClosed);
        Assert.Equal(0, _h.Breaker.Snapshot().ConsecutiveFailures);
    }
}
