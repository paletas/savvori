using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Savvori.WebApi.Modeling;

public static class ModelServiceCollectionExtensions
{
    public const string HttpClientName = "model";

    /// <summary>
    /// Registers the model backend. Safe with the feature flag off: nothing calls the model, and the clients
    /// are only constructed (never invoked) when something resolves them.
    /// </summary>
    public static IServiceCollection AddModelServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ModelOptions>(configuration.GetSection(ModelOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ModelCircuitBreaker>();
        services.AddSingleton<ModelTelemetry>();
        services.AddHostedService<ModelTelemetrySampler>();

        // RemoveAllResilienceHandlers is flagged experimental but is the only way to opt one client out of ConfigureHttpClientDefaults.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(HttpClientName, (sp, c) =>
            {
                var o = sp.GetRequiredService<IOptions<ModelOptions>>().Value;
                if (Uri.TryCreate(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/", UriKind.Absolute, out var uri))
                    c.BaseAddress = uri;
                c.Timeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
            })
            // ServiceDefaults adds a standard resilience handler (retries, 30s total timeout) to every client.
            // Model calls must not retry behind our back: retries belong to the queue, timeouts to ModelOptions.
            .RemoveAllResilienceHandlers()
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<ModelOptions>>().Value.ConnectTimeoutSeconds),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
#pragma warning restore EXTEXP0001

        services.AddSingleton<IEmbeddingClient>(sp => new BreakerEmbeddingClient(
            new OllamaEmbeddingClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                sp.GetRequiredService<IOptions<ModelOptions>>(),
                sp.GetRequiredService<TimeProvider>()),
            sp.GetRequiredService<ModelCircuitBreaker>()));
        services.AddSingleton<IPairJudge>(sp => new BreakerPairJudge(
            new OllamaPairJudge(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                sp.GetRequiredService<IOptions<ModelOptions>>()),
            sp.GetRequiredService<ModelCircuitBreaker>()));
        services.AddSingleton<ICategoryJudge>(sp => new BreakerCategoryJudge(
            new OllamaCategoryJudge(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                sp.GetRequiredService<IOptions<ModelOptions>>()),
            sp.GetRequiredService<ModelCircuitBreaker>()));

        services.AddScoped<ModelJobQueue>();
        services.AddSingleton<CurrentModelState>();
        services.AddSingleton<EmbeddingIndex>();
        services.AddScoped<EmbeddingScanner>();
        services.AddScoped<CandidateGenerator>();
        services.AddScoped<IModelJobHandler, EmbedJobHandler>();
        services.AddScoped<IModelJobHandler, JudgeJobHandler>();
        services.AddScoped<MatchApplier>();
        services.AddScoped<MatchingService>();
        services.AddScoped<CategoryClassifier>();
        services.AddSingleton<BulkRunner>();
        services.AddScoped<MatchBulkService>();
        services.AddScoped<CategoryBulkService>();
        services.AddScoped<IStaleEmbeddingSource, DbStaleEmbeddingSource>();
        services.AddScoped<IModelStatusService, ModelStatusService>();
        return services;
    }
}
