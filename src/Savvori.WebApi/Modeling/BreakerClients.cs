using Savvori.Shared;
namespace Savvori.WebApi.Modeling;

/// <summary>
/// Runs a model call under the circuit breaker: refuses while open (without counting that as a new failure),
/// records transport failures and successes. A <see cref="ModelResponseException"/> proves the server is
/// reachable, so it counts as a success for the breaker.
/// </summary>
internal static class BreakerGuard
{
    public static async Task<T> RunAsync<T>(ModelCircuitBreaker breaker, Func<Task<T>> call)
    {
        if (!breaker.TryAcquire())
            throw new ModelUnavailableException("Model circuit breaker is open.");
        try
        {
            var result = await call();
            breaker.RecordSuccess();
            return result;
        }
        catch (ModelUnavailableException ex)
        {
            breaker.RecordFailure(ex.Message);
            throw;
        }
        catch (ModelResponseException ex)
        {
            breaker.RecordReachableButBad(ex.Message);
            throw;
        }
    }
}

/// <summary>The <see cref="IEmbeddingClient"/> the rest of the app sees: every call goes through the breaker.</summary>
public sealed class BreakerEmbeddingClient(IEmbeddingClient inner, ModelCircuitBreaker breaker) : IEmbeddingClient
{
    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
        BreakerGuard.RunAsync(breaker, () => inner.EmbedAsync(texts, ct));

    public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct = default) =>
        BreakerGuard.RunAsync(breaker, () => inner.GetModelInfoAsync(ct));

    public Task PingAsync(CancellationToken ct = default) =>
        BreakerGuard.RunAsync(breaker, async () => { await inner.PingAsync(ct); return true; });
}

/// <summary>The <see cref="IPairJudge"/> the rest of the app sees: every call goes through the breaker.</summary>
public sealed class BreakerPairJudge(IPairJudge inner, ModelCircuitBreaker breaker) : IPairJudge
{
    public Task<JudgeVerdict> JudgeAsync(JudgeItem a, JudgeItem b, CancellationToken ct = default) =>
        BreakerGuard.RunAsync(breaker, () => inner.JudgeAsync(a, b, ct));
}

/// <summary>The <see cref="ICategoryJudge"/> the rest of the app sees: every call goes through the breaker.</summary>
public sealed class BreakerCategoryJudge(ICategoryJudge inner, ModelCircuitBreaker breaker) : ICategoryJudge
{
    public Task<JudgeVerdict> JudgeAsync(CategoryJudgeItem item, CancellationToken ct = default) =>
        BreakerGuard.RunAsync(breaker, () => inner.JudgeAsync(item, ct));
}
