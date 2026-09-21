using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;

namespace Savvori.Api.Tests;

/// <summary>Bulk review actions end to end on real SQLite, including the background run and the undo.</summary>
public class BulkAdminTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly HttpClient _client;

    public BulkAdminTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private (Guid A, Guid B, Guid CanonA, Guid CanonB, Guid Candidate) SeedPair(double cosine)
    {
        var chainA = Guid.NewGuid();
        var chainB = Guid.NewGuid();
        var canonA = Guid.NewGuid();
        var canonB = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var candidate = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            db.StoreChains.Add(new StoreChain { Id = chainA, Name = $"A-{chainA:N}", Slug = $"a-{chainA:N}", BaseUrl = "https://a.test", IsActive = true });
            db.StoreChains.Add(new StoreChain { Id = chainB, Name = $"B-{chainB:N}", Slug = $"b-{chainB:N}", BaseUrl = "https://b.test", IsActive = true });
            db.Products.Add(new Product { Id = canonA, Name = "Leite" });
            db.Products.Add(new Product { Id = canonB, Name = "Leite M." });
            db.SaveChanges();
            StoreProduct Make(Guid id, Guid chain, Guid canon, string name) => new()
            {
                Id = id, StoreChainId = chain, ExternalId = id.ToString("N"), Name = name, Brand = "Mimosa", Unit = ProductUnit.L,
                SizeValue = 1, CanonicalProductId = canon, IsActive = true, MatchStatus = MatchStatus.AutoMatched, MatchMethod = "created-new",
                FirstSeen = DateTime.UtcNow, LastScraped = DateTime.UtcNow
            };
            db.StoreProducts.Add(Make(a, chainA, canonA, "Leite Meio Gordo Mimosa 1L"));
            db.StoreProducts.Add(Make(b, chainB, canonB, "Leite Gordo Meio Mimosa 1L"));
            var (x, y) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
            db.MatchCandidates.Add(new MatchCandidate
            {
                Id = candidate, StoreProductAId = x, StoreProductBId = y, Cosine = cosine, SizeKnown = true, BrandCheck = CandidateBrandCheck.Ok,
                ModelName = "bge-m3", ModelDigest = "d", CreatedAt = DateTime.UtcNow, Status = CandidateStatus.NeedsReview,
                Suggestion = "embedding-cosine"
            });
        });
        return (a, b, canonA, canonB, candidate);
    }

    private async Task<JsonElement> WaitForBatchAsync(string area, Guid batchId, string status)
    {
        for (var i = 0; i < 100; i++)
        {
            var list = await _client.GetFromJsonAsync<JsonElement>($"/api/admin/{area}/bulk/batches", Ct);
            var batch = list.EnumerateArray().FirstOrDefault(b => b.GetProperty("id").GetGuid() == batchId);
            if (batch.ValueKind == JsonValueKind.Object && batch.GetProperty("status").GetString() == status) return batch;
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException($"batch never reached {status}");
    }

    [Fact]
    public async Task Matches_PreviewSamples_ApplyRunsInTheBackground_AndUndoRestoresEverything()
    {
        var p = SeedPair(0.97);
        var low = SeedPair(0.80);

        var preview = await _client.GetFromJsonAsync<JsonElement>("/api/admin/matching/bulk/preview?minCosine=0.9&sample=10", Ct);
        Assert.True(preview.GetProperty("eligible").GetInt32() >= 1);
        var sample = preview.GetProperty("sample").EnumerateArray().ToList();
        Assert.All(sample, s => Assert.True(s.GetProperty("cosine").GetDouble() >= 0.9));
        Assert.All(sample, s => Assert.False(string.IsNullOrEmpty(s.GetProperty("a").GetProperty("name").GetString())));

        var apply = await _client.PostAsync("/api/admin/matching/bulk/apply?minCosine=0.9", null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
        var batchId = (await apply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("batchId").GetGuid();
        var done = await WaitForBatchAsync("matching", batchId, "Done");
        Assert.True(done.GetProperty("applied").GetInt32() >= 1);

        _factory.SeedData(db =>
        {
            Assert.Equal(db.StoreProducts.AsNoTracking().Single(x => x.Id == p.A).CanonicalProductId,
                         db.StoreProducts.AsNoTracking().Single(x => x.Id == p.B).CanonicalProductId);
            Assert.Equal(CandidateStatus.NeedsReview, db.MatchCandidates.AsNoTracking().Single(c => c.Id == low.Candidate).Status);
            Assert.Equal(batchId, db.MatchMerges.AsNoTracking().Single(m => m.CandidateId == p.Candidate).BatchId);
        });

        var undo = await _client.PostAsync($"/api/admin/matching/bulk/batches/{batchId}/undo", null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, undo.StatusCode);
        await WaitForBatchAsync("matching", batchId, "Undone");
        _factory.SeedData(db =>
        {
            Assert.Equal(p.CanonA, db.StoreProducts.AsNoTracking().Single(x => x.Id == p.A).CanonicalProductId);
            Assert.Equal(p.CanonB, db.StoreProducts.AsNoTracking().Single(x => x.Id == p.B).CanonicalProductId);
            Assert.Equal(2, db.Products.Count(x => x.Id == p.CanonA || x.Id == p.CanonB));
            Assert.Equal(CandidateStatus.NeedsReview, db.MatchCandidates.AsNoTracking().Single(c => c.Id == p.Candidate).Status);
        });

        // a run can only be undone once
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/admin/matching/bulk/batches/{batchId}/undo", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Categories_ApplyAndUndo_EndToEnd()
    {
        var category = Guid.NewGuid();
        var product = Guid.NewGuid();
        var suggestion = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            db.ProductCategories.Add(new ProductCategory { Id = category, Name = $"Leite {category:N}", Slug = $"leite-{category:N}" });
            db.Products.Add(new Product { Id = product, Name = "Leite UHT Magro" });
            db.SaveChanges();
            db.CategorySuggestions.Add(new CategorySuggestion
            {
                Id = suggestion, ProductId = product, SuggestedCategoryId = category, Confidence = 0.96, NeighbourCount = 7,
                Status = CategorySuggestionStatus.Suggested, Method = "embedding-knn", ModelName = "bge-m3", ModelDigest = "d",
                CreatedAt = DateTime.UtcNow
            });
        });

        var preview = await _client.GetFromJsonAsync<JsonElement>("/api/admin/categorisation/bulk/preview?minConfidence=0.9", Ct);
        Assert.True(preview.GetProperty("eligible").GetInt32() >= 1);

        var apply = await _client.PostAsync("/api/admin/categorisation/bulk/apply?minConfidence=0.9", null, Ct);
        var batchId = (await apply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("batchId").GetGuid();
        await WaitForBatchAsync("categorisation", batchId, "Done");
        _factory.SeedData(db => Assert.Equal(category, db.Products.AsNoTracking().Single(p => p.Id == product).CategoryId));

        await _client.PostAsync($"/api/admin/categorisation/bulk/batches/{batchId}/undo", null, Ct);
        await WaitForBatchAsync("categorisation", batchId, "Undone");
        _factory.SeedData(db =>
        {
            Assert.Null(db.Products.AsNoTracking().Single(p => p.Id == product).CategoryId);
            Assert.Equal(CategorySuggestionStatus.Suggested, db.CategorySuggestions.AsNoTracking().Single(s => s.Id == suggestion).Status);
        });
    }

    [Fact]
    public async Task UnknownBatch_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/admin/matching/bulk/batches/{Guid.NewGuid()}/undo", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/admin/categorisation/bulk/batches/{Guid.NewGuid()}/undo", null, Ct)).StatusCode);
    }
}
