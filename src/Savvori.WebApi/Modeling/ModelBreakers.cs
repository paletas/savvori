using Microsoft.Extensions.Options;

namespace Savvori.WebApi.Modeling;

/// <summary>One <see cref="ModelCircuitBreaker"/> per backend, so an outage of one does not stop work on the other.</summary>
public sealed class ModelBreakers(IOptions<ModelOptions> options, TimeProvider time)
{
    private readonly ModelCircuitBreaker _gateway = new(options, time);
    private readonly ModelCircuitBreaker _openRouter = new(options, time);

    public ModelCircuitBreaker For(ModelBackend b) => b == ModelBackend.Gateway ? _gateway : _openRouter;

    public IEnumerable<(ModelBackend Backend, ModelCircuitBreaker Breaker)> All
    {
        get
        {
            yield return (ModelBackend.Gateway, _gateway);
            yield return (ModelBackend.OpenRouter, _openRouter);
        }
    }
}
