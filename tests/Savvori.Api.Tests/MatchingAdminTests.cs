using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;
using Savvori.WebApi;

namespace Savvori.Api.Tests;

/// <summary>Runs the review queue against real SQLite so the EF queries are translated, not just simulated.</summary>
public class MatchingAdminTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly HttpClient _client;

    public MatchingAdminTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record Seed(Guid Candidate, Guid A, Guid B, Guid CanonA, Guid CanonB);

    private Seed SeedPair(bool sameChainClash = false, CandidateStatus status = CandidateStatus.NeedsReview)
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
            db.Products.Add(new Product { Id = canonA, Name = "Leite Meio Gordo" });
            db.Products.Add(new Product { Id = canonB, Name = "Leite M. Gordo" });
            db.SaveChanges();
            StoreProduct Make(Guid id, Guid chain, Guid canon, string name) => new()
            {
                Id = id, StoreChainId = chain, ExternalId = id.ToString("N"), Name = name, Brand = "Mimosa",
                Unit = ProductUnit.L, SizeValue = 1, CanonicalProductId = canon, IsActive = true,
                MatchStatus = MatchStatus.AutoMatched, MatchMethod = "created-new",
                FirstSeen = DateTime.UtcNow, LastScraped = DateTime.UtcNow,
                Prices = { new StoreProductPrice { Id = Guid.NewGuid(), Price = 0.99m, IsLatest = true, ScrapedAt = DateTime.UtcNow } }
            };
            db.StoreProducts.Add(Make(a, chainA, canonA, "Leite Meio Gordo Mimosa 1L"));
            db.StoreProducts.Add(Make(b, chainB, canonB, "Leite M. Gordo Mimosa 1L"));
            if (sameChainClash)
            {
                var extra = Guid.NewGuid();
                db.StoreProducts.Add(Make(extra, chainA, canonB, "Leite Mimosa 6x1L"));
            }
            var (x, y) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
            db.MatchCandidates.Add(new MatchCandidate
            {
                Id = candidate, StoreProductAId = x, StoreProductBId = y, Cosine = 0.93, SizeKnown = true,
                BrandCheck = CandidateBrandCheck.Ok, ModelName = "bge-m3", ModelDigest = "d", CreatedAt = DateTime.UtcNow,
                Status = status, Suggestion = status == CandidateStatus.NeedsReview ? "embedding-cosine" : null
            });
        });
        return new Seed(candidate, a, b, canonA, canonB);
    }

    private async Task<JsonElement> Json(HttpResponseMessage r) =>
        await r.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> Post(string url) => _client.PostAsync(url, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Review_ListsPairsSideBySide_WithPriceChainAndSuggestion()
    {
        var s = SeedPair();

        var r = await _client.GetAsync("/api/admin/matching/review?filter=suggested&pageSize=50", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = (await Json(r)).GetProperty("items").EnumerateArray().ToList();
        var item = items.Single(i => i.GetProperty("id").GetGuid() == s.Candidate);
        Assert.Equal("embedding-cosine", item.GetProperty("suggestion").GetString());
        Assert.Equal(0.99, item.GetProperty("a").GetProperty("price").GetDouble() + 0, 2);
        Assert.StartsWith("Leite", item.GetProperty("a").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("warning").ValueKind);
    }

    [Fact]
    public async Task Accept_MergesTheTwoProducts_MarksThemManual_AndUndoRestores()
    {
        var s = SeedPair();

        var accept = await Post($"/api/admin/matching/candidates/{s.Candidate}/accept");
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        _factory.SeedData(db =>
        {
            var a = db.StoreProducts.AsNoTracking().Single(p => p.Id == s.A);
            var b = db.StoreProducts.AsNoTracking().Single(p => p.Id == s.B);
            Assert.Equal(a.CanonicalProductId, b.CanonicalProductId);
            Assert.Equal(MatchStatus.ManualMatched, a.MatchStatus);
            Assert.Equal(MatchStatus.ManualMatched, b.MatchStatus);
            Assert.Equal(1, db.Products.Count(p => p.Id == s.CanonA || p.Id == s.CanonB));
        });

        var summary = await Json(await _client.GetAsync("/api/admin/matching/summary", TestContext.Current.CancellationToken));
        Assert.True(summary.GetProperty("multiChainCanonicals").GetInt32() >= 1);

        var undo = await Post($"/api/admin/matching/candidates/{s.Candidate}/undo");
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        _factory.SeedData(db =>
        {
            Assert.Equal(s.CanonA, db.StoreProducts.AsNoTracking().Single(p => p.Id == s.A).CanonicalProductId);
            Assert.Equal(s.CanonB, db.StoreProducts.AsNoTracking().Single(p => p.Id == s.B).CanonicalProductId);
            Assert.Equal(2, db.Products.Count(p => p.Id == s.CanonA || p.Id == s.CanonB));
            Assert.Equal(CandidateStatus.Rejected, db.MatchCandidates.AsNoTracking().Single(c => c.Id == s.Candidate).Status);
        });
    }

    [Fact]
    public async Task Accept_WithSameChainClash_IsRefusedWithAReason_UntilForced()
    {
        var s = SeedPair(sameChainClash: true);

        var review = await _client.GetAsync("/api/admin/matching/review?pageSize=50", TestContext.Current.CancellationToken);
        var item = (await Json(review)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == s.Candidate);
        Assert.Contains("same chain", item.GetProperty("warning").GetString());

        var refused = await Post($"/api/admin/matching/candidates/{s.Candidate}/accept");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("same chain", (await Json(refused)).GetProperty("message").GetString());

        var forced = await Post($"/api/admin/matching/candidates/{s.Candidate}/accept?force=true");
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
    }

    [Theory]
    [InlineData("reject", CandidateStatus.Rejected)]
    [InlineData("different-variant", CandidateStatus.DifferentVariant)]
    public async Task RejectAndDifferentVariant_AreStored_SoThePairIsNeverProposedAgain(string action, CandidateStatus expected)
    {
        var s = SeedPair();

        var r = await Post($"/api/admin/matching/candidates/{s.Candidate}/{action}");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        _factory.SeedData(db =>
        {
            var c = db.MatchCandidates.AsNoTracking().Single(x => x.Id == s.Candidate);
            Assert.Equal(expected, c.Status);
            Assert.Equal("manual-review", c.Method);
            Assert.NotNull(c.DecidedAt);
            Assert.Equal(s.CanonA, db.StoreProducts.AsNoTracking().Single(p => p.Id == s.A).CanonicalProductId);
        });
    }

    [Fact]
    public async Task Unknown_Candidate_Returns404_AndAppliedCannotBeRejectedWithoutUndo()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Post($"/api/admin/matching/candidates/{Guid.NewGuid()}/reject")).StatusCode);

        var s = SeedPair();
        await Post($"/api/admin/matching/candidates/{s.Candidate}/accept");
        Assert.Equal(HttpStatusCode.Conflict, (await Post($"/api/admin/matching/candidates/{s.Candidate}/reject")).StatusCode);
    }

    [Fact]
    public async Task Run_WithTheFeatureFlagOff_ExplainsWhyNothingHappened()
    {
        var r = await Post("/api/admin/matching/run");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(string.IsNullOrEmpty((await Json(r)).GetProperty("skippedReason").GetString()));
    }

    [Fact]
    public async Task MappingStats_IncludeTheMatchReportAdditions()
    {
        SeedPair();

        var stats = await Json(await _client.GetAsync("/api/admin/mapping/stats", TestContext.Current.CancellationToken));

        Assert.True(stats.TryGetProperty("multiChainCanonicals", out _));
        Assert.Contains(stats.GetProperty("candidatesByStatus").EnumerateArray(),
            x => x.GetProperty("status").GetString() == "NeedsReview");
    }
}
