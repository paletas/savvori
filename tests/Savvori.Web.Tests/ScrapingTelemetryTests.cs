using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Savvori.WebApi;
using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public class ScrapingTelemetryTests
{
    private record Measured(string Name, double Value, Dictionary<string, object?> Tags);

    private static MeterListener Listen(ScrapingTelemetry telemetry, List<Measured> into)
    {
        var listener = new MeterListener
        {
            // Other tests create their own ScrapingTelemetry in parallel, so only listen to this instance's meter.
            InstrumentPublished = (inst, l) =>
            {
                if (ReferenceEquals(inst.Meter, telemetry.Meter)) l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => into.Add(new(i.Name, v, ToDict(t))));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => into.Add(new(i.Name, v, ToDict(t))));
        listener.Start();
        return listener;
    }

    private static Dictionary<string, object?> ToDict(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var d = new Dictionary<string, object?>();
        foreach (var t in tags) d[t.Key] = t.Value;
        return d;
    }

    private static ScrapeOutcome Outcome(OutcomeResult result, params (int Code, int N)[] statuses)
    {
        var o = new ScrapeOutcome { Kind = OutcomeKind.Category, Key = "cat", Url = "https://x/y", Result = result };
        foreach (var (code, n) in statuses) o.StatusCounts[code] = n;
        return o;
    }

    private static RunHealth Health(ScrapingJobStatus status) => new(status, [], []);

    [Fact]
    public void RecordRun_Degraded_EmitsFailedBlockedSkippedDiscoveryCanary()
    {
        using var telemetry = new ScrapingTelemetry();
        var seen = new List<Measured>();
        using var listener = Listen(telemetry, seen);
        var result = new ScrapeRunResult([], [Outcome(OutcomeResult.Failed), Outcome(OutcomeResult.Blocked), Outcome(OutcomeResult.Ok),
                new ScrapeOutcome { Kind = OutcomeKind.Discovery, Result = OutcomeResult.Failed }], [],
            Canary: new CanaryResult(false, "down", 1));

        telemetry.RecordRun("pingodoce", result, Health(ScrapingJobStatus.Degraded), new ProcessResult(0, 0));

        foreach (var name in new[]
        {
            "savvori.scraping.categories.failed", "savvori.scraping.blocked", "savvori.scraping.stale_marking.skipped",
            "savvori.scraping.discovery.failed", "savvori.scraping.canary.failed"
        })
        {
            var m = Assert.Single(seen, x => x.Name == name);
            Assert.Equal("pingodoce", m.Tags["store.chain.slug"]);
        }
        Assert.Equal(3, seen.Single(x => x.Name == "savvori.scraping.categories.failed").Value);
        Assert.Equal(1, seen.Single(x => x.Name == "savvori.scraping.blocked").Value);
    }

    [Fact]
    public void RecordRun_IncompleteRun_EmitsStaleMarkingSkipped()
    {
        using var telemetry = new ScrapingTelemetry();
        var seen = new List<Measured>();
        using var listener = Listen(telemetry, seen);
        var result = new ScrapeRunResult([], [Outcome(OutcomeResult.Ok)], ["Loop detected"], Incomplete: true);

        telemetry.RecordRun("pingodoce", result, Health(ScrapingJobStatus.CompletedWithWarnings), new ProcessResult(0, 0));

        Assert.Single(seen, x => x.Name == "savvori.scraping.stale_marking.skipped");
    }

    [Fact]
    public void RecordRun_Healthy_EmitsOnlyHttpResponses()
    {
        using var telemetry = new ScrapingTelemetry();
        var seen = new List<Measured>();
        using var listener = Listen(telemetry, seen);
        var result = new ScrapeRunResult([], [Outcome(OutcomeResult.Ok, (200, 3))], []);

        telemetry.RecordRun("lidl", result, Health(ScrapingJobStatus.Completed), new ProcessResult(10, 0));

        var m = Assert.Single(seen);
        Assert.Equal("savvori.scraping.http.responses", m.Name);
        Assert.Equal("lidl", m.Tags["store.chain.slug"]);
    }

    [Fact]
    public void RecordRun_StaleMarked_EmitsDeactivated()
    {
        using var telemetry = new ScrapingTelemetry();
        var seen = new List<Measured>();
        using var listener = Listen(telemetry, seen);

        telemetry.RecordRun("continente", new ScrapeRunResult([], [], []), Health(ScrapingJobStatus.Completed), new ProcessResult(10, 4));

        var m = Assert.Single(seen);
        Assert.Equal("savvori.scraping.products.deactivated", m.Name);
        Assert.Equal(4, m.Value);
        Assert.Equal("continente", m.Tags["store.chain.slug"]);
    }

    [Fact]
    public void RecordRun_HttpResponses_TaggedByStatus()
    {
        using var telemetry = new ScrapingTelemetry();
        var seen = new List<Measured>();
        using var listener = Listen(telemetry, seen);
        var result = new ScrapeRunResult([],
            [Outcome(OutcomeResult.Ok, (200, 3), (403, 1)), Outcome(OutcomeResult.Failed, (403, 2))], []);

        telemetry.RecordRun("auchan", result, Health(ScrapingJobStatus.CompletedWithWarnings), new ProcessResult(0, 0));

        var http = seen.Where(x => x.Name == "savvori.scraping.http.responses").ToList();
        Assert.Equal(2, http.Count);
        Assert.Equal(3, http.Single(x => (string?)x.Tags["http.status"] == "200").Value);
        Assert.Equal(3, http.Single(x => (string?)x.Tags["http.status"] == "403").Value);
        Assert.All(http, x => Assert.Equal("auchan", x.Tags["store.chain.slug"]));
    }

    [Fact]
    public async Task Sampler_SampleAsync_PublishesHealthAndStaleSkipped()
    {
        var dbName = $"ScrapingSampler_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<SavvoriDbContext>(o => o.UseInMemoryDatabase(dbName));
        using var provider = services.BuildServiceProvider();

        var chainId = Guid.NewGuid();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
            db.StoreChains.Add(new StoreChain { Id = chainId, Name = "Pingo Doce", Slug = "pingodoce", BaseUrl = "https://pd.pt", IsActive = true });
            for (var i = 0; i < 2; i++)
                db.ScrapingJobs.Add(new ScrapingJob
                {
                    Id = Guid.NewGuid(), StoreChainId = chainId, Status = ScrapingJobStatus.Degraded,
                    StartedAt = DateTime.UtcNow.AddHours(-3 + i), CompletedAt = DateTime.UtcNow.AddHours(-3 + i).AddMinutes(5),
                    StaleMarkingSkipped = true, DropRatio = 0.4
                });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var telemetry = new ScrapingTelemetry();
        var sampler = new ScrapingTelemetrySampler(
            provider.GetRequiredService<IServiceScopeFactory>(), telemetry, NullLogger<ScrapingTelemetrySampler>.Instance);
        await sampler.SampleAsync(TestContext.Current.CancellationToken);

        var seen = new List<Measured>();
        using var listener = Listen(telemetry, seen);
        listener.RecordObservableInstruments();

        var health = Assert.Single(seen, x => x.Name == "savvori.scraping.run.health");
        Assert.Equal(2, health.Value);
        Assert.Equal("pingodoce", health.Tags["store.chain.slug"]);
        Assert.Equal(2, Assert.Single(seen, x => x.Name == "savvori.scraping.stale_skipped.consecutive").Value);
    }
}
