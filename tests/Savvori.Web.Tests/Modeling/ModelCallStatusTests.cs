using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class ModelCallStatusTests : IDisposable
{
    private readonly RecorderRig _rig = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    private async Task<ModelStatus> StatusAsync(ModelBreakers? breakers = null)
    {
        using var scope = _rig.Services.CreateScope();
        return await _rig.StatusService(scope.ServiceProvider.GetRequiredService<SavvoriDbContext>(), breakers).GetAsync(Ct);
    }

    private ModelCall Row(string task, double daysAgo, decimal? cost) => new()
    {
        Id = Guid.NewGuid(),
        // "Today" rows sit at 00:30 UTC of the current day so they stay inside today whatever the clock.
        At = daysAgo == 0
            ? _rig.Time.GetUtcNow().UtcDateTime.Date.AddMinutes(30)
            : _rig.Time.GetUtcNow().UtcDateTime.AddDays(-daysAgo),
        Backend = "openrouter", Task = task, Model = "m", Status = ModelCallStatus.Ok, CostUsd = cost
    };

    [Fact]
    public async Task Breakers_ListBothBackends_WithState()
    {
        _rig.Options.Breaker.FailureThreshold = 3;
        var breakers = new ModelBreakers(Microsoft.Extensions.Options.Options.Create(_rig.Options), _rig.Time);
        for (var i = 0; i < 3; i++) breakers.For(ModelBackend.OpenRouter).RecordFailure("down");

        var status = await StatusAsync(breakers);

        Assert.Equal(["gateway", "openrouter"], status.Breakers.Select(b => b.Backend).ToArray());
        Assert.Equal("Closed", status.Breakers[0].State);
        var open = status.Breakers[1];
        Assert.Equal("Open", open.State);
        Assert.NotNull(open.RetryAt);
        Assert.Equal("Closed", status.BreakerState);
    }

    [Fact]
    public async Task StatusExposesThreeTaskModels()
    {
        _rig.Options.CategoryModel = "cat-m";
        _rig.Options.PairJudgeModel = "pair-m";
        _rig.Options.TranslatorModel = "tr-m";

        var status = await StatusAsync();

        Assert.Equal("cat-m", status.CategoryModel);
        Assert.Equal("pair-m", status.PairJudgeModel);
        Assert.Equal("tr-m", status.TranslatorModel);
        Assert.Equal(_rig.Options.EmbeddingModel, status.EmbeddingModel);
    }

    [Fact]
    public async Task Spend_TodayVs30Days_AndPerTask()
    {
        _rig.Seed(
            Row("pair", 0, 0.002m),
            Row("pair", 10, 0.003m),
            Row("pair", 31, 0.5m),
            Row("category", 0, 0.001m),
            Row("category", 5, null));

        var status = await StatusAsync();

        Assert.Equal(0.003m, status.SpendTodayUsd);
        Assert.Equal(0.006m, status.Spend30dUsd);
        Assert.Equal(["pair", "category"], status.SpendByTask.Select(t => t.Task).ToArray());
        Assert.Equal(2, status.SpendByTask[0].Calls30d);
        Assert.Equal(2, status.SpendByTask[1].Calls30d); // the null-cost row counts as a call, cost 0
        Assert.Equal(0.001m, status.SpendByTask[1].Last30dUsd);
    }
}
