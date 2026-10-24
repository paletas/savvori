using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi.Controllers;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class JudgeReviewTests : IDisposable
{
    private readonly PipelineHost _h = new();

    public void Dispose() => _h.Dispose();

    private (Guid Candidate, Guid A, Guid B) JudgeCase(string nameA = "Iogurte Grego Natural", string nameB = "Iogurte Grego")
    {
        var (a, _) = _h.AddListed(_h.ChainA, nameA);
        var (b, _) = _h.AddListed(_h.ChainB, nameB);
        return (_h.AddCandidate(a, b, 0.85), a, b);
    }

    /// <summary>Drains with the clock moved past any backoff, so each call is one more attempt.</summary>
    private async Task DrainOnceMoreAsync()
    {
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        await _h.DrainAsync();
    }

    [Fact]
    public async Task JudgeJob_PersistsScore()
    {
        var (c, _, _) = JudgeCase();
        _h.Judge.Verdict = JudgeVerdict.Yes;
        _h.Judge.Score = 0.73;

        await _h.RunMatchingAsync();
        await _h.DrainAsync();

        var cand = _h.Candidate(c);
        Assert.Equal(0.73, cand.JudgeScore);
        Assert.Equal(JudgeVerdict.Yes, cand.JudgeVerdict);
        Assert.Equal(CandidateStatus.NeedsReview, cand.Status);
        Assert.Equal("embedding-judge", cand.Suggestion);
    }

    [Fact]
    public async Task DeadLetteredJudgeJob_SendsCandidateToReview()
    {
        _h.Options.Queue.MaxAttempts = 2;
        _h.Options.Breaker.FailureThreshold = 100; // keep the breaker out of it: this is about the queue
        var (c, _, _) = JudgeCase();
        await _h.RunMatchingAsync();
        _h.With(db =>
        {
            var cand = db.MatchCandidates.Single(x => x.Id == c);
            cand.JudgeVerdict = JudgeVerdict.Yes; // garbage that must not survive
            cand.JudgeScore = 0.99;
        });
        _h.JudgeFaults.GoDown(FaultMode.BadResponse);

        await _h.DrainAsync(); // attempt 1
        Assert.Equal(CandidateStatus.PendingJudge, _h.Candidate(c).Status);
        Assert.Equal(ModelJobStatus.Pending, _h.Query(db => db.ModelJobs.Single(j => j.Type == ModelJobType.Judge).Status));

        await DrainOnceMoreAsync(); // attempt 2 -> dead letter

        Assert.Equal(ModelJobStatus.DeadLetter, _h.Query(db => db.ModelJobs.Single(j => j.Type == ModelJobType.Judge).Status));
        var after = _h.Candidate(c);
        Assert.Equal(CandidateStatus.NeedsReview, after.Status);
        Assert.Null(after.JudgeVerdict);
        Assert.Null(after.JudgeScore);
        Assert.Null(after.Suggestion);
        Assert.NotNull(after.Note);
    }

    [Fact]
    public async Task NonJudgeDeadLetter_DoesNotTouchCandidates()
    {
        _h.Options.Queue.MaxAttempts = 2;
        _h.Options.Breaker.FailureThreshold = 100;
        var (c, _, _) = JudgeCase();
        _h.With(db => db.MatchCandidates.Single(x => x.Id == c).Status = CandidateStatus.PendingJudge);
        _h.AddProduct(_h.ChainC, "Leite", "Mimosa");
        await _h.ScanAsync(); // queues an Embed job
        _h.Faults.GoDown(FaultMode.BadResponse);

        await _h.DrainAsync();
        await DrainOnceMoreAsync();

        Assert.Contains(_h.Query(db => db.ModelJobs.ToList()), j => j.Type == ModelJobType.Embed && j.Status == ModelJobStatus.DeadLetter);
        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.PendingJudge, cand.Status);
        Assert.Null(cand.Note);
    }

    private Guid ReviewCandidate(string suffix, double cosine, double? score, DateTime decidedAt)
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Produto A " + suffix);
        var (b, _) = _h.AddListed(_h.ChainB, "Produto B " + suffix);
        var id = _h.AddCandidate(a, b, cosine);
        _h.With(db =>
        {
            var cand = db.MatchCandidates.Single(x => x.Id == id);
            cand.Status = CandidateStatus.NeedsReview;
            cand.JudgeScore = score;
            cand.DecidedAt = decidedAt;
        });
        return id;
    }

    private async Task<List<(Guid Id, double? Score)>> ReviewAsync(string sort)
    {
        using var scope = _h.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<MatchingAdminController>(scope.ServiceProvider);
        var ok = Assert.IsType<OkObjectResult>(await controller.Review(sort: sort, ct: TestContext.Current.CancellationToken));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return doc.RootElement.GetProperty("Items").EnumerateArray()
            .Select(i => (i.GetProperty("Id").GetGuid(),
                i.GetProperty("JudgeScore").ValueKind == JsonValueKind.Null ? (double?)null : i.GetProperty("JudgeScore").GetDouble()))
            .ToList();
    }

    [Fact]
    public async Task Review_SortByScore_ListsHighestFirst_AndNullsLast_DefaultKeepsOldOrder()
    {
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var low = ReviewCandidate("low", 0.80, 0.2, t.AddDays(3));       // newest
        var none = ReviewCandidate("none", 0.90, null, t.AddDays(2));
        var high = ReviewCandidate("high", 0.70, 0.9, t.AddDays(1));     // oldest

        var byScore = await ReviewAsync("score");
        Assert.Equal([high, low, none], byScore.Select(x => x.Id));
        Assert.Equal([0.9, 0.2, null], byScore.Select(x => x.Score));

        var byDefault = await ReviewAsync("default");
        Assert.Equal([low, none, high], byDefault.Select(x => x.Id)); // DecidedAt descending, as before
    }
}
