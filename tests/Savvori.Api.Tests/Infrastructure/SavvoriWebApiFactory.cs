using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Quartz;
using Savvori.WebApi;
using Savvori.WebApi.Services;

namespace Savvori.Api.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory for integration tests. Uses a shared in-memory SQLite database, mocks ILocationService,
/// and disables Quartz background jobs.
/// </summary>
public class SavvoriWebApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = OpenKeeperConnection();

    // A shared-cache in-memory SQLite database lives only while at least one connection is open.
    private static SqliteConnection OpenKeeperConnection()
    {
        var connection = new SqliteConnection($"Data Source=file:TestDb_{Guid.NewGuid():N}?mode=memory&cache=shared");
        connection.Open();
        return connection;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }

    /// <summary>Mocked location service, configured per-test as needed.</summary>
    public ILocationService LocationService { get; } = Substitute.For<ILocationService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Each factory gets an isolated in-memory SQLite database; the keeper connection below keeps it alive
        builder.UseSetting("ConnectionStrings:savvori", _connection.ConnectionString);

        builder.ConfigureServices(services =>
        {
            // Replace ILocationService with mock
            services.RemoveAll<ILocationService>();
            LocationService
                .ResolvePostalCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new GeoCoordinate(38.716, -9.139));
            LocationService
                .CalculateDistanceKm(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>())
                .Returns(callInfo =>
                {
                    var lat1 = callInfo.ArgAt<double>(0);
                    var lon1 = callInfo.ArgAt<double>(1);
                    var lat2 = callInfo.ArgAt<double>(2);
                    var lon2 = callInfo.ArgAt<double>(3);
                    var dlat = (lat2 - lat1) * Math.PI / 180;
                    var dlon = (lon2 - lon1) * Math.PI / 180;
                    var a = Math.Sin(dlat / 2) * Math.Sin(dlat / 2)
                          + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                          * Math.Sin(dlon / 2) * Math.Sin(dlon / 2);
                    return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
                });
            services.AddScoped<ILocationService>(_ => LocationService);

            // Remove Quartz hosted services to prevent background job scheduling
            var quartzDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Quartz") == true ||
                            d.ImplementationType?.FullName?.Contains("Quartz") == true)
                .ToList();
            foreach (var d in quartzDescriptors)
                services.Remove(d);

            // Register a mock ISchedulerFactory so ScrapingAdminController can be resolved
            var schedulerMock = Substitute.For<IScheduler>();
            schedulerMock.CheckExists(Arg.Any<JobKey>(), Arg.Any<CancellationToken>()).Returns(false);
            var schedulerFactoryMock = Substitute.For<ISchedulerFactory>();
            schedulerFactoryMock.GetScheduler(Arg.Any<CancellationToken>()).Returns(schedulerMock);
            services.AddSingleton<ISchedulerFactory>(schedulerFactoryMock);
        });
    }

    /// <summary>Seed test data via a synchronous action on the DbContext.</summary>
    public void SeedData(Action<SavvoriDbContext> seeder)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
        seeder(db);
        db.SaveChanges();
    }

}
