using Savvori.WebApi;
using Savvori.Shared;
using Savvori.WebApi.Scraping;
using Savvori.WebApi.Scraping.Scrapers;
using Savvori.WebApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();

builder.Services.AddMemoryCache();

builder.AddNpgsqlDbContext<SavvoriDbContext>("savvori");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "JwtOrCookie";
    options.DefaultChallengeScheme = "JwtOrCookie";
})
.AddPolicyScheme("JwtOrCookie", "JWT or Cookie", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
            return JwtBearerDefaults.AuthenticationScheme;
        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new()
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = false // TODO: Set to true and configure key in production
    };
})
.AddCookie();

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

builder.Services.AddHttpClient("minipreco", c =>
{
    c.BaseAddress = new Uri("https://www.minipreco.pt");
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-PT,pt;q=0.9");
    c.Timeout = TimeSpan.FromSeconds(60);
});

// Default HttpClient for stub scrapers (Lidl, Intermarché, Mercadona)
foreach (var stubSlug in new[] { "lidl", "intermarche", "mercadona" })
{
    builder.Services.AddHttpClient(stubSlug, c =>
    {
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        c.Timeout = TimeSpan.FromSeconds(30);
    });
}

builder.Services.AddScoped<ScraperResultProcessor>();

// Register all IStoreScraper implementations
builder.Services.AddScoped<IStoreScraper, ContinenteScraper>();
builder.Services.AddScoped<IStoreScraper, PingoDoceScraper>();
builder.Services.AddScoped<IStoreScraper, AuchanScraper>();
builder.Services.AddScoped<IStoreScraper, MiniprecoScraper>();
builder.Services.AddScoped<IStoreScraper, LidlScraper>();
builder.Services.AddScoped<IStoreScraper, InterarcheScraper>();
builder.Services.AddScoped<IStoreScraper, MercadonaScraper>();

// Location and optimization services
builder.Services.AddHttpClient("geoapi", c =>
{
    c.BaseAddress = new Uri("https://geo.iotech.pt");
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Savvori/1.0");
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<ILocationService, GeoApiLocationService>();
builder.Services.AddScoped<IShoppingOptimizer, ShoppingOptimizer>();

// --- Quartz scheduler ---
builder.Services.AddQuartz(q =>
{
    // Jobs are registered per StoreChain slug.
    // Each active store chain gets two daily trigger: 06:00 and 18:00 UTC.
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
    await db.Database.MigrateAsync();
    await CategorySeeder.SeedAsync(db, app.Logger);
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
    public bool Enabled { get; set; } = true;
    public bool ScrapeLocations { get; set; } = true;
    public string? MorningCron { get; set; }
    public string? EveningCron { get; set; }
}
