using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// The model backend could not be reached or answered with a transport-level failure
/// (connect error, timeout, 5xx). Counts against the circuit breaker; never means "no" to a judge.
/// </summary>
public sealed class ModelUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The backend answered but the response was unusable (bad shape, wrong count). Not a breaker failure.</summary>
public sealed class ModelResponseException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <param name="ModelName">Model that produced the vectors.</param>
/// <param name="ModelDigest">Version fingerprint of that model; vectors from different digests must never be compared.</param>
public sealed record EmbeddingResult(
    IReadOnlyList<float[]> Vectors, string ModelName, string ModelDigest, int Dimension);

/// <summary>Identity of the embedding model currently served: vectors are only comparable within one identity.</summary>
public sealed record ModelInfo(string ModelName, string ModelDigest);

public interface IEmbeddingClient
{
    /// <summary>Which model (name + digest) is being served right now. Throws <see cref="ModelUnavailableException"/> when unknown.</summary>
    Task<ModelInfo> GetModelInfoAsync(CancellationToken ct = default);

    /// <summary>Embeds the texts in order. Throws <see cref="ModelUnavailableException"/> on transport failure.</summary>
    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>Cheap health check used to close the circuit breaker. Throws when the backend is down.</summary>
    Task PingAsync(CancellationToken ct = default);
}

/// <summary>Only product text (no user data) is ever sent to the judge.</summary>
public sealed record JudgeItem(string Name, string? Brand, string? Size, string Chain);


public interface IPairJudge
{
    /// <summary>
    /// Are these the same product? A transport failure throws <see cref="ModelUnavailableException"/>;
    /// it is never reported as <see cref="JudgeVerdict.No"/>.
    /// </summary>
    Task<JudgeVerdict> JudgeAsync(JudgeItem a, JudgeItem b, CancellationToken ct = default);
}

/// <summary>Only product text (no user data) is ever sent to the judge.</summary>
public sealed record CategoryJudgeItem(string Name, string? Brand, string? StoreCategory, string SuggestedCategory);

public interface ICategoryJudge
{
    /// <summary>
    /// Does this product genuinely belong in the suggested category (not just share a word with it)? A transport
    /// failure throws <see cref="ModelUnavailableException"/>; it is never reported as <see cref="JudgeVerdict.No"/>.
    /// </summary>
    Task<JudgeVerdict> JudgeAsync(CategoryJudgeItem item, CancellationToken ct = default);
}

/// <summary>Only product text (no user data) is ever sent to the translator.</summary>
public sealed record TranslateItem(string Name, string? Brand, string? Category);

/// <summary>Generic names/keywords per language (pt, en, es, fr) for one product; a language may be empty.</summary>
public sealed record TranslateResult(IReadOnlyDictionary<string, IReadOnlyList<string>> Keywords);

public interface IProductTranslator
{
    /// <summary>
    /// Suggests generic names and search keywords per language for each product, in the same order as the input.
    /// A transport failure throws <see cref="ModelUnavailableException"/>; an unusable answer (wrong count, bad shape)
    /// throws <see cref="ModelResponseException"/>.
    /// </summary>
    Task<IReadOnlyList<TranslateResult>> TranslateAsync(IReadOnlyList<TranslateItem> items, CancellationToken ct = default);
}
