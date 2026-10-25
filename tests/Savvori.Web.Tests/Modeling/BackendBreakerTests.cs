using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class BackendBreakerTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    private void Open(ModelBackend backend)
    {
        for (var i = 0; i < 3; i++) _h.Breakers.For(backend).RecordFailure("down");
    }

    /// <summary>Queues Embed jobs (products without an embedding) and one Judge job (a candidate).</summary>
    private async Task<Guid> EmbedAndJudgeJobsDueAsync()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Iogurte Grego Natural");
        var (b, _) = _h.AddListed(_h.ChainB, "Iogurte Grego");
        var c = _h.AddCandidate(a, b, 0.85);
        _h.Judge.Verdict = JudgeVerdict.Yes;
        await _h.RunMatchingAsync();
        await _h.ScanAsync();
        return c;
    }

    private List<ModelJob> Jobs() => _h.Query(db => db.ModelJobs.AsNoTracking().ToList());

    [Fact]
    public void ModelBreakers_AreIndependent()
    {
        Open(ModelBackend.OpenRouter);

        Assert.False(_h.Breakers.For(ModelBackend.OpenRouter).IsClosed);
        Assert.True(_h.Breakers.For(ModelBackend.Gateway).IsClosed);
    }

    [Fact]
    public async Task OpenRouterBreakerOpen_EmbedJobStillProcessed_JudgeJobNotClaimed()
    {
        await EmbedAndJudgeJobsDueAsync();
        Open(ModelBackend.OpenRouter);

        await _h.DrainAsync();

        var jobs = Jobs();
        Assert.All(jobs.Where(j => j.Type == ModelJobType.Embed), j => Assert.Equal(ModelJobStatus.Done, j.Status));
        var judge = Assert.Single(jobs, j => j.Type == ModelJobType.Judge);
        Assert.Equal(ModelJobStatus.Pending, judge.Status);
        Assert.Equal(0, judge.Attempts);
        Assert.True(_h.Breakers.For(ModelBackend.Gateway).IsClosed);
    }

    [Fact]
    public async Task OpenBackend_IsProbedAndRecovers()
    {
        var c = await EmbedAndJudgeJobsDueAsync();
        Open(ModelBackend.OpenRouter);
        _h.Time.Advance(TimeSpan.FromSeconds(61));

        await _h.DrainAsync(); // probe closes OpenRouter; Judge is not drained in this run

        Assert.True(_h.Breakers.For(ModelBackend.OpenRouter).IsClosed);
        Assert.Equal(ModelJobStatus.Pending, Jobs().Single(j => j.Type == ModelJobType.Judge).Status);
        Assert.Equal(CandidateStatus.PendingJudge, _h.Candidate(c).Status);

        await _h.DrainAsync();

        Assert.Equal(ModelJobStatus.Done, Jobs().Single(j => j.Type == ModelJobType.Judge).Status);
    }

    [Fact]
    public async Task GatewayDown_DoesNotStopJudgeJobs()
    {
        await EmbedAndJudgeJobsDueAsync();
        Open(ModelBackend.Gateway);

        await _h.DrainAsync();

        var jobs = Jobs();
        Assert.Equal(ModelJobStatus.Done, jobs.Single(j => j.Type == ModelJobType.Judge).Status);
        Assert.All(jobs.Where(j => j.Type == ModelJobType.Embed), j => Assert.Equal(ModelJobStatus.Pending, j.Status));
    }

    [Fact]
    public void BreakerOpenGauge_HasBackendTag()
    {
        var telemetry = _h.Services.GetRequiredService<ModelTelemetry>();
        // Other tests create their own ModelTelemetry in parallel, so only listen to this instance's meter.
        var meter = (Meter)typeof(ModelTelemetry).GetField("_meter", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(telemetry)!;
        var seen = new Dictionary<string, int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (ReferenceEquals(inst.Meter, meter) && inst.Name == "savvori.model.breaker.open") l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            foreach (var t in tags)
                if (t.Key == "backend") seen[(string)t.Value!] = value;
        });
        listener.Start();

        telemetry.SetBreakerOpen(ModelBackend.OpenRouter, true);
        listener.RecordObservableInstruments();

        Assert.Equal(2, seen.Count);
        Assert.Equal(1, seen["openrouter"]);
        Assert.Equal(0, seen["gateway"]);
    }
}
