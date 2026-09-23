using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

/// <summary>Answers from a name -> (language -> keywords) table; unknown products get empty lists. Counts requests.</summary>
public sealed class FakeProductTranslator : IProductTranslator
{
    public Dictionary<string, Dictionary<string, string[]>> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Calls { get; private set; }
    public List<int> BatchSizes { get; } = [];

    public Task<IReadOnlyList<TranslateResult>> TranslateAsync(IReadOnlyList<TranslateItem> items, CancellationToken ct = default)
    {
        Calls++;
        BatchSizes.Add(items.Count);
        IReadOnlyList<TranslateResult> results = items.Select(i =>
        {
            var langs = Answers.GetValueOrDefault(i.Name) ?? [];
            return new TranslateResult(new[] { "pt", "en", "es", "fr" }.ToDictionary(
                l => l, l => (IReadOnlyList<string>)(langs.GetValueOrDefault(l) ?? [])));
        }).ToList();
        return Task.FromResult(results);
    }
}

public sealed class FlakyProductTranslator(IProductTranslator inner, FaultPlan plan) : IProductTranslator
{
    public async Task<IReadOnlyList<TranslateResult>> TranslateAsync(IReadOnlyList<TranslateItem> items, CancellationToken ct = default)
    {
        await plan.ApplyAsync(ct);
        return await inner.TranslateAsync(items, ct);
    }
}
