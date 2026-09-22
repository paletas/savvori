using Microsoft.EntityFrameworkCore;
using Quartz;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;
using Savvori.WebApi.Scraping;
using Savvori.WebApi.Scraping.Scrapers;
using Savvori.WebApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Register scraping and model ActivitySources/Meters with the OTel pipeline. Microsoft.EntityFrameworkCore is
// EF Core's own built-in meter (query counts/duration, active DbContexts) - no package needed, just AddMeter.
builder.Services.AddSingleton<ScrapingTelemetry>();
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(ScrapingTelemetry.ActivitySourceName).AddSource(ModelTelemetry.ActivitySourceName))
    .WithMetrics(m => m
        .AddMeter(ScrapingTelemetry.MeterName)
        .AddMeter(ModelTelemetry.MeterName)
        .AddMeter("Microsoft.EntityFrameworkCore"));

builder.Services.AddOpenApi();

builder.Services.AddMemoryCache();

var connectionString = builder.Configuration.GetConnectionString("savvori") ?? "Data Source=data/savvori.db";
var sqliteBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
if (!builder.Environment.IsEnvironment("Testing")
    && Path.GetDirectoryName(Path.GetFullPath(sqliteBuilder.DataSource)) is { Length: > 0 } dbDir)
    Directory.CreateDirectory(dbDir);
builder.Services.AddDbContext<SavvoriDbContext>(opts => opts.UseSqlite(connectionString));

builder.Services.AddControllers();

