using Microsoft.EntityFrameworkCore;
using Savvori.Shared;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class ThrottleDeferralTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    // The shared host caps queue delays at 100 s; use the production default so Retry-After values above it are visible.
    public ThrottleDeferralTests() => _h.Options.Queue.MaxDelaySeconds = 3600;

    private static readonly ModelOptions.QueueOptions Q = new() { BaseDelaySeconds = 30, MaxDelaySeconds = 3600, ThrottleFloorSeconds = 30 };

    [Theory]
    [InlineData(ThrottleKind.Routing, 10, 0, 0.0, 30)]      // Retry-After below the floor
    [InlineData(ThrottleKind.Routing, 60, 0, 0.0, 60)]
    [InlineData(ThrottleKind.Routing, 60, 1, 0.0, 120)]
    [InlineData(ThrottleKind.Routing, 60, 2, 0.0, 240)]
    [InlineData(ThrottleKind.Routing, 60, 0, 1.0, 75)]      // full jitter is x1.25
    [InlineData(ThrottleKind.Routing, 60, 6, 0.0, 3600)]    // 3840 capped
    [InlineData(ThrottleKind.Routing, 60, 20, 0.0, 3600)]
    [InlineData(ThrottleKind.Routing, 7200, 0, 0.0, 3600)]  // gateway retryAtMs hours out is re-checked hourly
    [InlineData(ThrottleKind.Proxy, 10, 0, 0.0, 30)]
    [InlineData(ThrottleKind.Proxy, 60, 2, 0.0, 240)]
    [InlineData(ThrottleKind.Quota, 7200, 0, 0.0, 7200)]    // not capped at an hour
    [InlineData(ThrottleKind.Quota, 7200, 5, 1.0, 7200)]    // no progression, no jitter
    [InlineData(ThrottleKind.Quota, 5, 0, 0.0, 30)]
    public void Compute_FollowsThePolicy(ThrottleKind kind, double retryAfterSeconds, int prior, double jitter, double expectedSeconds)
    {
        var delay = ModelDeferral.Compute(kind, TimeSpan.FromSeconds(retryAfterSeconds), prior, Q, jitter);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, 3);
    }

    [Fact]
    public void ThrottleTracker_CountsAndClears()
    {
        var t = new ThrottleTracker();
        var id = Guid.NewGuid();
        Assert.Equal(0, t.Prior(id));
        t.Record(id);
        t.Record(id);
        Assert.Equal(2, t.Prior(id));
        t.Clear(id);
        Assert.Equal(0, t.Prior(id));
    }

    private Guid JudgeJob()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Iogurte Grego Natural");
        var (b, _) = _h.AddListed(_h.ChainB, "Iogurte Grego");
        return _h.AddCandidate(a, b, 0.85);
    }

    private ModelJob TheJudgeJob() => _h.Query(db => db.ModelJobs.AsNoTracking().Single(j => j.Type == ModelJobType.Judge));

    [Fact]
    public async Task ThrottledJob_IsDeferred_AttemptsUnchanged_BreakerClosed()
    {
        var c = JudgeJob();
        await _h.RunMatchingAsync();
        var before = TheJudgeJob();
        _h.JudgeFaults.GoDown(FaultMode.Throttled);
        _h.JudgeFaults.ThrottleRetryAfter = TimeSpan.FromSeconds(120);

        await _h.DrainAsync();

        var job = TheJudgeJob();
        Assert.Equal(ModelJobStatus.Pending, job.Status);
        Assert.Equal(before.Attempts, job.Attempts);
        Assert.True(job.NextAttemptAt >= _h.Time.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(120));
        Assert.NotNull(job.LastError);
        var breaker = _h.Breakers.For(ModelBackend.OpenRouter).Snapshot();
        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Equal(0, breaker.ConsecutiveFailures);
        Assert.Equal(CandidateStatus.PendingJudge, _h.Candidate(c).Status);
    }

    [Fact]
    public async Task ThrottledGateway_StopsEmbedAndTranslate_ForRestOfRun_ButJudgeStillRuns()
    {
        _h.Options.BatchSize = 1; // every job is its own chunk
        for (var i = 0; i < 5; i++) _h.AddProduct(_h.ChainC, $"Produto {i}", "Marca");
        JudgeJob();
        await _h.RunMatchingAsync();
        await _h.ScanAsync();
        _h.Judge.Verdict = JudgeVerdict.Yes;
        _h.Faults.GoDown(FaultMode.Throttled);
        _h.Faults.ThrottleRetryAfter = TimeSpan.FromSeconds(120);

        await _h.DrainAsync();

        Assert.Equal(1, _h.Faults.Calls); // one request, then the gateway is left alone for the run
        var jobs = _h.Query(db => db.ModelJobs.AsNoTracking().ToList());
        var embeds = jobs.Where(j => j.Type == ModelJobType.Embed).ToList();
        Assert.NotEmpty(embeds);
        Assert.All(embeds, j => Assert.Equal(ModelJobStatus.Pending, j.Status));
        Assert.All(embeds, j => Assert.Equal(0, j.Attempts));
        Assert.True(embeds.Select(j => j.NextAttemptAt).Distinct().Count() <= 2); // the 429 one and the released ones share a wait
        Assert.Equal(ModelJobStatus.Done, jobs.Single(j => j.Type == ModelJobType.Judge).Status);
        Assert.True(_h.Breaker.IsClosed);
    }

    [Fact]
    public async Task ConsecutiveRoutingThrottles_BackOffProgressively()
    {
        JudgeJob();
        await _h.RunMatchingAsync();
        _h.JudgeFaults.GoDown(FaultMode.Throttled);
        _h.JudgeFaults.ThrottleRetryAfter = TimeSpan.FromSeconds(60);

        var lengths = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var now = _h.Time.GetUtcNow().UtcDateTime;
            await _h.DrainAsync();
            var job = TheJudgeJob();
            lengths.Add((job.NextAttemptAt - now).TotalSeconds);
            Assert.Equal(0, job.Attempts);
            _h.Time.Advance(job.NextAttemptAt - now + TimeSpan.FromSeconds(1));
        }

        Assert.InRange(lengths[0], 60, 75);
        Assert.InRange(lengths[1], 120, 150);
        Assert.InRange(lengths[2], 240, 300);

        // A success clears the counter: the next throttle is back near the base.
        _h.JudgeFaults.Recover();
        await _h.DrainAsync();
        Assert.Equal(ModelJobStatus.Done, TheJudgeJob().Status);
    }

    [Fact]
    public async Task ThrottledJob_NeverDeadLetters()
    {
        _h.Options.Queue.MaxAttempts = 2;
        JudgeJob();
        await _h.RunMatchingAsync();
        _h.JudgeFaults.GoDown(FaultMode.Throttled);

        for (var i = 0; i < 5; i++)
        {
            await _h.DrainAsync();
            _h.Time.Advance(TimeSpan.FromHours(2));
            Assert.Equal(ModelJobStatus.Pending, TheJudgeJob().Status);
        }

        _h.JudgeFaults.Recover();
        await _h.DrainAsync();
        Assert.Equal(ModelJobStatus.Done, TheJudgeJob().Status);
    }

    [Fact]
    public async Task QuotaThrottle_DefersLong()
    {
        JudgeJob();
        await _h.RunMatchingAsync();
        _h.JudgeFaults.GoDown(FaultMode.Throttled);
        _h.JudgeFaults.ThrottleKind = ThrottleKind.Quota;
        _h.JudgeFaults.ThrottleRetryAfter = TimeSpan.FromHours(6);
        var now = _h.Time.GetUtcNow().UtcDateTime;

        await _h.DrainAsync();

        var wait = TheJudgeJob().NextAttemptAt - now;
        Assert.InRange(wait.TotalHours, 5.99, 6.01); // not capped at the one-hour queue maximum
    }
}
