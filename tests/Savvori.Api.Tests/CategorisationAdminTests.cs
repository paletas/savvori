using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;

namespace Savvori.Api.Tests;

/// <summary>Category suggestion review against real SQLite (translated queries, real constraints).</summary>
public class CategorisationAdminTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly HttpClient _client;

    public CategorisationAdminTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private (Guid Suggestion, Guid Product, Guid Category) Seed(CategorySuggestionStatus status = CategorySuggestionStatus.Suggested, string raw = "alimentacao")
    {
        var category = Guid.NewGuid();
        var product = Guid.NewGuid();
        var suggestion = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            db.ProductCategories.Add(new ProductCategory { Id = category, Name = $"Leite {category:N}", Slug = $"leite-{category:N}" });
            db.Products.Add(new Product { Id = product, Name = "Leite Meio Gordo", Brand = "Mimosa", Category = raw });
            db.SaveChanges();
            db.CategorySuggestions.Add(new CategorySuggestion
            {
                Id = suggestion, ProductId = product, SuggestedCategoryId = category, Confidence = 0.72, NeighbourCount = 7,
                Status = status, Method = "embedding-knn", ModelName = "bge-m3", ModelDigest = "d", CreatedAt = DateTime.UtcNow
            });
        });
        return (suggestion, product, category);
    }

    private Task<HttpResponseMessage> Post(string url) => _client.PostAsync(url, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Review_ListsSuggestionsWithProductCategoryAndConfidence()
    {
        var s = Seed();

        var r = await _client.GetAsync("/api/admin/categorisation/review?pageSize=100", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await r.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var item = json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == s.Suggestion);
        Assert.Equal("Leite Meio Gordo", item.GetProperty("productName").GetString());
        Assert.Equal(0.72, item.GetProperty("confidence").GetDouble(), 2);
        Assert.StartsWith("Leite", item.GetProperty("suggested").GetString());
    }

    [Fact]
    public async Task Accept_SetsTheCategory_ThenUndoRemovesItAndRejects()
    {
        var s = Seed();

        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/admin/categorisation/suggestions/{s.Suggestion}/accept")).StatusCode);
        _factory.SeedData(db => Assert.Equal(s.Category, db.Products.AsNoTracking().Single(p => p.Id == s.Product).CategoryId));

        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/admin/categorisation/suggestions/{s.Suggestion}/undo")).StatusCode);
        _factory.SeedData(db =>
        {
            Assert.Null(db.Products.AsNoTracking().Single(p => p.Id == s.Product).CategoryId);
            Assert.Equal(CategorySuggestionStatus.Rejected, db.CategorySuggestions.AsNoTracking().Single(x => x.Id == s.Suggestion).Status);
        });
    }

    [Fact]
    public async Task AcceptedSuggestions_StayListedUnderApplied_SoTheyCanBeUndone()
    {
        var s = Seed();
        await Post($"/api/admin/categorisation/suggestions/{s.Suggestion}/accept");

        var pending = await _client.GetFromJsonAsync<JsonElement>("/api/admin/categorisation/review?filter=suggested&pageSize=100", TestContext.Current.CancellationToken);
        var applied = await _client.GetFromJsonAsync<JsonElement>("/api/admin/categorisation/review?filter=applied&pageSize=100", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(pending.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == s.Suggestion);
        Assert.Contains(applied.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == s.Suggestion);
    }

    [Fact]
    public async Task Reject_IsStored_AndAnAppliedSuggestionCannotBeRejectedWithoutUndo()
    {
        var pending = Seed();
        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/admin/categorisation/suggestions/{pending.Suggestion}/reject")).StatusCode);
        _factory.SeedData(db => Assert.Equal(CategorySuggestionStatus.Rejected,
            db.CategorySuggestions.AsNoTracking().Single(x => x.Id == pending.Suggestion).Status));

        var applied = Seed();
        await Post($"/api/admin/categorisation/suggestions/{applied.Suggestion}/accept");
        Assert.Equal(HttpStatusCode.Conflict, (await Post($"/api/admin/categorisation/suggestions/{applied.Suggestion}/reject")).StatusCode);
    }

    [Fact]
    public async Task StringProposal_AcceptCategorisesEveryUncategorisedProductWithThatString()
    {
        var category = Guid.NewGuid();
        var decision = Guid.NewGuid();
        var products = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var already = Guid.NewGuid();
        var other = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            db.ProductCategories.Add(new ProductCategory { Id = category, Name = $"Bolachas {category:N}", Slug = $"b-{category:N}" });
            db.ProductCategories.Add(new ProductCategory { Id = other, Name = $"Outra {other:N}", Slug = $"o-{other:N}" });
            db.SaveChanges();
            foreach (var p in products) db.Products.Add(new Product { Id = p, Name = "Bolacha", Category = "Bolachas & Biscoitos" });
            db.Products.Add(new Product { Id = already, Name = "Bolacha manual", Category = "Bolachas & Biscoitos", CategoryId = other });
            db.CategoryStringDecisions.Add(new CategoryStringDecision
            {
                Id = decision, RawString = "bolachas biscoitos", CategoryId = category, Confidence = 0.9, Support = 3,
                Status = CategorySuggestionStatus.Suggested, Method = "embedding-knn", ModelName = "bge-m3", ModelDigest = "d",
                CreatedAt = DateTime.UtcNow
            });
        });

        var listed = await _client.GetFromJsonAsync<JsonElement>("/api/admin/categorisation/strings", TestContext.Current.CancellationToken);
        Assert.Contains(listed.EnumerateArray(), x => x.GetProperty("rawString").GetString() == "bolachas biscoitos");

        var r = await Post($"/api/admin/categorisation/strings/{decision}/accept");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(3, (await r.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("updated").GetInt32());
        _factory.SeedData(db =>
        {
            Assert.All(products, p => Assert.Equal(category, db.Products.AsNoTracking().Single(x => x.Id == p).CategoryId));
            Assert.Equal(other, db.Products.AsNoTracking().Single(x => x.Id == already).CategoryId); // untouched
        });
    }

    [Fact]
    public async Task Run_WithTheFeatureFlagOff_ExplainsWhy_AndTheSummaryHasNoDryRunFlag()
    {
        var run = await Post("/api/admin/categorisation/run");
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.False(string.IsNullOrEmpty((await run.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("skippedReason").GetString()));

        var summary = await _client.GetFromJsonAsync<JsonElement>("/api/admin/categorisation/summary", TestContext.Current.CancellationToken);
        Assert.False(summary.TryGetProperty("dryRun", out _));
    }

    [Fact]
    public async Task Unknown_Suggestion_Returns409WithAMessage()
    {
        var r = await Post($"/api/admin/categorisation/suggestions/{Guid.NewGuid()}/accept");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }
}