// --- Scraping infrastructure ---
builder.Services.AddHttpClient("continente", c =>
{
    c.BaseAddress = new Uri("https://www.continente.pt");
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-PT,pt;q=0.9");
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient("pingodoce", c =>
{
    c.BaseAddress = new Uri("https://www.pingodoce.pt");
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-PT,pt;q=0.9");
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient("auchan", c =>
{
    c.BaseAddress = new Uri("https://www.auchan.pt");
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-PT,pt;q=0.9");
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient("celeiro", c =>
{
    c.BaseAddress = new Uri("https://www.celeiro.pt");
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-PT,pt;q=0.9");
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient("lidl", c =>
{
    c.BaseAddress = new Uri("https://www.lidl.pt");
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-PT,pt;q=0.9");
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddScoped<ScraperResultProcessor>();
builder.Services.AddScoped<TaxonomyMigrationService>();
builder.Services.AddScoped<ICategoryLocalizer, CategoryLocalizer>();

// Register all IStoreScraper implementations
builder.Services.AddScoped<IStoreScraper, ContinenteScraper>();
builder.Services.AddScoped<IStoreScraper, PingoDoceScraper>();
builder.Services.AddScoped<IStoreScraper, AuchanScraper>();
builder.Services.AddScoped<IStoreScraper, CeleiroScraper>();
builder.Services.AddScoped<IStoreScraper, LidlScraper>();

// Location and optimization services
builder.Services.AddHttpClient("geoapi", c =>
{
    c.BaseAddress = new Uri("https://geoapi.pt");
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Savvori/1.0");
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<ILocationService, GeoApiLocationService>();
builder.Services.AddScoped<IShoppingOptimizer, ShoppingOptimizer>();

// --- Remote model backend (feature-flagged, off by default; never called from request or scrape paths) ---
builder.Services.AddModelServices(builder.Configuration);

// --- Quartz scheduler ---
builder.Services.AddQuartz(q =>
{
    // Drains the model job queue; a no-op unless Model:Enabled and the circuit breaker is closed.
    var modelPollSeconds = Math.Max(10, builder.Configuration.GetValue("Model:Queue:PollSeconds", 60));
    q.AddJob<ModelQueueDrainJob>(opts => opts.WithIdentity("model-queue-drain"));
    q.AddTrigger(opts => opts
        .ForJob("model-queue-drain")
        .WithIdentity("model-queue-drain-trigger")
        .WithSimpleSchedule(s => s.WithIntervalInSeconds(modelPollSeconds).RepeatForever()));

    // Jobs are registered per StoreChain slug.
    // Each active store chain gets two daily trigger: 06:00 and 18:00 UTC.
    // Embedding scan (queues embed jobs) and nightly candidate generation; both no-ops unless Model:Enabled.
    q.AddJob<EmbeddingScanJob>(opts => opts.WithIdentity("model-embedding-scan"));
    q.AddTrigger(opts => opts.ForJob("model-embedding-scan").WithIdentity("model-embedding-scan-trigger")
        .WithCronSchedule(builder.Configuration.GetValue("Model:Scan:Cron", "0 15 * * * ?")!));
    q.AddJob<CandidateGenerationJob>(opts => opts.WithIdentity("model-candidate-generation"));
    q.AddTrigger(opts => opts.ForJob("model-candidate-generation").WithIdentity("model-candidate-generation-trigger")
        .WithCronSchedule(builder.Configuration.GetValue("Model:Candidates:Cron", "0 30 3 * * ?")!));

    q.AddJob<MatchingJob>(opts => opts.WithIdentity("model-matching"));
    q.AddTrigger(opts => opts.ForJob("model-matching").WithIdentity("model-matching-trigger")
        .WithCronSchedule(builder.Configuration.GetValue("Model:Matching:Cron", "0 45 3 * * ?")!));

    q.AddJob<CategoryClassifierJob>(opts => opts.WithIdentity("model-category-classifier"));
    q.AddTrigger(opts => opts.ForJob("model-category-classifier").WithIdentity("model-category-classifier-trigger")
        .WithCronSchedule(builder.Configuration.GetValue("Model:Categories:Cron", "0 0 4 * * ?")!));

    var chains = builder.Configuration
        .GetSection("Scraping:Chains")
        .Get<List<ScrapingChainConfig>>() ?? [];

    foreach (var chain in chains.Where(c => c.Enabled))
    {
        var jobKey = new JobKey($"scrape-{chain.Slug}");
        q.AddJob<StoreScrapeJob>(opts => opts
            .WithIdentity(jobKey)
            .UsingJobData(StoreScrapeJob.StoreChainSlugKey, chain.Slug)
            .UsingJobData(StoreScrapeJob.ScrapeLocationsKey, chain.ScrapeLocations)
            .StoreDurably());

        q.AddTrigger(opts => opts
            .ForJob(jobKey)
            .WithIdentity($"scrape-{chain.Slug}-morning")
            .WithCronSchedule(chain.MorningCron ?? "0 0 6 * * ?"));

        q.AddTrigger(opts => opts
            .ForJob(jobKey)
            .WithIdentity($"scrape-{chain.Slug}-evening")
            .WithCronSchedule(chain.EveningCron ?? "0 0 18 * * ?"));
    }
});
builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

var app = builder.Build();

app.MapDefaultEndpoints();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
    if (!app.Environment.IsEnvironment("Testing"))
    {
        DatabaseBackup.BackupIfMigrationsPending(db, app.Logger);
        await db.Database.MigrateAsync();
        await CategorySeeder.SeedAsync(db, app.Logger);

        // Taxonomy v2 is the category tree. A database that has never had it is migrated once, here (products keep their
        // old category in LegacyCategoryId; POST /api/admin/taxonomy/revert undoes it and is not re-applied on restart).
        // The seed rules are applied again on every start to products that still have no category.
        var taxonomy = scope.ServiceProvider.GetRequiredService<TaxonomyMigrationService>();
        if (!await db.TaxonomyMigrations.AnyAsync())
        {
            var applied = await taxonomy.ApplyAsync();
            app.Logger.LogInformation("Taxonomy v2 applied on startup: {Applied}.", applied.Applied);
        }
        var reseeded = await taxonomy.ReseedAsync();
        if (reseeded > 0) app.Logger.LogInformation("Seed rules categorised {Count} more product(s).", reseeded);

        await CategoryTranslations.SeedAsync(db);
        await StoreChainSeeder.SeedAsync(db, app.Configuration, app.Logger);

        // A bulk run cut short by a restart cannot resume: show it as failed (its finished part can still be undone).
        foreach (var interrupted in await db.BulkBatches.Where(b => b.Status == Savvori.Shared.BulkBatchStatus.Running || b.Status == Savvori.Shared.BulkBatchStatus.Undoing).ToListAsync())
        {
            interrupted.Status = Savvori.Shared.BulkBatchStatus.Failed;
            interrupted.Error = "Interrupted by application restart.";
            interrupted.FinishedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        // Mark any jobs left in Running state as Failed — they were interrupted by a restart.
        var staleJobs = await db.ScrapingJobs
            .Where(j => j.Status == ScrapingJobStatus.Running)
            .ToListAsync();
        if (staleJobs.Count > 0)
        {
            foreach (var stale in staleJobs)
            {
                stale.Status = ScrapingJobStatus.Failed;
                stale.CompletedAt = DateTime.UtcNow;
                stale.ErrorMessage = "Job interrupted by application restart.";
            }
            await db.SaveChangesAsync();
            app.Logger.LogWarning("Marked {Count} interrupted scraping job(s) as Failed on startup.", staleJobs.Count);
        }
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapControllers();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

public class ScrapingChainConfig
{
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool ScrapeLocations { get; set; } = true;
    public string? MorningCron { get; set; }
    public string? EveningCron { get; set; }
}
