namespace Savvori.WebApi.Modeling;

/// <summary>A cheap health check for one backend, used by the drain job while that backend's breaker is open.</summary>
public interface IModelProbe
{
    ModelBackend Backend { get; }
    Task PingAsync(CancellationToken ct);
}

/// <summary>Gateway probe: the (breaker-wrapped) embedding client's ping, a <c>GET models</c> through the shared throttle.</summary>
public sealed class EmbeddingPingProbe(IEmbeddingClient embeddings) : IModelProbe
{
    public ModelBackend Backend => ModelBackend.Gateway;

    public Task PingAsync(CancellationToken ct) => embeddings.PingAsync(ct);
}

/// <summary>OpenRouter probe: <c>GET api/v1/key</c> under the OpenRouter breaker.</summary>
public sealed class OpenRouterKeyProbe(HttpClient http, ModelBreakers breakers, TimeProvider time) : IModelProbe
{
    public ModelBackend Backend => ModelBackend.OpenRouter;

    public Task PingAsync(CancellationToken ct) =>
        BreakerGuard.RunAsync(breakers.For(ModelBackend.OpenRouter), async () =>
        {
            (await ModelHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, "api/v1/key"), null, time, ct)).Dispose();
            return true;
        });
}
