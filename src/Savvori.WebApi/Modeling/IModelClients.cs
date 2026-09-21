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

public enum JudgeVerdict { Yes, No, Unclear }

public interface IPairJudge
{
    /// <summary>
    /// Are these the same product? A transport failure throws <see cref="ModelUnavailableException"/>;
    /// it is never reported as <see cref="JudgeVerdict.No"/>.
    /// </summary>
    Task<JudgeVerdict> JudgeAsync(JudgeItem a, JudgeItem b, CancellationToken ct = default);
}
