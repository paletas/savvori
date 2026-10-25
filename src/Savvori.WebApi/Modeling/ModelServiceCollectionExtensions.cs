using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Savvori.WebApi.Modeling;

public static class ModelServiceCollectionExtensions
{
    public const string GatewayHttpClientName = "gateway";
    public const string OpenRouterHttpClientName = "openrouter";

    /// <summary>
    /// Registers the model backend. Safe with the feature flag off: nothing calls the model, and the clients
    /// are only constructed (never invoked) when something resolves them.
    /// </summary>
    public static IServiceCollection AddModelServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ModelOptions>(configuration.GetSection(ModelOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ModelBreakers>();
        services.AddSingleton<ModelTelemetry>();
        services.AddSingleton<IModelCallRecorder, ModelCallRecorder>();
        services.AddHostedService<ModelTelemetrySampler>();

        // RemoveAllResilienceHandlers is flagged experimental but is the only way to opt one client out of ConfigureHttpClientDefaults.
#pragma warning disable EXTEXP0001
        AddModelHttpClient(services, GatewayHttpClientName, o => (o.Gateway.BaseUrl, o.Gateway.ApiKey));
        AddModelHttpClient(services, OpenRouterHttpClientName, o => (o.OpenRouter.BaseUrl, o.OpenRouter.ApiKey));
#pragma warning restore EXTEXP0001

        // One limiter for every gateway caller: the server counts requests per source IP.
        services.AddSingleton(sp => new GatewayThrottle(
            sp.GetRequiredService<IOptions<ModelOptions>>(), sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IEmbeddingClient>(sp => new BreakerEmbeddingClient(
            new GatewayEmbeddingClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(GatewayHttpClientName),
                sp.GetRequiredService<IOptions<ModelOptions>>(),
                sp.GetRequiredService<GatewayThrottle>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IModelCallRecorder>()),
            sp.GetRequiredService<ModelBreakers>().For(ModelBackend.Gateway)));
        services.AddSingleton<IPairJudge>(sp => new BreakerPairJudge(
            new JevPairJudge(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenRouterHttpClientName),
                sp.GetRequiredService<IOptions<ModelOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IModelCallRecorder>()),
            sp.GetRequiredService<ModelBreakers>().For(ModelBackend.OpenRouter)));
        services.AddSingleton<ICategoryJudge>(sp => new BreakerCategoryJudge(
            new GatewayCategoryJudge(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(GatewayHttpClientName),
                sp.GetRequiredService<IOptions<ModelOptions>>(),
                sp.GetRequiredService<GatewayThrottle>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IModelCallRecorder>()),
            sp.GetRequiredService<ModelBreakers>().For(ModelBackend.Gateway)));

        services.AddSingleton<IProductTranslator>(sp => new BreakerProductTranslator(
            new GatewayProductTranslator(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(GatewayHttpClientName),
                sp.GetRequiredService<IOptions<ModelOptions>>(),
                sp.GetRequiredService<GatewayThrottle>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IModelCallRecorder>()),
            sp.GetRequiredService<ModelBreakers>().For(ModelBackend.Gateway)));

        services.AddSingleton<ThrottleTracker>();
        services.AddSingleton<IModelProbe>(sp => new EmbeddingPingProbe(sp.GetRequiredService<IEmbeddingClient>()));
        services.AddSingleton<IModelProbe>(sp => new OpenRouterKeyProbe(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenRouterHttpClientName),
            sp.GetRequiredService<ModelBreakers>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IModelCallRecorder>()));

        services.AddScoped<ModelJobQueue>();
        services.AddSingleton<CurrentModelState>();
        services.AddSingleton<EmbeddingIndex>();
        services.AddScoped<EmbeddingScanner>();
        services.AddScoped<CandidateGenerator>();
        services.AddScoped<IModelJobHandler, EmbedJobHandler>();
        services.AddScoped<IModelJobHandler, JudgeJobHandler>();
        services.AddScoped<AliasScanner>();
        services.AddScoped<IModelJobHandler, TranslateJobHandler>();
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

#pragma warning disable EXTEXP0001
    private static void AddModelHttpClient(
        IServiceCollection services, string name, Func<ModelOptions, (string BaseUrl, string ApiKey)> select)
    {
        services.AddHttpClient(name, (sp, c) =>
            {
                var o = sp.GetRequiredService<IOptions<ModelOptions>>().Value;
                var (baseUrl, apiKey) = select(o);
                if (Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var uri))
                    c.BaseAddress = uri;
                c.Timeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
                if (!string.IsNullOrEmpty(apiKey))
                    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            })
            // ServiceDefaults adds a standard resilience handler (retries, 30s total timeout) to every client.
            // Model calls must not retry behind our back: retries belong to the queue, timeouts to ModelOptions.
            .RemoveAllResilienceHandlers()
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<ModelOptions>>().Value.ConnectTimeoutSeconds),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
    }
#pragma warning restore EXTEXP0001
}
