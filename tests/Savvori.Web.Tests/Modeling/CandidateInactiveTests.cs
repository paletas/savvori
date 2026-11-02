using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

/// <summary>Undecided candidates whose product went inactive are kept and flagged, and no job or run touches them.</summary>
public sealed class CandidateInactiveTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    private static float[] V(double angleDegrees)
    {
        var r = angleDegrees * Math.PI / 180;
        return [(float)Math.Cos(r), (float)Math.Sin(r), 0f, 0f];
    }

    private void Deactivate(Guid id) => _h.With(db => db.StoreProducts.Single(p => p.Id == id).IsActive = false);

    private void SetStatus(Guid id, CandidateStatus status, string? suggestion = null) => _h.With(db =>
    {
        var c = db.MatchCandidates.Single(x => x.Id == id);
        c.Status = status;
        c.Suggestion = suggestion;
    });

    private (Guid Candidate, Guid A, Guid B) Pair(double cosine = 0.97)
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Leite Meio Gordo");
        var (b, _) = _h.AddListed(_h.ChainB, "Leite Gordo Meio");
        return (_h.AddCandidate(a, b, cosine), a, b);
    }

    [Fact]
    public async Task Generator_KeepsUndecidedOnInactive_ButStillRemovesQualifyingLoss()
    {
        var (kept, a, b) = Pair();
        _h.SetEmbedding(a, V(0));
        _h.SetEmbedding(b, V(10));
        Deactivate(b);
        SetStatus(kept, CandidateStatus.NeedsReview);

        // Control: both sides active but no longer similar, so the leftover is stale.
        var (control, c, d) = Pair();
        _h.SetEmbedding(c, V(0));
        _h.SetEmbedding(d, V(90));

        var run = await _h.GenerateAsync();

        Assert.True(_h.Query(db => db.MatchCandidates.Any(x => x.Id == kept)));
        Assert.False(_h.Query(db => db.MatchCandidates.Any(x => x.Id == control)));
        Assert.Equal(1, run.Removed);
    }

    [Theory]
    [InlineData(CandidateStatus.Applied)]
    [InlineData(CandidateStatus.Rejected)]
    [InlineData(CandidateStatus.DifferentVariant)]
    public async Task Generator_LeavesDecidedUntouched(CandidateStatus status)
    {
        var (id, a, b) = Pair();
        _h.SetEmbedding(a, V(0));
        _h.SetEmbedding(b, V(10));
        Deactivate(a);
        SetStatus(id, status);
        var before = _h.Candidate(id);

        var run = await _h.GenerateAsync();

        var after = _h.Candidate(id);
        Assert.Equal(0, run.Removed);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.Cosine, after.Cosine);
    }

    [Fact]
    public async Task Pipeline_SkipsInactive()
    {
        var (id, a, _) = Pair(0.97);
        Deactivate(a);

        var r = await _h.RunMatchingAsync();

        Assert.Equal(0, r.Evaluated);
        Assert.Equal(CandidateStatus.Proposed, _h.Candidate(id).Status);
        Assert.Equal(0, _h.JudgeCalls.Calls);
    }

    [Fact]
    public async Task Bulk_SkipsInactive()
    {
        var (id, a, _) = Pair(0.97);
        SetStatus(id, CandidateStatus.NeedsReview, "embedding-cosine");
        using (var scope = _h.Services.CreateScope())
        {
            var eligible = await scope.ServiceProvider.GetRequiredService<MatchBulkService>().Eligible(0.90)
                .Select(c => c.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal([id], eligible);
        }

        Deactivate(a);

        using var scope2 = _h.Services.CreateScope();
        var after = await scope2.ServiceProvider.GetRequiredService<MatchBulkService>().Eligible(0.90)
            .Select(c => c.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(after);
    }

    [Fact]
    public async Task ModelJob_InactiveSubject_SkippedNoCall()
    {
        var (id, a, _) = Pair(0.85);
        SetStatus(id, CandidateStatus.PendingJudge);
        Deactivate(a);
        var inactive = _h.AddProduct(_h.ChainC, "Arroz Agulha", active: false);
        using (var scope = _h.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<ModelJobQueue>();
            await queue.EnqueueAsync(ModelJobType.Judge, id, "any-hash", TestContext.Current.CancellationToken);
            await queue.EnqueueAsync(ModelJobType.Embed, inactive, "any-hash", TestContext.Current.CancellationToken);
        }

        await _h.DrainAsync();

        var jobs = _h.Query(db => db.ModelJobs.AsNoTracking().ToList());
        Assert.All(jobs, j => Assert.Equal(ModelJobStatus.Done, j.Status));
        Assert.Equal(0, _h.JudgeCalls.Calls);
        Assert.Equal(0, _h.Embedder.Calls);
        Assert.Equal(CandidateStatus.PendingJudge, _h.Candidate(id).Status);
    }
}
